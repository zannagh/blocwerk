/*
 * The offline gym client's half of the worker (plan 2026-10-22 §1, PR 3.9), loaded into service-worker.js by
 * importScripts after sw-asset-cache.js, whose partition and state cache it reads.
 *
 * PRECACHE (sw-client-fill.js, imported after this file). A Server page posts `client-precache` once its core fill is
 * done (client-precache.js: not with Save-Data, not on a kiosk, once per client version). The worker then fetches the rendered shell `/v/` of the deploy it reaches,
 * reads the version and the declared routes from the shell's metas, and fills `blocwerk-client-{version}` with every
 * file the shell names (its scripts and stylesheets, the import map's runtime modules) and every eagerly loaded file of
 * dotnet.js's boot manifest, then the shell itself. Every one of those URLs is fingerprinted, so a URL names one set of
 * bytes and one version never mixes with another. Only then is `{version, routes}` written to `bw-sw-state`
 * (CLIENT_STATE_KEY): the completeness record, and the older versions are deleted. A fill that fails anywhere writes no
 * record; the next request resumes it (what is already in that version's cache is not fetched again).
 *
 * HAND-OFF. A Server-page navigation that fails (no response, a 502/503/504, or none within CLIENT_HANDOFF_MS) is redirected to
 * the client (`/v` + path + query) when clientHandoff allows it; otherwise the navigation keeps today's answer (the
 * network's, or offline.html when there is none). A slow server that answers is never cut off unless the client can
 * take over: the timer only ever adds the redirect, it never ends the wait on its own.
 *
 * WHO MAY BE HANDED OVER (clientServable): a complete version whose shell is really in its cache (a record alone is not
 * enough: the browser, or a fill racing a deletion, can leave a record without its cache), no locked partition on the
 * device (the 401 lock: a locked partition is never shown), and a last known partition that is a person's (`u-…`) or
 * `anon`. A kiosk (`k-…`) is refused until PR 3.15, and `none` or no label has nothing offline to show. Paths that are
 * auth, kiosk, dev, admin or the client itself (CLIENT_NEVER) are never handed over, whatever a route says.
 *
 * DEPLOYS. A deploy is announced to the pages about 5 s before the restart; the page posts `deploy-pending` and the
 * worker holds every hand-off until `deploy-done` (the page saw the new instance) or CLIENT_DEPLOY_MAX_MS, so a deploy
 * keeps today's answers (offline.html, or the proxy's error). An outage nobody announced still hands over.
 *
 * Old versions are deleted only by a fill that completes a newer one (commitClient), never by activate: an activate
 * could otherwise delete a version another worker is still filling.
 *
 * The client's files are answered cache-first from the client caches, for a /v/ page also its /_content/ and /js/ files:
 * the server may be down or hanging, and those files are immutable under their names.
 */
const CLIENT_CACHE_PREFIX = 'blocwerk-client-';
const CLIENT_STATE_KEY = '/__bw/client';
const CLIENT_SHELL_PATH = '/v/';
const CLIENT_HANDOFF_MS = 4000;
const CLIENT_VERSION = /^[0-9a-f]{16}$/;
const CLIENT_FILL_PARALLEL = 4;
// An announced deploy (maintenance.js, via sw-guard.js) holds the hand-off for at most this long: the 502s of a restart
// are not an outage. Persisted with its end in bw-sw-state, so a restarted worker keeps it, and never longer than this.
const CLIENT_DEPLOY_KEY = '/__bw/deploy';
const CLIENT_DEPLOY_MAX_MS = 3 * 60 * 1000;
// Only a proxy's "the app is not there" hands over; a 500 is the app's own answer.
const CLIENT_HANDOFF_STATUS = [502, 503, 504];
// First segments never handed to the client: sign-in, sign-out and OAuth flows, account and settings, kiosk pairing,
// the dev and admin tools, and /v itself.
const CLIENT_NEVER = /^\/(?:account|signin[^/]*|signout[^/]*|signing-in|signed-out|signup|login|logout|oauth[^/]*|forgot-password|reset-password|access-denied|join|kiosk|dev|settings|administration|v)(?:\/|$)/i;
// The boot manifest's eager resources (dotnet.js, between /*json-start*/ and /*json-end*/); build/check-client-bundle.mjs
// reads the same kinds. Lazy assemblies, satellites and symbols are loaded on demand only, so they are left out.
const CLIENT_BOOT_KINDS = ['jsModuleNative', 'jsModuleRuntime', 'jsModuleWorker', 'wasmNative', 'icu', 'coreAssembly', 'assembly'];

const clientState = { record: null, read: null, filling: null };

// The complete version's record `{ version, routes }`, or null. `fresh` reads the state cache again.
function completeClient(fresh) {
    if (fresh || !clientState.read) {
        clientState.read = caches.match(CLIENT_STATE_KEY, { cacheName: ASSET_STATE_CACHE })
            .then(stored => (stored ? stored.json() : null))
            .then(record => {
                const valid = record && CLIENT_VERSION.test(record.version) && Array.isArray(record.routes);
                clientState.record = valid ? { version: record.version, routes: record.routes.filter(r => typeof r === 'string') } : null;
                return clientState.record;
            })
            .catch(() => clientState.record);
    }
    return clientState.read;
}

const clientCacheName = version => CLIENT_CACHE_PREFIX + version;
const clientUrl = path => new URL(path, self.location.origin).href;
const clientFileOk = response => !!response && response.status === 200 && response.type === 'basic' && !response.redirected;

