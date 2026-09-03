/*
 * ============================================================================
 * FORM-SNAPSHOT CONTRACT for circuit-independent boulder create / revise.
 * ============================================================================
 *
 * WHY A SECOND CONTRACT. offline-actions.js scrapes a handful of self-describing attributes off
 * the clicked element, which is perfect for the four small idempotent actions but does NOT
 * generalise to a whole boulder form (name + grade + three rule flags + a variable-length list of
 * hold marks). So boulder create/revise use a different capture: Blazor renders the entire form
 * as a JSON snapshot into a hidden element, and this file reads that snapshot at click time and
 * enqueues it as ONE queue entry. Everything downstream (IndexedDB, backoff, the 401 pause, the
 * status pill, the reconnect flush) is the same queue as offline-actions.js — this is a new entry
 * KIND, not a fork.
 *
 * ----------------------------------------------------------------------------
 * ENTRY KINDS (see offline-transport.js ENDPOINTS)
 *   'boulder-create'  POST /api/offline/boulders          upsert on the client-minted Boulder.Id
 *   'boulder-revise'  POST /api/offline/boulders/{id}      replace the boulder's holds + fields
 * Both dedupe on the boulder id (payload.boulderId), so re-tapping submit before the first send
 * collapses onto the same never-sent entry rather than stacking. Create is idempotent because the
 * id is the key; revise is idempotent because it replaces state. A replay never duplicates.
 *
 * DEPENDENCY ORDERING. The boulder id is minted on the client (Blazor OnInitialized), so an
 * ascent logged on a still-offline boulder carries that same id via data-bw-boulder. The queue
 * flushes FIFO and stops on the first not-yet-sent failure, so a 'boulder-create' enqueued before
 * an 'attempt' always reaches the server first; the attempt then resolves against the created
 * boulder. No cross-entry reordering is needed — insertion order plus the shared id is enough.
 *
 * ----------------------------------------------------------------------------
 * HOW TO WIRE A SUBMIT BUTTON (the Razor author's side)
 * ----------------------------------------------------------------------------
 *
 * Render a hidden element carrying the live snapshot, recomputed on every render so it reflects
 * the last state that reached the browser:
 *
 *   <div id="bw-boulder-form" hidden data-bw-boulder-snapshot="@SnapshotJson"></div>
 *
 * where SnapshotJson serializes { id, wallId, name, grade, kickboardFootholdsOn, handsFollowFeet,
 * footColorOnly, holds:[{holdId,type,usage}] } (camelCase, System.Text.Json defaults).
 *
 * Render the submit button with a Blazor @onclick for the online path AND these attributes so the
 * click still works when the circuit is down:
 *
 *   REQUIRED
 *     data-bw-boulder-action="create" | "revise"
 *     data-bw-boulder-form="<css selector of the snapshot element>"
 *     data-bw-boulder-nav="<url to navigate to after enqueue>"
 *     data-bw-mode="offline-only"     handle ONLY while the circuit is down; when it is up the
 *                                     element's own @onclick runs the normal server path instead.
 *   PER ACTION
 *     create  data-bw-boulder-draft="true" | "false"   whether Publish or Save-as-Draft was hit
 *
 * The @onclick MUST use the same client-minted id (pass it to CreateBoulderAsync) so the online
 * and offline paths converge on one boulder even if both somehow run.
 *
 *   NOT QUEUEABLE
 *     data-bw-offline-unavailable="<message>"   instead of the attributes above, for an action the
 *                                     queue must never carry (an anonymous kiosk create: the
 *                                     endpoint is [Authorize], so the entry would 401 and pause the
 *                                     whole queue). Offline the click shows <message>; online it is
 *                                     inert and the @onclick runs as usual.
 */
