/*
 * Blocwerk service worker.
 *
 * HONEST SCOPE — this is a Blazor *Server* app. A service worker CANNOT render server pages
 * offline: every interactive page is produced by the server over SignalR, so there is nothing to
 * cache for them. What it does buy us:
 *   1. The static shell (CSS/JS/manifest/icons) is cached, so an installed PWA opens instantly and
 *      its chrome renders even with no network. Only the stable-path files are precached on
 *      install; the fingerprinted CSS/JS join the cache on the first online load (see below).
 *   2. The queued /api/offline/* POSTs can be attempted the moment the SW is up, independent of
 *      the SignalR circuit — though those requests are ALWAYS network-only here; the queue owns
 *      their retry/idempotency, and a cached POST would be meaningless.
 *   3. Navigations that fail offline fall back to a tidy /offline.html instead of the browser's
 *      dinosaur, or (PR 3.9) go to the offline gym client under /v/ when it is precached and covers the
 *      route: the offline client section below.
 *
 * NEVER cached: anything under /api/* and /_blazor/* (SignalR), and any non-GET request. Those are
 * strictly network-only so auth, the circuit and the offline queue always talk to the live server.
 * The one exception is the content-addressed wall assets (/api/walls/{w}/a/{sha}[/{width}]): immutable bytes,
 * cache-first per identity partition, in sw-asset-cache.js.
 *
 * Page <-> worker messages carry `proto` (PROTO below). Both sides ignore a message whose proto or type they
 * do not know, and the worker never depends on the page talking to it: skipWaiting + clients.claim means a new
 * worker can end up serving an older page's JS (and the reverse), and that pairing must stay harmless.
 *
 * NICE-TO-HAVE not implemented: the Background Sync API could flush the queue when the tab is
 * closed. The baseline here is the existing `online` event + circuit-reconnect flush in
 * offline-queue.js / blazor-boot.js, which covers the tab-open case; Background Sync would only add
 * closed-tab flushing and needs its own IndexedDB replay in the SW, so it is deferred.
 */
const CACHE = 'blocwerk-shell-v5';
const PROTO = 1;

// ONE imported script, as before PR 3.9: on Linux WebKit (WPE) every further importScripts made the first install
// hang now and then (the page's `serviceWorker.ready` never resolved: 6 of 50 runs with four imports, 1 of 50 with
// two, 0 of 48 with one, 0 of 50 with this code inline). So the offline client's half and push live in this file,
// above 300 lines on purpose.
importScripts('/js/sw-asset-cache.js');

// The worker's half of the 401 lock (Phase 3 PR 3.5): a `u-` partition the page locked after its session expired
// (asset-lock.js) is kept for the same user's return but never served. The page writes the locked label to
// `bw-sw-state` (ASSET_LOCK_KEY) and removes it on unlock or wipe; respondFromAssetCache and storeAsset
// (sw-asset-cache.js) never serve from, nor store into, a partition of that label. Online the served label is the
// cookie's, never the locked one, so this is a second guard there; on the OFFLINE path (the last known label) it is the
// only one, so that path reads the marker fresh, while the online path uses the copy read once per worker start and
// again on every `sw-refresh-partition`. Here, not in an imported file of its own: a worker that WebKit restarts with
// the network down must never lack it.
const ASSET_LOCK_KEY = '/__bw/locked';

const assetLock = { label: null, read: null };

// The locked label, or null. `fresh` reads the marker again instead of the copy.
function lockedPartition(fresh) {
    if (fresh || !assetLock.read) {
        assetLock.read = caches.match(ASSET_LOCK_KEY, { cacheName: ASSET_STATE_CACHE })
            .then(stored => (stored ? stored.text() : null))
            .then(label => {
                assetLock.label = typeof label === 'string' && label ? label : null;
                return assetLock.label;
            })
            .catch(() => assetLock.label);
    }
    return assetLock.read;
}

