/*
 * Blocwerk service worker.
 *
 * HONEST SCOPE — this is a Blazor *Server* app. A service worker CANNOT render server pages
 * offline: every interactive page is produced by the server over SignalR, so there is nothing to
 * cache for them. What it does buy us:
 *   1. The static shell (CSS/JS/manifest/icons) is precached, so an installed PWA opens instantly
 *      and its chrome renders even with no network.
 *   2. The queued /api/offline/* POSTs can be attempted the moment the SW is up, independent of
 *      the SignalR circuit — though those requests are ALWAYS network-only here; the queue owns
 *      their retry/idempotency, and a cached POST would be meaningless.
 *   3. Navigations that fail offline fall back to a tidy /offline.html instead of the browser's
 *      dinosaur.
 *
 * NEVER cached: anything under /api/* and /_blazor/* (SignalR), and any non-GET request. Those are
 * strictly network-only so auth, the circuit and the offline queue always talk to the live server.
 *
 * NICE-TO-HAVE not implemented: the Background Sync API could flush the queue when the tab is
 * closed. The baseline here is the existing `online` event + circuit-reconnect flush in
 * offline-queue.js / blazor-boot.js, which covers the tab-open case; Background Sync would only add
 * closed-tab flushing and needs its own IndexedDB replay in the SW, so it is deferred.
 */
const CACHE = 'blocwerk-shell-v2';

// Own static assets only. Fingerprinted framework files are handled at runtime (cache-first),
// never precached by exact name, because their hashes change on every build.
const PRECACHE = [
    '/offline.html',
    '/manifest.webmanifest',
    '/icons/icon-192.png',
    '/icons/icon-512.png',
    '/css/app.css',
    '/css/pages.css',
    '/css/components.css',
    '/js/theme.js',
    '/js/offline-db.js',
    '/js/offline-transport.js',
    '/js/offline-queue.js',
    '/js/offline-actions.js',
    '/js/offline-boulder.js',
    '/js/offline-status.js'
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
            .then(keys => Promise.all(keys.filter(k => k !== CACHE).map(k => caches.delete(k))))
            .then(() => self.clients.claim())
    );
});

function isBypassed(url, request) {
    if (request.method !== 'GET') {
        return true;
    }
    // Live server only: the offline queue, auth and the SignalR circuit must never be intercepted.
    return url.pathname.startsWith('/api/') || url.pathname.startsWith('/_blazor');
}

// Our own CSS/JS/icons change on every deploy but keep a stable path (no fingerprint). These MUST
// be network-first, or a stale cache silently masks a fresh deploy — the exact trap that made a
// re-composed image still serve the old front-end.
function isOwnAsset(url) {
    return url.pathname.startsWith('/css/')
        || url.pathname.startsWith('/js/')
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

    if (url.origin !== self.location.origin || isBypassed(url, request)) {
        return; // Let the network handle it untouched.
    }

    // Navigations (server-rendered): try the network, fall back to the offline page when it fails.
    if (request.mode === 'navigate') {
        event.respondWith(
            fetch(request).catch(() => caches.match('/offline.html'))
        );
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
    // fallback only. The app is online-first (Blazor Server needs the circuit anyway), so the
    // extra round-trip costs nothing users notice while online.
    if (isOwnAsset(url)) {
        event.respondWith(
            fetchAndCache(request).catch(() => caches.match(request))
        );
    }
});
