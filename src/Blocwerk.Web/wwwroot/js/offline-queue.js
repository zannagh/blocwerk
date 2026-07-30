/*
 * Blocwerk durable offline action queue.
 *
 * The app is Blazor Server: every @onclick is a SignalR round-trip, so when the circuit is down
 * nothing happens at all. This queue is the escape hatch — actions are written to IndexedDB the
 * moment the user taps, then POSTed to /api/offline/* (plain HTTP, no circuit) when the network
 * allows. See offline-actions.js for the DOM contract that feeds it, and offline-transport.js
 * for the failure matrix.
 *
 * Public API (window.blocwerkOfflineQueue), where `kind` is
 * 'attempt' | 'rating' | 'favorite' | 'comment':
 *   enqueue(kind, payload) -> Promise<entry>   queue an action and try to send it immediately
 *   flush()                -> Promise<summary> send everything that is due
 *   pending()              -> Promise<entry[]> everything still queued, oldest first
 *   subscribe(cb)          -> unsubscribe fn   cb(state) on every state change
 *   state()                -> { online, paused, flushing, count, rejected, lastError }
 *   retryNow()             -> Promise          clear backoff/pause and flush (user gesture)
 *   dismissRejected()      -> void             acknowledge permanently-failed actions
 *
 * IDEMPOTENCE. Each entry gets a crypto.randomUUID() as its clientRequestId AT ENQUEUE TIME and
 * reuses it for every retry. Attempts and comments have partial unique indexes on that column
 * server-side, so a replay returns the existing row; ratings and favourites are absolute-value
 * upserts, so replaying them is a no-op. That is what makes "send, lose the response, send
 * again" safe.
 */