// The next request reads the marker again (a page changed the lock).
function forgetLockedPartition() {
    assetLock.read = null;
}

// True when `label` must not be served or stored: it is the locked partition.
function isLockedPartition(label, fresh) {
    return lockedPartition(fresh).then(locked => locked !== null && locked === label);
}


// Stable-path assets only. The app's own CSS/JS are referenced from BlocwerkApp.razor through
// @Assets[...], so the page requests them under a build-specific fingerprinted name that this file
// cannot know. They are picked up by the runtime cache below on the first online load instead, and
// listing their unfingerprinted names here would only cache copies nothing ever asks for.
const PRECACHE = [
    '/offline.html',
    '/manifest.webmanifest',
    '/icons/icon-192.png',
    '/icons/icon-512.png'
];

self.addEventListener('install', event => {
    event.waitUntil(
        caches.open(CACHE)
            // addAll is atomic; tolerate a missing optional asset so install never wedges.
            .then(cache => Promise.allSettled(PRECACHE.map(url => cache.add(url))))
            .then(() => self.skipWaiting())
    );
});

self.addEventListener('activate', event => {
    event.waitUntil(
        caches.keys()
            // Only this worker's own older shells, the retired asset-cache schema 'bw-a0-*', and the storage
            // probe's size-ladder scratch (up to 1.5 GB a crashed run can leave behind). Every other cache on
            // the origin (the probe's markers in 'bw-storage-probe', on-device wall data in 'bw-a1-*') is not
            // the shell's to delete. Keeping 'bw-a1-*' here, before anything writes it, is what makes rolling
            // back to this worker or any later one safe once the asset cache ships. The offline client's
            // 'blocwerk-client-*' are not this handler's either: only a completed fill deletes old versions.
            .then(keys => Promise.all(keys.filter(isDisposableCache).map(k => caches.delete(k))))
            .then(() => self.clients.claim())
    );
});

function isDisposableCache(name) {
    return (name.startsWith('blocwerk-shell-') && name !== CACHE)
        || name.startsWith('bw-a0-')
        || name === 'bw-storage-probe-ladder';
}

// The messages this protocol version answers: a page asking which worker it is talking to, asking it to look the
// partition up again, or (PR 3.9) to precache the offline client or hold its hand-off for a deploy. The reply goes to
// the sender's MessagePort when it passed one, otherwise back to the sending client.
self.addEventListener('message', event => {
    const data = event.data;
    if (!data || typeof data !== 'object' || data.proto !== PROTO) {
        return;
    }
    if (data.type === 'sw-info') {
        // `partition`: the label the asset cache serves right now (sw-asset-cache.js), null before any lookup.
        // `client`: the offline client's complete version whose shell is in its cache, else null. Read before answering,
        // so a worker that just started never reports null for a version it has.
        lifetime(event, presentClient().then(record => reply(event,
            { proto: PROTO, type: 'sw-info', shell: CACHE, partition: assetPartitions.current, client: record ? record.version : null })));
    } else if (data.type === 'deploy-pending' || data.type === 'deploy-done') {
        // An announced deploy holds the offline client's hand-off until the new instance is seen (the offline client section).
        lifetime(event, data.type === 'deploy-pending' ? holdForDeploy() : endDeployHold());
    } else if (data.type === 'client-target') {
        // PR 3.14: where "Continue offline" goes for the page's path, the hand-off rules without the deploy hold (the user
        // asked), `target` null when the client cannot take it.
        lifetime(event, clientTarget(data.path).then(target => reply(event, { proto: PROTO, type: 'client-target', target })));
    } else if (data.type === 'client-precache') {
        lifetime(event, precacheClient().then(result => reply(event, { proto: PROTO, type: 'client-precached', ...result })));
    } else if (data.type === 'sw-refresh-partition') {
        // Answers `partition-refreshed` with the label served once the lookup is done, so the page can fill at once.
        const clientId = event.source && event.source.id;
        const done = refreshPartition(clientId)
            .then(() => reply(event, { proto: PROTO, type: 'partition-refreshed', partition: assetPartitions.current }))
            .catch(() => undefined);
        lifetime(event, done);
    }
});