// True when a template (relative to /v/, `{name}` one segment, `{*name}` the rest) covers the path.
function clientRouteCovers(template, pathname) {
    const want = template.split('/').filter(Boolean);
    const have = pathname.split('/').filter(Boolean);
    for (let i = 0; i < want.length; i++) {
        if (/^\{\*{1,2}[^}]+\}$/.test(want[i])) {
            return true;
        }
        if (i >= have.length || !(/^\{[^}]+\}$/.test(want[i]) || want[i].toLowerCase() === have[i].toLowerCase())) {
            return false;
        }
    }
    return want.length === have.length;
}

function clientServable(record, partition, locked) {
    return !!record && locked === null && (partition === 'anon' || /^u-[a-z2-7]{20}$/.test(partition || ''));
}

// What may serve the client right now: the complete record when the device's state allows it, else null.
// The complete record when its version's shell is in its cache, else null.
function presentClient() {
    return completeClient(false)
        .then(record => (record
            ? caches.match(clientUrl(CLIENT_SHELL_PATH), { cacheName: clientCacheName(record.version) }).then(hit => (hit ? record : null))
            : null))
        .catch(() => null);
}

// True while an announced deploy holds the hand-off. Read from the state cache each time (failures are rare): a worker
// restarted mid-deploy keeps it, and an end in the past, unreadable, or further away than the maximum (a clock that
// jumped back) ends it at once, so it can never stay on.
function deployHolding() {
    return caches.match(CLIENT_DEPLOY_KEY, { cacheName: ASSET_STATE_CACHE })
        .then(stored => (stored ? stored.text() : null))
        .then(text => {
            if (text === null) {
                return false;
            }
            const until = Number(text);
            const now = Date.now();
            if (Number.isFinite(until) && until > now && until <= now + CLIENT_DEPLOY_MAX_MS) {
                return true;
            }
            return endDeployHold().then(() => false);
        })
        .catch(() => false);
}

function holdForDeploy() {
    return caches.open(ASSET_STATE_CACHE)
        .then(cache => cache.put(CLIENT_DEPLOY_KEY, new Response(String(Date.now() + CLIENT_DEPLOY_MAX_MS))))
        .catch(() => undefined);
}

function endDeployHold() {
    return caches.open(ASSET_STATE_CACHE).then(cache => cache.delete(CLIENT_DEPLOY_KEY)).catch(() => undefined);
}

function servableClient() {
    return Promise.all([presentClient(), lastKnownPartition(), lockedPartition(true)])
        .then(([record, partition, locked]) => (clientServable(record, partition, locked) ? record : null))
        .catch(() => null);
}

// The client URL that takes over a failed navigation to `url`, or null.
function clientHandoff(url) {
    if (CLIENT_NEVER.test(url.pathname)) {
        return Promise.resolve(null);
    }
    return Promise.all([servableClient(), deployHolding()]).then(([record, holding]) => {
        const covered = !holding && !!record && record.routes.some(route => clientRouteCovers(route, url.pathname));
        return covered ? `/v${url.pathname}${url.search}` : null;
    });
}

const clientRedirect = path => Response.redirect(new URL(path, self.location.origin).href, 302);

// A Server-page navigation: the network's answer, unless it fails and the client can take over (see HAND-OFF).
function navigateWithHandoff(request, offlinePage) {
    const url = new URL(request.url);
    let target = null;
    const handoff = () => target || (target = clientHandoff(url).catch(() => null));
    return new Promise(resolve => {
        const timer = setTimeout(() => handoff().then(path => {
            if (path) {
                resolve(clientRedirect(path));
            }
        }), CLIENT_HANDOFF_MS);
        const settle = answer => {
            clearTimeout(timer);
            resolve(answer);
        };
        fetch(request).then(
            response => (CLIENT_HANDOFF_STATUS.includes(response.status) ? handoff().then(path => settle(path ? clientRedirect(path) : response)) : settle(response)),
            () => handoff().then(path => settle(path ? clientRedirect(path) : offlinePage())));
    });
}

// A /v/ navigation: the complete version's shell when the client may be served, else the network (offline.html when
// it fails). The shell's base is /v/, so this one copy opens every client route.
function respondWithClientShell(request, offlinePage) {
    return servableClient()
        .then(record => (record ? caches.match(clientUrl(CLIENT_SHELL_PATH), { cacheName: clientCacheName(record.version) }) : undefined))
        .catch(() => undefined)
        .then(cached => cached || fetch(request).catch(() => offlinePage()));
}

// True for a request the client caches answer first: the client's own files, and the shared files a /v/ page loads.
function isClientFile(url, request) {
    if (url.pathname.startsWith(CLIENT_SHELL_PATH)) {
        return true;
    }
    const fromClient = typeof request.referrer === 'string' && request.referrer.startsWith(self.location.origin + CLIENT_SHELL_PATH);
    return fromClient && (url.pathname.startsWith('/_content/') || url.pathname.startsWith('/js/'));
}

function respondWithClientFile(request) {
    return caches.keys()
        .then(names => names.filter(name => name.startsWith(CLIENT_CACHE_PREFIX)))
        .then(names => names.reduce((found, cacheName) => found.then(hit => hit
            || caches.match(request.url, { cacheName, ignoreVary: true })), Promise.resolve(undefined)))
        .catch(() => undefined)
        // A failed network leg still finds a copy the shell cache keeps (/js/, /_content/), as the own-asset rule does.
        .then(hit => hit || fetch(request).catch(error => caches.match(request).then(copy => copy || Promise.reject(error))));
}

completeClient(false);
