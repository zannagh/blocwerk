/*
 * Registers the service worker. Kept separate from blazor-boot.js so the manual Blazor.start flow
 * there is untouched: SW registration is independent of the circuit and must not delay or depend
 * on it.
 *
 * The worker file lives at /js/service-worker.js but must control the whole origin, so it is
 * registered with scope '/'. That wider scope is only permitted because the server sends
 * `Service-Worker-Allowed: /` for that file (see Program.cs). Registration is guarded for browsers
 * without service-worker support and never throws into the page.
 */
(function () {
    if (!('serviceWorker' in navigator)) {
        return;
    }

    window.addEventListener('load', function () {
        navigator.serviceWorker.register('/js/service-worker.js', { scope: '/' })
            .catch(function (err) {
                console.warn('[blocwerk-pwa] service worker registration failed', err);
            });
    });
})();