function lifetime(event, promise) {
    if (typeof event.waitUntil === 'function') {
        event.waitUntil(promise);
    }
}

function reply(event, message) {
    const target = (event.ports && event.ports[0]) || event.source;
    if (target && typeof target.postMessage === 'function') {
        target.postMessage(message);
    }
}

function isBypassed(url, request) {
    if (request.method !== 'GET') {
        return true;
    }
    // Live server only: the offline queue, auth and the SignalR circuit must never be intercepted.
    return url.pathname.startsWith('/api/') || url.pathname.startsWith('/_blazor');
}

// Our own CSS/JS/icons, and the shared UI library's (/_content/Blocwerk.Shared.Ui/). The fingerprinted
// ones are immutable under a given name, and the ones that keep a stable path (icons, the manifest)
// change on every deploy, so network-first is right for both: it can never let a stale cache mask a
// fresh deploy — the exact trap that made a re-composed image still serve the old front-end — and for
// a fingerprinted URL the `immutable` response is already in the browser's HTTP cache, so the
// "network" leg costs no round trip.
function isOwnAsset(url) {
    return url.pathname.startsWith('/css/')
        || url.pathname.startsWith('/js/')
        || url.pathname.startsWith('/_content/')
        || url.pathname.startsWith('/icons/')
        || url.pathname === '/manifest.webmanifest';
}

function fetchAndCache(request) {
    return fetch(request).then(response => {
        if (response && response.ok && response.type === 'basic') {
            const copy = response.clone();
            caches.open(CACHE).then(cache => cache.put(request, copy));
        }
        return response;
    });
}

self.addEventListener('fetch', event => {
    const request = event.request;
    const url = new URL(request.url);

    // Wall assets first: they live under /api/, which the bypass below would otherwise hand to the network.
    // A Range request (splats) is never cache-backed, and the worker never stores a 206.
    const asset = url.origin === self.location.origin && request.method === 'GET' ? assetRoute(url) : null;
    if (asset) {
        if (!request.headers.has('range')) {
            event.respondWith(respondFromAssetCache(event, asset));
        }
        return;
    }

    if (url.origin !== self.location.origin || isBypassed(url, request)) {
        return; // Let the network handle it untouched.
    }

    // Navigations (server-rendered): the network, falling back to the offline client or the offline page when it
    // fails (the offline client section below); a /v/ navigation is the client's shell.
    if (request.mode === 'navigate') {
        const offlinePage = () => caches.match('/offline.html');
        event.respondWith(url.pathname.startsWith(CLIENT_SHELL_PATH)
            ? respondWithClientShell(request, offlinePage)
            : navigateWithHandoff(request, offlinePage));
        return;
    }

    // The offline client's files, and what its pages load: cache-first from the client caches, then the network.
    if (isClientFile(url, request)) {
        event.respondWith(respondWithClientFile(request));
        return;
    }

    // Fingerprinted framework files never change under a given name: cache-first is safe and fast.
    if (url.pathname.startsWith('/_framework/')) {
        event.respondWith(
            caches.match(request).then(cached => cached || fetchAndCache(request))
        );
        return;
    }

    // Own shell assets: network-first so a deploy takes effect immediately, cache is the offline
    // fallback only. This is also what populates the offline shell — every fingerprinted CSS/JS
    // file the page loads is cached here on the first online visit, in place of the precache list
    // that used to name them.
    if (isOwnAsset(url)) {
        event.respondWith(
            fetchAndCache(request).catch(() => caches.match(request))
        );
    }
});

// ===== The offline client (PR 3.9) =====

