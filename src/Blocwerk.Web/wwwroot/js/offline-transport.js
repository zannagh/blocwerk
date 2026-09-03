/*
 * HTTP transport + failure classification for the offline queue.
 *
 * Split out of offline-queue.js so the queue file only deals with storage and scheduling. The
 * failure matrix lives here because it is the part most likely to need tuning.
 *
 * Exposes `window.blocwerkOfflineTransport`. Consumed by offline-queue.js only.
 */
(function () {
    const ENDPOINTS = {
        attempt: '/api/offline/attempts',
        rating: '/api/offline/ratings',
        favorite: '/api/offline/favorites',
        comment: '/api/offline/comments',
        // The whole-form boulder kinds (see offline-boulder.js). 'boulder-revise' addresses the
        // boulder by id in the path, so its endpoint is a prefix completed per entry.
        'boulder-create': '/api/offline/boulders',
        'boulder-revise': '/api/offline/boulders/'
    };

    const SESSION_URL = '/api/offline/session';
    const ANTIFORGERY_URL = '/api/offline/antiforgery';
    const BASE_DELAY_MS = 2000;
    const MAX_DELAY_MS = 5 * 60 * 1000;

    // Same-origin marker. A cross-site <form> cannot set request headers, and a cross-origin
    // fetch that sets one triggers a CORS preflight the app does not answer, so only first-party
    // JavaScript can produce a request that passes. See RequireClientHeaderAttribute.cs.
    const CLIENT_HEADER = 'X-Blocwerk-Client';

    // Antiforgery request token. The server validates it on every offline POST; the matching
    // cookie is set by the same call that mints it. Cached for the life of the page and refreshed
    // once on rejection, because a token minted before a re-login is bound to the old principal.
    const ANTIFORGERY_HEADER = 'X-Blocwerk-Antiforgery';
    let antiforgeryToken = null;

    // A mint that produces no token REJECTS; it never resolves to null. Resolving to null used to
    // send the POST without the header, which the server answers 400, which the queue reads as "the
    // server rejected this action on its merits" and deletes the entry — a captive-portal
    // interstitial or a transient 502 on this one GET was enough to destroy a logged send. A
    // rejection instead lands in processOne's network-error path: keep the entry, back off, retry.
    // The cached token is only ever replaced on success, so a failed refresh cannot clobber a good
    // one.
    function fetchAntiforgeryToken() {
        return fetch(ANTIFORGERY_URL, {
            credentials: 'same-origin',
            cache: 'no-store',
            headers: { [CLIENT_HEADER]: '1' }
        })
            .then(r => r.ok ? r.json() : Promise.reject(new Error('HTTP ' + r.status)))
            .then(body => {
                const token = (body && body.token) ? body.token : null;
                if (!token) {
                    throw new Error('No security token in response.');
                }
                antiforgeryToken = token;
                return token;
            })
            .catch(err => {
                throw new Error('Could not obtain a security token: '
                    + ((err && err.message) ? err.message : 'network error'));
            });
    }

    function ensureAntiforgeryToken() {
        return antiforgeryToken
            ? Promise.resolve(antiforgeryToken)
            : fetchAntiforgeryToken();
    }

    function rawPost(url, body, token) {
        const headers = {
            'Content-Type': 'application/json',
            [CLIENT_HEADER]: '1'
        };
        if (token) {
            headers[ANTIFORGERY_HEADER] = token;
        }

        return fetch(url, {
            method: 'POST',
            credentials: 'same-origin',
            cache: 'no-store',
            headers: headers,
            body: JSON.stringify(body)
        });
    }

    /**
     * One POST, with the antiforgery token attached — never without one. Three different failures
     * all surface as a bare 400 and only one of them is the action's fault:
     *   - the cached token was minted for a different principal (a re-login on a shared kiosk
     *     tablet) or signed with a DataProtection key that has since rotated;
     *   - the page is running JavaScript older than the antiforgery requirement, so it sends no
     *     header at all — a PWA tab left open across a deploy;
     *   - the payload really is unacceptable.
     * So a 400 is retried once with a freshly minted token, and if it survives that the queue
     * DEFERS it rather than dropping it (see classify). Deleting somebody's logged send on a guess
     * is precisely the outcome this queue exists to prevent, and the 7-day expiry is the backstop
     * for a 400 that is genuinely permanent.
     */
    function post(url, body) {
        return ensureAntiforgeryToken()
            .then(token => rawPost(url, body, token))
            .then(response => {
                if (response.status !== 400) {
                    return response;
                }
                // The token in hand was just refused, so do not keep handing it out. If the re-mint
                // fails the whole send rejects and is retried later — it never falls back to a
                // tokenless POST, which is what CSRF protection depends on.
                antiforgeryToken = null;
                return fetchAntiforgeryToken().then(token => rawPost(url, body, token));
            });
    }

    window.blocwerkOfflineTransport = {
        supports: function (kind) {
            return Object.prototype.hasOwnProperty.call(ENDPOINTS, kind);
        },

        send: function (entry) {
            return post(this.endpointFor(entry), entry.payload);
        },

        /** Resolves the POST url for an entry; 'boulder-revise' completes its path with the id. */
        endpointFor: function (entry) {
            if (entry.kind === 'boulder-revise') {
                return ENDPOINTS['boulder-revise'] + encodeURIComponent(entry.payload.id);
            }
            return ENDPOINTS[entry.kind];
        },

        /**
         * Cheap authenticated probe used before a flush that follows a long offline period, so an
         * expired cookie surfaces as a re-login prompt rather than a burst of failing posts.
         */
        checkSession: function () {
            return fetch(SESSION_URL, {
                credentials: 'same-origin',
                cache: 'no-store',
                headers: { [CLIENT_HEADER]: '1' }
            }).then(r => r.status !== 401).catch(() => true);
        },

        /**
         * The failure matrix.
         *   'sent'   2xx, including a replay the server had already applied. Delete the entry.
         *   'pause'  401 on an entry that was queued by a SIGNED-IN user. The session is gone.
         *            Stop the queue and prompt for sign-in; the entry keeps its clientRequestId so
         *            it replays safely after logging back in.
         *            A 401 on an entry marked `payload.anonymousKiosk` is DROPPED instead. Nobody
         *            was signed in when it was queued and nobody will be — an unattended tablet has
         *            no one to prompt — so pausing on it would wedge every other queued action
         *            behind an entry that can never succeed. Such entries are no longer produced at
         *            all (BoulderCreate.razor does not wire the queue for an anonymous session);
         *            this is the backstop for one already sitting in a tablet's IndexedDB.
         *   'hold'   409. The entry was queued by a DIFFERENT user than the one signed in now
         *            (shared tablet, shared laptop). Writing it would credit the wrong person, so
         *            the server refused. Keep the entry, skip it, and let it replay if its owner
         *            comes back — the 7-day expiry is the backstop if they never do.
         *   'retry'  408 / 429 / 5xx. Transient. Keep the entry and back off.
         *   'defer'  400, already retried once with a freshly minted token by post(). Cannot be
         *            told apart from a stale/absent antiforgery token, so it is NOT permanent:
         *            keep the entry and back off, but let the rest of the run continue (unlike
         *            'retry', which stops it) so one bad payload cannot wedge the queue behind it.
         *            A truly permanent 400 costs one request per backoff window until the 7-day
         *            expiry retires it through reject(), which is the price of never guessing
         *            "permanent" about data a user actually entered.
         *   'drop'   any other 4xx (403, 404, 410, ...). The action can never succeed — deleted
         *            boulder, not a wall member. Delete it and tell the user.
         * Network-level errors never reach here; the queue treats a thrown fetch as 'retry'.
         */
        classify: function (response, entry) {
            if (response.ok) {
                return 'sent';
            }
            if (response.status === 401) {
                return (entry && entry.payload && entry.payload.anonymousKiosk) ? 'drop' : 'pause';
            }
            if (response.status === 409) {
                return 'hold';
            }
            if (response.status === 408 || response.status === 429) {
                return 'retry';
            }
            if (response.status === 400) {
                return 'defer';
            }
            if (response.status > 400 && response.status < 500) {
                return 'drop';
            }
            return 'retry';
        },

        readError: function (response) {
            return response.json()
                .then(body => (body && body.message) ? body.message : ('HTTP ' + response.status))
                .catch(() => 'HTTP ' + response.status);
        },

        /** Exponential backoff with +/-25% jitter, capped, so a fleet of tabs that all dropped at
         *  the same moment does not retry in lockstep when the network returns. */
        backoffFor: function (attempts) {
            const raw = Math.min(BASE_DELAY_MS * Math.pow(2, Math.max(0, attempts - 1)), MAX_DELAY_MS);
            return Math.round(raw * (0.75 + Math.random() * 0.5));
        }
    };
})();
