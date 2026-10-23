/*
 * Web Push and notification taps, loaded into service-worker.js by importScripts (moved out of it unchanged in PR 3.9,
 * to keep the worker file short). Nothing here takes part in caching or navigation.
 */
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