/*
 * The offline gym client's half of the worker (plan 2026-10-22 §1, PR 3.9). It reads sw-asset-cache.js's partition
 * and state cache.
 *
 * PRECACHE (the fill is the section after this one). A Server page posts `client-precache` once its core fill is
 * done (client-precache.js: not with Save-Data, once per client version). The worker then fetches the rendered shell `/v/` of the deploy it reaches,
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
 * device (the 401 lock: a locked partition is never shown), and a last known partition that is a person's (`u-…`),
 * `anon`, or a kiosk tablet's (`k-…`, PR 3.15: the client then shows only the bound wall's kiosk-class data, whoever
 * the tablet acts as, and only within 7 days of its last online fill: CLIENT_KIOSK_MAX_AGE_MS). `none` or no label has
 * nothing offline to show (a kiosk that may keep nothing is `none`). Paths that are
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

// A kiosk tablet's copy is handed over only within this long of its last successful online fill (client-precache.js
// writes the stamp), as long as a `u-` lock is kept; past it, or with no stamp of its own label, it is treated like `none`.
const CLIENT_KIOSK_FRESH_KEY = '/__bw/kiosk-fresh';
const CLIENT_KIOSK_MAX_AGE_MS = 7 * 24 * 60 * 60 * 1000;
// A stamp this far ahead of the clock still counts (clock skew between page and worker is none, so this is small).
const CLIENT_KIOSK_SKEW_MS = 5 * 60 * 1000;

function clientServable(record, partition, locked, kioskFresh) {
    if (!record || locked !== null) {
        return false;
    }
    if (/^k-[a-z2-7]{20}$/.test(partition || '')) {
        return kioskFreshFor(kioskFresh, partition, Date.now());
    }
    return partition === 'anon' || /^u-[a-z2-7]{20}$/.test(partition || '');
}

function kioskFreshFor(stamp, partition, now) {
    return !!stamp && stamp.label === partition && Number.isFinite(stamp.at)
        && now - stamp.at <= CLIENT_KIOSK_MAX_AGE_MS && stamp.at - now <= CLIENT_KIOSK_SKEW_MS;
}

function kioskStamp() {
    return caches.match(CLIENT_KIOSK_FRESH_KEY, { cacheName: ASSET_STATE_CACHE })
        .then(stored => (stored ? stored.json() : null))
        .catch(() => null);
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
    return Promise.all([presentClient(), lastKnownPartition(), lockedPartition(true), kioskStamp()])
        .then(([record, partition, locked, fresh]) => (clientServable(record, partition, locked, fresh) ? record : null))
        .catch(() => null);
}

// The client URL for a Server path the client covers on this device, or null (the deploy hold aside).
function clientTarget(path) {
    let url;
    try {
        url = new URL(typeof path === 'string' && path.startsWith('/') && !path.startsWith('//') ? path : '/', self.location.origin);
    } catch (e) {
        return Promise.resolve(null);
    }
    if (CLIENT_NEVER.test(url.pathname)) {
        return Promise.resolve(null);
    }
    return servableClient()
        .then(record => (record && record.routes.some(route => clientRouteCovers(route, url.pathname)) ? `/v${url.pathname}${url.search}` : null))
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

// A Server-page navigation: the network's answer, unless it fails and the client can take over (see HAND-OFF). A
// navigation from a /v/ page (the client's hand-back, PR 3.14) gets no 4 s timer: the client saw the server answer, so a
// slow page is waited for, and a tap can never bounce straight back into the client.
function navigateWithHandoff(request, offlinePage) {
    const url = new URL(request.url);
    let target = null;
    const handoff = () => target || (target = clientHandoff(url).catch(() => null));
    const fromClient = typeof request.referrer === 'string' && request.referrer.startsWith(self.location.origin + CLIENT_SHELL_PATH);
    return new Promise(resolve => {
        const timer = fromClient ? null : setTimeout(() => handoff().then(path => {
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

// ----- The offline client's precache -----

const clientMeta = (html, name) => {
    const match = new RegExp(`<meta name="${name}" content="([^"]*)"`).exec(html);
    return match ? match[1] : null;
};

// The shell of the deploy the worker reaches: its markup, version and routes. A redirect (a kiosk session is sent
// elsewhere) or any other answer than the shell's 200 fails the fill.
function readClientShell() {
    return fetch(CLIENT_SHELL_PATH, { credentials: 'same-origin', cache: 'no-store', redirect: 'error' }).then(response => {
        if (!clientFileOk(response)) {
            throw new Error('client shell: ' + response.status);
        }
        return response.text().then(html => {
            const version = clientMeta(html, 'bw-client-version');
            const routes = clientMeta(html, 'bw-client-routes');
            if (!CLIENT_VERSION.test(version || '') || routes === null) {
                throw new Error('client shell: no version');
            }
            // Only the type: the body is decoded text now, so the network's encoding and length no longer apply.
            const headers = { 'content-type': response.headers.get('content-type') || 'text/html; charset=utf-8' };
            return { html, version, routes: routes.split(/\s+/).filter(Boolean), headers };
        });
    });
}

// Every root-relative file the shell names: script and stylesheet URLs, and the import map's modules.
function clientShellFiles(html) {
    const files = [];
    const tag = /<(?:script|link)\b[^>]*?\s(?:src|href)="(\/(?!\/)[^"]*)"/g;
    for (let match = tag.exec(html); match; match = tag.exec(html)) {
        files.push(match[1]);
    }
    const map = /<script type="importmap"[^>]*>([\s\S]*?)<\/script>/.exec(html);
    const imports = map ? (JSON.parse(map[1]).imports || {}) : {};
    files.push(...Object.values(imports).filter(url => typeof url === 'string' && /^\/(?!\/)/.test(url)));
    return { files, dotnet: imports['/v/_framework/dotnet.js'] || null };
}

// The eager files of dotnet.js's boot manifest, beside dotnet.js. An SDK that changes the layout fails the fill.
function clientBootFiles(dotnetUrl, source) {
    const start = source.indexOf('/*json-start*/');
    const end = source.indexOf('/*json-end*/');
    if (start < 0 || end < start) {
        throw new Error('client boot manifest: not found');
    }
    const resources = JSON.parse(source.slice(start + 14, end)).resources || {};
    const base = dotnetUrl.slice(0, dotnetUrl.lastIndexOf('/') + 1);
    return CLIENT_BOOT_KINDS
        .flatMap(kind => (Array.isArray(resources[kind]) ? resources[kind] : []))
        .map(resource => resource && resource.name)
        .filter(name => typeof name === 'string' && /^[\w.-]+$/.test(name))
        .map(name => base + name);
}