(function () {
    // Absolute-value actions: a second tap on the same boulder supersedes the first rather than
    // stacking. Attempts and comments are additive, so they never collapse. A boulder
    // create/revise is a whole-form snapshot keyed on the boulder id, so a re-tap before the first
    // send is the latest intent and collapses onto the pending entry (see offline-boulder.js).
    const DEDUPABLE = { rating: true, favorite: true, 'boulder-create': true, 'boulder-revise': true };

    const MAX_QUEUE = 200;
    const MAX_AGE_MS = 7 * 24 * 60 * 60 * 1000;
    const RETRY_TICK_MS = 5000;

    const db = window.blocwerkOfflineDb;
    const transport = window.blocwerkOfflineTransport;
    const subscribers = [];
    const state = {
        online: navigator.onLine !== false,
        paused: false,
        flushing: false,
        count: 0,
        rejected: [],
        lastError: null,
        nextAttemptAt: 0
    };

    function notify() {
        const snapshot = Object.assign({}, state, { rejected: state.rejected.slice() });
        subscribers.forEach(cb => {
            try {
                cb(snapshot);
            } catch (err) {
                console.error('[blocwerk-offline] subscriber failed', err);
            }
        });
    }

    function refreshCount() {
        return db.count().then(n => {
            state.count = n;
            notify();
            return n;
        }).catch(() => state.count);
    }

    function reject(entry, message) {
        state.rejected.push({ kind: entry.kind, message: message, at: Date.now() });
        if (state.rejected.length > 20) {
            state.rejected.splice(0, state.rejected.length - 20);
        }
    }

    function enqueue(kind, payload) {
        if (!transport.supports(kind)) {
            return Promise.reject(new Error('Unknown offline action kind: ' + kind));
        }
        if (!db || !db.available()) {
            return Promise.reject(new Error('IndexedDB unavailable'));
        }

        const dedupeKey = DEDUPABLE[kind] ? kind + ':' + payload.boulderId : null;

        return db.all().then(entries => {
            if (dedupeKey) {
                // Only collapse into an entry we have never put on the wire. Once it has been
                // attempted the server may already hold it, and rewriting its payload under the
                // same clientRequestId would make the replay mean something different.
                const existing = entries.find(e => e.dedupeKey === dedupeKey && e.attempts === 0);
                if (existing) {
                    existing.payload = Object.assign({}, payload, {
                        clientRequestId: existing.clientRequestId
                    });
                    existing.createdAt = Date.now();
                    return db.put(existing).then(() => existing);
                }
            }

            if (entries.length >= MAX_QUEUE) {
                return Promise.reject(new Error(
                    'Offline queue is full (' + MAX_QUEUE + ' actions). Reconnect to sync.'));
            }

            const clientRequestId = crypto.randomUUID();
            const entry = {
                clientRequestId: clientRequestId,
                kind: kind,
                payload: Object.assign({}, payload, { clientRequestId: clientRequestId }),
                dedupeKey: dedupeKey,
                createdAt: Date.now(),
                attempts: 0,
                nextAttemptAt: 0,
                lastError: null
            };

            return db.add(entry).then(id => {
                entry.id = id;
                return entry;
            });
        }).then(entry => {
            return refreshCount().then(() => {
                // Fire and forget: the caller only cares that the action is durable now.
                flush();
                return entry;
            });
        });
    }

    function processOne(entry) {
        if (Date.now() - entry.createdAt > MAX_AGE_MS) {
            reject(entry, 'Queued action expired after 7 days and was discarded.');
            return db.remove(entry.id).then(() => 'drop');
        }

        entry.attempts += 1;

        return transport.send(entry).then(response => {
            const verdict = transport.classify(response);

            if (verdict === 'sent') {
                return db.remove(entry.id).then(() => 'sent');
            }

            return transport.readError(response).then(message => {
                if (verdict === 'drop') {
                    reject(entry, message);
                    return db.remove(entry.id).then(() => 'drop');
                }

                if (verdict === 'pause') {
                    state.paused = true;
                    state.lastError = message;
                    entry.attempts -= 1; // Not the action's fault; do not burn its backoff.
                    entry.lastError = message;
                    return db.put(entry).then(() => 'pause');
                }

                entry.lastError = message;
                entry.nextAttemptAt = Date.now() + transport.backoffFor(entry.attempts);
                state.lastError = message;
                return db.put(entry).then(() => 'retry');
            });
        }).catch(err => {
            // Network-level failure: offline, DNS, TLS, aborted. Always keep and retry.
            entry.lastError = err && err.message ? err.message : 'Network error';
            entry.nextAttemptAt = Date.now() + transport.backoffFor(entry.attempts);
            return db.put(entry).then(() => 'retry');
        });
    }

    function flush() {
        if (state.flushing || state.paused || !db || !db.available()) {
            return Promise.resolve({ sent: 0, dropped: 0, remaining: state.count });
        }

        state.flushing = true;
        notify();

        const summary = { sent: 0, dropped: 0, remaining: 0 };

        return db.all().then(entries => {
            const now = Date.now();
            const due = entries.filter(e => (e.nextAttemptAt || 0) <= now);

            // Sequential, in insertion order: preserves the order the user tapped things in and
            // avoids firing a hundred parallel requests the instant a flaky link comes back.
            return due.reduce((chain, entry) => chain.then(stop => {
                if (stop) {
                    return true;
                }
                return processOne(entry).then(outcome => {
                    if (outcome === 'sent') {
                        summary.sent += 1;
                    } else if (outcome === 'drop') {
                        summary.dropped += 1;
                    }

                    // A paused queue (401) or a dead network stops the run; retrying the rest
                    // right now would just repeat the same failure.
                    return outcome === 'pause' || outcome === 'retry';
                });
            }), Promise.resolve(false));
        }).then(() => db.all()).then(remaining => {
            summary.remaining = remaining.length;
            state.count = remaining.length;
            state.nextAttemptAt = remaining.reduce(
                (min, e) => (e.nextAttemptAt && (!min || e.nextAttemptAt < min)) ? e.nextAttemptAt : min,
                0);
            if (remaining.length === 0) {
                state.lastError = null;
            }
        }).catch(err => {
            state.lastError = err && err.message ? err.message : String(err);
        }).then(() => {
            state.flushing = false;
            notify();
            if (summary.sent > 0) {
                document.dispatchEvent(new CustomEvent('bw:offline-flushed', { detail: summary }));
            }
            return summary;
        });
    }

    // User-initiated "retry now". Confirms the session is actually usable before unpausing, so
    // tapping it with an expired cookie re-states the sign-in prompt instead of silently
    // re-running the same 401 for every queued action.
    function retryNow() {
        return transport.checkSession().then(authenticated => {
            if (!authenticated) {
                state.paused = true;
                state.lastError = 'Your session has expired. Sign in to sync.';
                notify();
                return { sent: 0, dropped: 0, remaining: state.count };
            }

            state.paused = false;
            state.lastError = null;
            return db.all()
                .then(entries => Promise.all(entries.map(e => {
                    e.nextAttemptAt = 0;
                    return db.put(e);
                })))
                .then(() => flush());
        });
    }

    window.blocwerkOfflineQueue = {
        enqueue: enqueue,
        flush: flush,
        retryNow: retryNow,
        pending: function () {
            return db && db.available() ? db.all() : Promise.resolve([]);
        },
        state: function () {
            return Object.assign({}, state, { rejected: state.rejected.slice() });
        },
        subscribe: function (cb) {
            subscribers.push(cb);
            cb(this.state());
            return function () {
                const i = subscribers.indexOf(cb);
                if (i >= 0) {
                    subscribers.splice(i, 1);
                }
            };
        },
        dismissRejected: function () {
            state.rejected = [];
            notify();
        },

        // Called by blazor-boot.js when the SignalR circuit comes back up. If the queue was
        // paused on a 401 this re-probes the session, so signing back in (which re-establishes
        // the circuit) resumes the queue on its own.
        onCircuitUp: function () {
            state.online = true;
            return state.paused ? retryNow() : flush();
        }
    };

    // Flush triggers: regained connectivity, returning to a backgrounded tab (where `online`
    // often does not fire), a periodic tick that respects each entry's backoff, and page load.
    window.addEventListener('online', () => { state.online = true; notify(); flush(); });
    window.addEventListener('offline', () => { state.online = false; notify(); });
    document.addEventListener('visibilitychange', () => {
        if (document.visibilityState === 'visible') { flush(); }
    });
    setInterval(() => {
        if (!state.paused && state.count > 0) { flush(); }
    }, RETRY_TICK_MS);
    refreshCount().then(() => flush());
})();
