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
 *   state()                -> { online, paused, flushing, count, held, rejected, lastError }
 *   retryNow()             -> Promise          clear backoff/pause and flush (user gesture)
 *   dismissRejected()      -> void             acknowledge permanently-failed actions
 *
 * IDEMPOTENCE. Each entry gets a crypto.randomUUID() as its clientRequestId AT ENQUEUE TIME and
 * reuses it for every retry. Attempts and comments have partial unique indexes on that column
 * server-side, so a replay returns the existing row; ratings and favourites are absolute-value
 * upserts, so replaying them is a no-op. That is what makes "send, lose the response, send
 * again" safe.
 *
 * ATTRIBUTION. IndexedDB is per browser profile, not per user, and a queued action carries no
 * identity of its own — replay is a plain cookie-authenticated POST, so the server would credit it
 * to whoever is signed in WHEN IT DRAINS. On a device several people use in sequence (a kiosk
 * tablet, a shared laptop) that is the wrong person: A logs a send while the link is flaky, A
 * releases the tablet, B picks themselves, the queue flushes, and A's send lands on B's logbook.
 * So every entry is stamped with the acting user's id at enqueue time (`queuedForUserId`, read
 * from <body data-bw-user>), and:
 *   - flush() skips entries stamped for somebody else instead of sending them;
 *   - the server re-checks the stamp against the real cookie identity and answers 409 if it
 *     disagrees, which is the actual guarantee — the client-side skip is only an optimisation.
 * Mismatched entries are HELD, not discarded: someone's logged climb is real data, and on a gym
 * tablet its owner very plausibly comes back. The existing 7-day expiry is the backstop, and it
 * already surfaces the loss through reject() rather than dropping it silently.
 *
 * LEGACY ENTRIES. Anything already in a browser's store when this shipped has no stamp. Unstamped
 * is treated as "belongs to whoever is signed in", i.e. exactly the old behaviour — the only
 * alternatives are to destroy data or to strand it forever, since there is no way to recover an
 * owner that was never recorded. The exposure is a one-time drain of what was already queued, so
 * the upgrade cannot make any device worse than it was.
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

    // How long to leave an entry alone after the server held it for the wrong user. Long enough
    // that a tablet in use by somebody else is not re-probed every tick, short enough that the
    // owner signing back in gets their queue drained within a minute.
    const HOLD_RECHECK_MS = 60000;

    const HELD_MESSAGE_ONE =
        'One queued action belongs to a different account. It will sync when they sign in.';
    const HELD_MESSAGE_MANY =
        ' queued actions belong to a different account. They will sync when they sign in.';

    const db = window.blocwerkOfflineDb;
    const transport = window.blocwerkOfflineTransport;
    const subscribers = [];
    const state = {
        online: navigator.onLine !== false,
        paused: false,
        flushing: false,
        count: 0,
        held: 0,
        rejected: [],
        lastError: null,
        nextAttemptAt: 0
    };

    /**
     * The user this page was rendered for, or null when nobody is signed in. Read from the DOM on
     * every call rather than cached, so it is always the identity of the page the user is actually
     * looking at. Not a credential: it is only ever used to decide which of OUR OWN queued entries
     * to send, and the server re-checks it against the cookie.
     */
    function currentUserId() {
        const id = document.body && document.body.dataset
            ? document.body.dataset.bwUser
            : null;
        return id ? id : null;
    }

    /**
     * True when an entry may be sent as the user who is signed in now. Unstamped entries (queued
     * before stamping existed) always may — see LEGACY ENTRIES above.
     */
    function belongsToCurrentUser(entry) {
        return !entry.queuedForUserId || entry.queuedForUserId === currentUserId();
    }

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

        // Stamped at ENQUEUE time — the whole point is that this is the person who tapped, not the
        // person who happens to be signed in whenever the network comes back.
        const queuedForUserId = currentUserId();

        // The dedupe key is scoped to the user as well as the boulder: two people rating the same
        // boulder on the same tablet are two separate intents, and collapsing B's rating onto A's
        // pending entry would both lose B's tap and rewrite A's.
        const dedupeKey = DEDUPABLE[kind]
            ? kind + ':' + payload.boulderId + ':' + (queuedForUserId || '')
            : null;

        return db.all().then(entries => {
            if (dedupeKey) {
                // Only collapse into an entry we have never put on the wire. Once it has been
                // attempted the server may already hold it, and rewriting its payload under the
                // same clientRequestId would make the replay mean something different.
                const existing = entries.find(e => e.dedupeKey === dedupeKey && e.attempts === 0);
                if (existing) {
                    existing.payload = Object.assign({}, payload, {
                        clientRequestId: existing.clientRequestId,
                        queuedForUserId: queuedForUserId || undefined
                    });
                    existing.queuedForUserId = queuedForUserId;
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
                // The stamp rides on the entry (so flush can skip without unpacking the payload)
                // AND in the payload (so the server can refuse a mismatch). Anonymous enqueues
                // stay unstamped, which is the legacy path.
                queuedForUserId: queuedForUserId,
                payload: Object.assign({}, payload, {
                    clientRequestId: clientRequestId,
                    queuedForUserId: queuedForUserId || undefined
                }),
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
            const verdict = transport.classify(response, entry);

            if (verdict === 'sent') {
                return db.remove(entry.id).then(() => 'sent');
            }

            return transport.readError(response).then(message => {
                if (verdict === 'drop') {
                    reject(entry, message);
                    return db.remove(entry.id).then(() => 'drop');
                }

                if (verdict === 'hold') {
                    // Refused for wrong-user, not for being wrong. Keep it verbatim and do not burn
                    // its backoff — nothing about the entry needs to change for it to succeed once
                    // its owner is signed in again.
                    entry.attempts -= 1;
                    entry.lastError = message;
                    entry.nextAttemptAt = Date.now() + HOLD_RECHECK_MS;
                    return db.put(entry).then(() => 'hold');
                }

                if (verdict === 'pause') {
                    state.paused = true;
                    state.lastError = message;
                    entry.attempts -= 1; // Not the action's fault; do not burn its backoff.
                    entry.lastError = message;
                    return db.put(entry).then(() => 'pause');
                }

                // 'retry' and 'defer' are the same bookkeeping — keep the entry, burn a backoff
                // window — and differ only in whether flush() should attempt the rest of the run.
                // 'defer' is a 400 the transport could not attribute (see offline-transport.js):
                // possibly a bad payload, possibly a page whose JavaScript predates the antiforgery
                // header. Guessing "permanent" there deletes real data, so it waits for the 7-day
                // expiry instead; a reload fixes the stale-JavaScript case outright.
                entry.lastError = message;
                entry.nextAttemptAt = Date.now() + transport.backoffFor(entry.attempts);
                state.lastError = message;
                return db.put(entry).then(() => verdict);
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
            return Promise.resolve({ sent: 0, dropped: 0, held: state.held, remaining: state.count });
        }

        state.flushing = true;
        notify();

        const summary = { sent: 0, dropped: 0, held: 0, remaining: 0 };

        return db.all().then(entries => {
            const now = Date.now();

            // Entries stamped for somebody else are not sent at all. The server would refuse them
            // anyway (409); skipping here just spares the round-trip and keeps one held entry from
            // sitting at the head of the queue.
            const held = entries.filter(e => !belongsToCurrentUser(e));
            summary.held = held.length;

            const due = entries.filter(
                e => (e.nextAttemptAt || 0) <= now && belongsToCurrentUser(e));

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
                    } else if (outcome === 'hold') {
                        // The stamp on the entry disagreed with the DOM but the server caught it
                        // anyway. Count it and carry on: it says nothing about the next entry.
                        summary.held += 1;
                    }

                    // A paused queue (401) or a dead network stops the run; retrying the rest
                    // right now would just repeat the same failure. A hold does not — it is about
                    // that one entry's owner, not about the connection. Neither does a defer: it
                    // may well be about that one entry's payload, and a deferred entry now outlives
                    // its attempt for up to seven days, so it must never sit in front of the queue.
                    return outcome === 'pause' || outcome === 'retry';
                });
            }), Promise.resolve(false));
        }).then(() => db.all()).then(remaining => {
            summary.remaining = remaining.length;
            state.count = remaining.length;
            state.held = remaining.filter(e => !belongsToCurrentUser(e)).length;
            state.nextAttemptAt = remaining.reduce(
                (min, e) => (e.nextAttemptAt && (!min || e.nextAttemptAt < min)) ? e.nextAttemptAt : min,
                0);
            if (remaining.length === 0) {
                state.lastError = null;
            } else if (state.held === remaining.length) {
                // Everything left over is somebody else's. Say so, otherwise the status pill reads
                // as a stuck sync to a person who can do nothing about it.
                state.lastError = state.held === 1
                    ? HELD_MESSAGE_ONE
                    : state.held + HELD_MESSAGE_MANY;
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
                return { sent: 0, dropped: 0, held: state.held, remaining: state.count };
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