// One file into the version's cache, unless it is already there (a resumed fill). Resolves with its response.
function storeClientFile(cache, path) {
    const key = clientUrl(path);
    return cache.match(key).then(hit => hit || fetch(key, { credentials: 'same-origin' }).then(response => {
        if (!clientFileOk(response)) {
            throw new Error('client file: ' + path);
        }
        return cache.put(key, response.clone()).then(() => response);
    }));
}

function storeClientFiles(cache, paths) {
    const queue = [...new Set(paths)];
    const next = () => (queue.length > 0 ? storeClientFile(cache, queue.shift()).then(next) : Promise.resolve());
    return Promise.all(Array.from({ length: Math.min(CLIENT_FILL_PARALLEL, queue.length) }, next));
}

// The files first, the shell last: a version's cache holds a shell only when everything it names is there.
function fillClientVersion(cache, shell) {
    const { files, dotnet } = clientShellFiles(shell.html);
    if (!dotnet) {
        throw new Error('client shell: no dotnet.js');
    }
    return storeClientFile(cache, dotnet)
        .then(response => response.text())
        .then(source => storeClientFiles(cache, [...files, ...clientBootFiles(dotnet, source)]))
        .then(() => cache.put(clientUrl(CLIENT_SHELL_PATH), new Response(shell.html, { status: 200, headers: shell.headers })));
}

