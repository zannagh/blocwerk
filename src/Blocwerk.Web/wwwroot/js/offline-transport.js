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
    const BASE_DELAY_MS = 2000;
    const MAX_DELAY_MS = 5 * 60 * 1000;

    // Same-origin marker. A cross-site <form> cannot set request headers, and a cross-origin
    // fetch that sets one triggers a CORS preflight the app does not answer, so only first-party
    // JavaScript can produce a request that passes. See RequireClientHeaderAttribute.cs.
    const CLIENT_HEADER = 'X-Blocwerk-Client';

    function post(url, body) {
        return fetch(url, {
            method: 'POST',
            credentials: 'same-origin',
            cache: 'no-store',
            headers: {
                'Content-Type': 'application/json',
                [CLIENT_HEADER]: '1'
            },
            body: JSON.stringify(body)
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
         *   'pause'  401. The session is gone. Stop the queue and prompt for sign-in; the entry
         *            keeps its clientRequestId so it replays safely after logging back in.
         *   'hold'   409. The entry was queued by a DIFFERENT user than the one signed in now
         *            (shared tablet, shared laptop). Writing it would credit the wrong person, so
         *            the server refused. Keep the entry, skip it, and let it replay if its owner
         *            comes back — the 7-day expiry is the backstop if they never do.
         *   'retry'  408 / 429 / 5xx. Transient. Keep the entry and back off.
         *   'drop'   any other 4xx. The action can never succeed (deleted boulder, not a wall
         *            member, malformed payload). Delete it and tell the user.
         * Network-level errors never reach here; the queue treats a thrown fetch as 'retry'.
         */
        classify: function (response) {
            if (response.ok) {
                return 'sent';
            }
            if (response.status === 401) {
                return 'pause';
            }
            if (response.status === 409) {
                return 'hold';
            }
            if (response.status === 408 || response.status === 429) {
                return 'retry';
            }
            if (response.status >= 400 && response.status < 500) {
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
