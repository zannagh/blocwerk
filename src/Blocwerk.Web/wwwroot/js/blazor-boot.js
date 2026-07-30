/*
 * Manual Blazor startup + circuit connection state.
 *
 * `blazor.web.js` is loaded with autostart="false" so we can widen the reconnection budget: the
 * stock policy gives up after a handful of quick retries, which on a phone that walked into a
 * dead spot in a gym means the app is simply dead until a manual refresh.
 *
 * Deliberately NOT setting `circuit.reconnectionHandler`: supplying one replaces Blazor's
 * DefaultReconnectionHandler wholesale, which would mean re-implementing the retry loop itself.
 * .NET 10 gives a better hook — the `components-reconnect-state-changed` event on the element
 * with id `components-reconnect-modal` — so we keep the framework's retry logic and only observe
 * it. States: show | retrying | paused | hide | failed | rejected.
 *
 * Exposes `window.blocwerkConnection`:
 *   isUp()            -> bool
 *   status()          -> 'connected' | 'reconnecting' | 'failed' | 'rejected'
 *   subscribe(cb)     -> unsubscribe fn, cb(status)
 *   reconnect()       -> manual retry (used by the status pill)
 *   reload()          -> full page reload, for the 'rejected' case
 */
(function () {
    const MODAL_ID = 'components-reconnect-modal';
    const MAX_RETRIES = 30;
    const subscribers = [];

    let status = 'connected';

    function publish(next) {
        if (status === next) {
            return;
        }
        status = next;
        subscribers.forEach(cb => {
            try {
                cb(status);
            } catch (err) {
                console.error('[blocwerk-connection] subscriber failed', err);
            }
        });
    }

    window.blocwerkConnection = {
        isUp: function () {
            return status === 'connected';
        },
        status: function () {
            return status;
        },
        subscribe: function (cb) {
            subscribers.push(cb);
            cb(status);
            return function () {
                const i = subscribers.indexOf(cb);
                if (i >= 0) {
                    subscribers.splice(i, 1);
                }
            };
        },
        reconnect: function () {
            if (window.Blazor && window.Blazor.reconnect) {
                window.Blazor.reconnect();
            }
        },
        reload: function () {
            location.reload();
        }
    };

    function onStateChanged(event) {
        const next = event && event.detail ? event.detail.state : null;

        if (next === 'hide') {
            publish('connected');

            // The circuit is back: drain anything the user did while it was down, then let the
            // components reload from server truth (offline-actions.js listens for the flush).
            if (window.blocwerkOfflineQueue) {
                window.blocwerkOfflineQueue.onCircuitUp();
            }
            return;
        }

        if (next === 'show' || next === 'retrying' || next === 'paused') {
            publish('reconnecting');
            return;
        }

        if (next === 'failed') {
            publish('failed');

            // Blazor stops on its own after maxRetries; keep nudging it so a phone that
            // regains signal ten minutes later recovers without a manual refresh.
            if (navigator.onLine !== false) {
                setTimeout(() => window.blocwerkConnection.reconnect(), 5000);
            }
            return;
        }

        if (next === 'rejected') {
            // Server-side state is gone. A reload is the only fix, but it is offered rather
            // than forced: reloading while offline would replace a working queued UI with an
            // error page. The queue lives in IndexedDB, so a reload never loses it.
            publish('rejected');
        }
    }

    function attach() {
        const modal = document.getElementById(MODAL_ID);
        if (modal) {
            modal.addEventListener('components-reconnect-state-changed', onStateChanged);
        }
    }

    // A phone that regains connectivity should try the circuit immediately, not wait out the
    // current backoff step.
    window.addEventListener('online', function () {
        if (status !== 'connected') {
            window.blocwerkConnection.reconnect();
        }
    });

    attach();

    window.Blazor.start({
        circuit: {
            reconnectionOptions: {
                maxRetries: MAX_RETRIES,
                retryIntervalMilliseconds: function (previousAttempts, maxRetries) {
                    if (previousAttempts >= maxRetries) {
                        return null;
                    }

                    // Fast at first (a dropped websocket usually comes straight back), then
                    // backing off to 15s with jitter so a restarting server is not stampeded.
                    const step = Math.min(1000 * Math.pow(1.6, previousAttempts), 15000);
                    return Math.round(step * (0.75 + Math.random() * 0.5));
                }
            }
        }
    }).catch(function (err) {
        console.error('[blocwerk-connection] Blazor failed to start', err);
        publish('failed');
    });
})();