// The completeness record, then every other version goes.
function commitClient(shell) {
    const record = { version: shell.version, routes: shell.routes };
    return caches.open(ASSET_STATE_CACHE)
        .then(cache => cache.put(CLIENT_STATE_KEY, new Response(JSON.stringify(record), { headers: { 'content-type': 'application/json' } })))
        .then(() => {
            clientState.record = record;
            clientState.read = Promise.resolve(record);
            return caches.keys();
        })
        .then(names => Promise.all(names
            .filter(name => name.startsWith(CLIENT_CACHE_PREFIX) && name !== clientCacheName(record.version))
            .map(name => caches.delete(name))))
        .then(() => record.version);
}

function fillClient() {
    const connection = self.navigator && self.navigator.connection;
    if (connection && connection.saveData) {
        return Promise.reject(new Error('client precache: Save-Data'));
    }
    return Promise.all([readClientShell(), completeClient(true)])
        .then(([shell, record]) => caches.open(clientCacheName(shell.version)).then(cache => cache.match(clientUrl(CLIENT_SHELL_PATH))
            .then(present => (record && record.version === shell.version && present
                ? shell.version
                : fillClientVersion(cache, shell).then(() => commitClient(shell))))));
}

// `client-precache`: one fill at a time; resolves `{ version, complete }` (version null when it is unknown).
function precacheClient() {
    if (!clientState.filling) {
        clientState.filling = fillClient()
            .then(version => ({ version, complete: true }), () => ({ version: clientState.record ? clientState.record.version : null, complete: false }))
            .then(result => {
                clientState.filling = null;
                return result;
            });
    }
    return clientState.filling;
}

// ===== Web Push and notification taps =====

// Web Push (RFC 8291 / VAPID). The server sends a small JSON payload with lowercase fields
// (title, body, url, tag, icon); anything missing falls back to a sensible default so a payload the
// server trims can still surface a usable notification. showNotification must run inside
// waitUntil — the SW may be a one-shot wake with no page attached, and the browser terminates it the
// moment this handler's returned promise settles.
self.addEventListener('push', event => {
    let payload = {};
    if (event.data) {
        try {
            payload = event.data.json() || {};
        } catch (e) {
            // Empty or non-JSON push: fall back to a generic notice rather than dropping it.
            payload = {};
        }
    }

    const title = payload.title || 'Blocwerk';
    const options = {
        body: payload.body || '',
        icon: payload.icon || '/icons/icon-192.png',
        badge: '/icons/icon-192.png',
        tag: payload.tag,
        data: { url: payload.url || '/' }
    };

    event.waitUntil(self.registration.showNotification(title, options));
});

// Tapping a notification should land the user on its deep link, reusing an already-open Blocwerk
// window rather than stacking another one. An installed PWA is usually already open somewhere on
// some other URL, so matching on an exact URL would miss it and spawn a second window; instead we
// reuse the first window we find, steer it to the deep link, and only open a fresh one when nothing
// is open at all. Same waitUntil rule as above.
self.addEventListener('notificationclick', event => {
    event.notification.close();

    const targetUrl = (event.notification.data && event.notification.data.url) || '/';
    const targetHref = new URL(targetUrl, self.location.origin).href;

    event.waitUntil(
        self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then(clientList => {
            if (clientList.length > 0) {
                const client = clientList[0];
                // navigate() may be undefined (older engines); fall back to a plain focus so the tap
                // still surfaces an existing window instead of opening a duplicate.
                if (typeof client.navigate === 'function') {
                    return client.navigate(targetHref)
                        .then(navigated => (navigated || client).focus())
                        .catch(() => client.focus());
                }
                if ('focus' in client) {
                    return client.focus();
                }
            }
            if (self.clients.openWindow) {
                return self.clients.openWindow(targetUrl);
            }
            return undefined;
        })
    );
});
