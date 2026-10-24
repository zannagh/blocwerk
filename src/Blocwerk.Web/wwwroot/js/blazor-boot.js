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
 * What the framework does on its own, and what this file adds:
 *   - The framework retries on a backoff, and on a refused reconnect (the server no longer has the
 *     circuit) it resumes into a new circuit in place before it reports 'rejected'.
 *   - Its backoff only ends early when the tab becomes visible, so a phone that walks back into
 *     signal with the app open waits out the current step. The steps are therefore kept short
 *     (3s at most): the wait after the network returns is what the user feels.
 *   - Once its retries are used up it stops for good. From then on this file asks GET /alive
 *     and gets the circuit back itself as soon as the server answers. The two never run at the
 *     same time, so there is only ever one attempt in flight.
 *   - A resume renders the page again from the top; bwScrollKeeper puts it back where it was.
 *   - A page the browser hid away and brought back without loading it has no circuit any more
 *     and the framework does not notice. That is reported as 'rejected' as well.
 *
 * Exposes `window.blocwerkConnection`:
 *   isUp()            -> bool
 *   status()          -> 'connected' | 'reconnecting' | 'failed' | 'rejected'
 *   started()         -> bool: Blazor.start returned. It does not wait for the circuit (see circuitOpened).
 *   circuitOpened()   -> bool: the circuit has opened at least once (the framework's onCircuitOpened). Before that,
 *                        'connected' only means "nothing went wrong yet": a connection lost while the circuit is
 *                        still starting is not one the framework reconnects; circuit-watchdog.js reports it.
 *   markDown()        -> circuit-watchdog.js: the connection is gone and the framework raised nothing. Reported
 *                        as 'failed', so the probe below gets the circuit back once the server answers.
 *   lastChangeAt()    -> when the framework last raised a reconnect state (ms since epoch, 0 = never)
 *   subscribe(cb)     -> unsubscribe fn, cb(status)
 *   reconnect()       -> manual retry (used by the status pill)
 *   reload()          -> full page reload, for the 'rejected' case
 */
(function () {
    const MODAL_ID = 'components-reconnect-modal';
    const MAX_RETRIES = 40;
    const PROBE_URL = '/alive';
    const subscribers = [];

    // The longest the framework waits between two of its own retries.
    const RETRY_MAX_MS = 3000;

    // After the framework gave up: how often the server is asked whether it is reachable.
    const PROBE_MS = 2000;
    const PROBE_MAX_MS = 5000;
    const PROBE_TIMEOUT_MS = 4000;

    let status = 'connected';
    let probeTimer = null;
    let probeDelay = PROBE_MS;
    let attempting = false;
    let hiddenAway = false;

    /** True once the framework has stopped retrying and getting the circuit back is up to us. */
    function isOurs() {
        return status === 'failed';
    }

    function publish(next) {
        if (status === next) {
            return;
        }
        status = next;

        // Starts the probe on 'failed' and stops it on anything else.
        probeDelay = PROBE_MS;
        scheduleProbe(PROBE_MS);
        subscribers.forEach(cb => {
            try {
                cb(status);
            } catch (err) {
                console.error('[blocwerk-connection] subscriber failed', err);
            }
        });
    }

    /** True when the server answers a plain request, so a websocket is worth opening. */
    function serverReachable() {
        const abort = typeof AbortController === 'function' ? new AbortController() : null;
        const timer = abort ? setTimeout(() => abort.abort(), PROBE_TIMEOUT_MS) : null;

        return fetch(PROBE_URL, { cache: 'no-store', credentials: 'omit', signal: abort ? abort.signal : undefined })
            .then(response => response.ok, () => false)
            .then(ok => {
                clearTimeout(timer);
                return ok;
            });
    }

    /**
     * One attempt to get the circuit back, in the framework's own order: reconnect to the circuit
     * the server still holds, else resume into a new one, else report that the session is gone.
     * Resolves false when the server could not be reached at all.
     */
    async function attempt() {
        if (attempting || !isOurs() || !window.Blazor || !window.Blazor.reconnect) {
            return false;
        }

        attempting = true;
        try {
            if (await window.Blazor.reconnect()) {
                // The framework raises 'hide' itself unless the socket was open already.
                if (status !== 'connected') {
                    onStateChanged({ detail: { state: 'hide' } });
                }
                return true;
            }
            if (window.Blazor.resumeCircuit && await window.Blazor.resumeCircuit()) {
                return true;
            }

            publish('rejected');
            return true;
        } catch (err) {
            if (isTerminal(err)) {
                // The circuit never got an id (circuit-watchdog.js reported a start that died): no reconnect or
                // resume can bring it back, so retrying would leave the pill on Reconnecting forever. Offer the reload.
                publish('rejected');
                return true;
            }
            // The websocket did not open (the connection is not 'Connected', negotiation failed): the server may
            // well still hold the circuit, so the next probe tries again.
            return false;
        } finally {
            attempting = false;
        }
    }

    // The framework's "no circuit to reconnect to": its reconnect throws this when the circuit never got an id.
    function isTerminal(err) {
        return /Circuit host not initialized/i.test(String(err && err.message ? err.message : err));
    }

    function scheduleProbe(delay) {
        clearTimeout(probeTimer);
        probeTimer = isOurs() ? setTimeout(probe, delay) : null;
    }

    function probe() {
        probeTimer = null;
        if (!isOurs()) {
            return;
        }

        serverReachable()
            .then(reachable => (reachable ? attempt() : false))
            .then(done => {
                probeDelay = done ? PROBE_MS : Math.min(Math.round(probeDelay * 1.5), PROBE_MAX_MS);
                scheduleProbe(probeDelay);
            });
    }

    /** The device says it is back (network, foreground): check now instead of on the next tick. */
    function wake() {
        if (hiddenAway) {
            // Hidden away and running again: the circuit is gone (see the pagehide listener).
            // maintenance.js reloads the page quietly once the server answers.
            hiddenAway = false;
            publish('rejected');
        } else if (isOurs()) {
            probeDelay = PROBE_MS;
            scheduleProbe(0);
        }
    }

    let started = false;
    let circuitOpened = false;
    let markedDown = false;
    let lastChangeAt = 0;

    window.blocwerkConnection = {
        started: function () {
            return started;
        },
        circuitOpened: function () {
            return circuitOpened;
        },
        lastChangeAt: function () {
            return lastChangeAt;
        },
        markDown: function () {
            if (status === 'connected') {
                markedDown = true;
                publish('failed');
            }
        },
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
        lastChangeAt = Date.now();
        markedDown = false;

        if (next === 'hide') {
            publish('connected');

            // Clear Blazor's #blazor-error-ui if a transient blip left it stuck. Safe: a real
            // unhandled exception kills the circuit, so no successful 'hide' follows it — this only
            // fires when the circuit actually recovered, where the scary banner is a false alarm.
            const errUi = document.getElementById('blazor-error-ui');
            if (errUi) {
                errUi.style.display = 'none';
            }

            // The circuit is back: drain anything the user did while it was down, then let the
            // components reload from server truth (offline-actions.js listens for the flush).
            if (window.blocwerkOfflineQueue) {
                window.blocwerkOfflineQueue.onCircuitUp();
            }
            return;
        }

        if (status === 'rejected') {
            // The session is gone for good; only a real 'hide' above leaves this state. Without
            // this the framework's still-running countdown would flip the pill back and forth.
            return;
        }

        if (next === 'show' || next === 'retrying' || next === 'paused') {
            publish('reconnecting');
            return;
        }

        if (next === 'failed') {
            // Blazor stops on its own after maxRetries. The probe takes over from here (publish
            // starts it), so a phone that regains signal ten minutes later still recovers.
            publish('failed');
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

    // A phone that regains connectivity, or is taken out of the pocket, should try the circuit
    // immediately, not wait out the current probe step. While the framework is still retrying it
    // handles the foreground case itself and its steps are short, so this acts after it gave up.
    //
    // pagehide is where Blazor tells the server to drop the circuit, and it never starts one again
    // on the same page. A page the browser kept anyway (frozen on a phone, or in the back/forward
    // cache) comes back looking alive with nothing behind it. Remember it so wake() can act.
    window.addEventListener('pagehide', function () {
        hiddenAway = true;
    });
    window.addEventListener('online', wake);
    window.addEventListener('pageshow', wake);
    document.addEventListener('visibilitychange', function () {
        if (document.visibilityState === 'visible') {
            wake();
        }
    });

    attach();

    // Enhanced navigation morphs the server DOM over ours and strips the client-only <html>
    // chrome (dark theme, desktop/fullscreen layout). Restore it after every enhanced load.
    if (window.Blazor && window.Blazor.addEventListener) {
        window.Blazor.addEventListener('enhancedload', function () {
            if (window.blocwerkChrome) {
                window.blocwerkChrome.reapply();
            }
        });
    }

    window.Blazor.start({
        circuit: {
            // Called around a resume (the old circuit closed and a new one opened in place), and opened once when
            // the circuit first starts.
            circuitHandlers: [{
                onCircuitClosed: function () {
                    if (window.bwScrollKeeper) {
                        window.bwScrollKeeper.keep();
                    }
                },
                onCircuitOpened: function () {
                    circuitOpened = true;
                    // The watchdog called a start that seemed stuck down, and the circuit got through after all.
                    if (markedDown) {
                        markedDown = false;
                        publish('connected');
                    }
                    if (window.bwScrollKeeper) {
                        window.bwScrollKeeper.restore();
                    }
                }
            }],
            reconnectionOptions: {
                maxRetries: MAX_RETRIES,
                retryIntervalMilliseconds: function (previousAttempts, maxRetries) {
                    if (previousAttempts >= maxRetries) {
                        return null;
                    }

                    // Fast at first (a dropped websocket usually comes straight back), then every
                    // few seconds, with jitter so a restarting server is not hit in lockstep.
                    const step = Math.min(1000 * Math.pow(1.6, previousAttempts), RETRY_MAX_MS);
                    return Math.round(step * (0.75 + Math.random() * 0.5));
                }
            }
        }
    }).then(function () {
        started = true;
    }).catch(function (err) {
        console.error('[blocwerk-connection] Blazor failed to start', err);
        publish('failed');
    });
})();