(function () {
    const queue = window.blocwerkOfflineQueue;
    if (!queue) {
        return;
    }

    const DOUBLE_TAP_MS = 1200;
    let lastClick = 0;

    function readSnapshot(element) {
        const selector = element.getAttribute('data-bw-boulder-form');
        const host = selector ? document.querySelector(selector) : null;
        const raw = host ? host.getAttribute('data-bw-boulder-snapshot') : null;
        if (!raw) {
            return null;
        }
        try {
            return JSON.parse(raw);
        } catch (err) {
            console.warn('[blocwerk-offline] bad boulder snapshot', err);
            return null;
        }
    }

    function buildEntry(element) {
        const action = element.getAttribute('data-bw-boulder-action');
        const snapshot = readSnapshot(element);
        if (!snapshot || !snapshot.id) {
            return null;
        }

        // The queue dedupes absolute actions on payload.boulderId; mirroring the id there lets a
        // create/revise collapse a double-tap without teaching the queue about boulder payloads.
        const payload = Object.assign({}, snapshot, { boulderId: snapshot.id });

        if (action === 'create') {
            payload.isDraft = element.getAttribute('data-bw-boulder-draft') === 'true';
            return { kind: 'boulder-create', payload: payload };
        }
        if (action === 'revise') {
            return { kind: 'boulder-revise', payload: payload };
        }
        return null;
    }

    function handle(event) {
        const element = event.target instanceof Element
            ? event.target.closest('[data-bw-boulder-action]')
            : null;

        if (!element || element.hasAttribute('disabled')) {
            return;
        }

        // Online: let the element's Blazor @onclick run the normal server path.
        if (element.getAttribute('data-bw-mode') === 'offline-only'
            && window.blocwerkConnection && window.blocwerkConnection.isUp()) {
            return;
        }

        const now = Date.now();
        if (now - lastClick < DOUBLE_TAP_MS) {
            event.preventDefault();
            return;
        }
        lastClick = now;

        const entry = buildEntry(element);
        if (!entry) {
            return;
        }

        event.preventDefault();

        queue.enqueue(entry.kind, entry.payload).then(() => {
            const nav = element.getAttribute('data-bw-boulder-nav');
            if (nav) {
                // Optimistic navigation. Offline this lands on the PWA offline fallback (the
                // boulder is server-rendered); the queue keeps the submit and the pill shows it.
                window.location.assign(nav);
            }
        }).catch(err => {
            console.error('[blocwerk-offline] boulder enqueue failed', err);
            window.dispatchEvent(new CustomEvent('bw:offline-error', {
                detail: { message: err && err.message ? err.message : 'Could not save this boulder.' }
            }));
        });
    }

    /**
     * An action that is explicitly NOT queueable while offline (today: a boulder create on an
     * ANONYMOUS kiosk tablet — see BoulderCreate.razor). The endpoint behind the queue is
     * [Authorize], so such an entry would flush to a 401, and a 401 pauses the whole queue awaiting
     * a sign-in nobody is going to perform on an unattended tablet. Rather than enqueue a poison
     * entry, say plainly that the action needs a connection.
     *
     * Marked with `data-bw-offline-unavailable="<message>"`. Online this does nothing at all and the
     * element's own Blazor @onclick runs the normal path, exactly as before.
     */
    function handleUnavailable(event) {
        const element = event.target instanceof Element
            ? event.target.closest('[data-bw-offline-unavailable]')
            : null;

        if (!element || element.hasAttribute('disabled')) {
            return;
        }

        if (window.blocwerkConnection && window.blocwerkConnection.isUp()) {
            return;
        }

        event.preventDefault();
        event.stopPropagation();

        const message = element.getAttribute('data-bw-offline-unavailable')
            || 'This action needs a connection.';
        if (window.blocwerkStatus && window.blocwerkStatus.flash) {
            window.blocwerkStatus.flash(message, 'error', 6000);
        } else {
            window.dispatchEvent(new CustomEvent('bw:offline-error', { detail: { message: message } }));
        }
    }

    document.addEventListener('click', handleUnavailable, true);
    document.addEventListener('click', handle, true);
})();
