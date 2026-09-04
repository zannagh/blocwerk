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
const CACHE = 'blocwerk-shell-v3';

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

// Our own CSS/JS/icons. The fingerprinted ones are immutable under a given name, and the ones that
// keep a stable path (icons, the manifest) change on every deploy, so network-first is right for
// both: it can never let a stale cache mask a fresh deploy — the exact trap that made a re-composed
// image still serve the old front-end — and for a fingerprinted URL the `immutable` response is
// already in the browser's HTTP cache, so the "network" leg costs no round trip.
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
    // fallback only. This is also what populates the offline shell — every fingerprinted CSS/JS
    // file the page loads is cached here on the first online visit, in place of the precache list
    // that used to name them.
    if (isOwnAsset(url)) {
        event.respondWith(
            fetchAndCache(request).catch(() => caches.match(request))
        );
    }
});

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
