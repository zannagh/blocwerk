/*
 * The offline gym client's half of the worker (plan 2026-10-22 §1, PR 3.9), loaded into service-worker.js by
 * importScripts after sw-asset-cache.js, whose partition and state cache it reads.
 *
 * PRECACHE. A Server page posts `client-precache` once its core fill is done (client-precache.js: not with Save-Data,
 * not on a kiosk, once per client version). The worker then fetches the rendered shell `/v/` of the deploy it reaches,
 * reads the version and the declared routes from the shell's metas, and fills `blocwerk-client-{version}` with every
 * file the shell names (its scripts and stylesheets, the import map's runtime modules) and every eagerly loaded file of
 * dotnet.js's boot manifest, then the shell itself. Every one of those URLs is fingerprinted, so a URL names one set of
 * bytes and one version never mixes with another. Only then is `{version, routes}` written to `bw-sw-state`
 * (CLIENT_STATE_KEY): the completeness record, and the older versions are deleted. A fill that fails anywhere writes no
 * record; the next request resumes it (what is already in that version's cache is not fetched again).
 *
 * HAND-OFF. A Server-page navigation that fails (no response, a 5xx, or none within CLIENT_HANDOFF_MS) is redirected to
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
    return servableClient().then(record => {
        const covered = record && record.routes.some(route => clientRouteCovers(route, url.pathname));
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
            response => (response.status >= 500 ? handoff().then(path => settle(path ? clientRedirect(path) : response)) : settle(response)),
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

// --- The precache (see PRECACHE above). ---

const clientUrl = path => new URL(path, self.location.origin).href;
const clientFileOk = response => !!response && response.status === 200 && response.type === 'basic' && !response.redirected;
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
    return lastKnownPartition()
        .then(partition => {
            // A kiosk gets no offline client until PR 3.15 (the server refuses it /v as well).
            if (/^k-/.test(partition || '')) {
                throw new Error('client precache: kiosk');
            }
            return Promise.all([readClientShell(), completeClient(true)]);
        })
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

completeClient(false);
