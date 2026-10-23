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
 *      route: sw-client-cache.js.
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

importScripts('/js/sw-asset-cache.js', '/js/sw-client-cache.js', '/js/sw-client-fill.js', '/js/sw-push.js');

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
        // An announced deploy holds the offline client's hand-off until the new instance is seen (sw-client-cache.js).
        lifetime(event, data.type === 'deploy-pending' ? holdForDeploy() : endDeployHold());
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
    // fails (sw-client-cache.js); a /v/ navigation is the client's shell.
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
