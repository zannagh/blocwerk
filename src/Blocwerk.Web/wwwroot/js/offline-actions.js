/*
 * ============================================================================
 * DOM ATTRIBUTE CONTRACT for circuit-independent actions.
 * ============================================================================
 *
 * WHY THIS EXISTS. A Blazor `@onclick` is dispatched over SignalR. When the circuit is down the
 * click reaches nobody and the UI just sits there. This file installs a single delegated
 * listener on `document` — plain DOM, no circuit — so the four replayable actions keep working.
 *
 * ----------------------------------------------------------------------------
 * HOW TO WIRE A BUTTON (this is the part the Razor component author needs)
 * ----------------------------------------------------------------------------
 *
 * Render a normal <button> with these attributes and NO `@onclick`. This handler becomes the
 * single writer for that action, which is what guarantees a click is never applied twice (once
 * by Blazor, once by the queue). After the queue drains, the component is asked to reload from
 * server truth — see "REFRESHING AFTER A FLUSH" below.
 *
 *   REQUIRED on every action element
 *     data-bw-action="attempt" | "rating" | "favorite" | "comment"
 *     data-bw-boulder="<boulder guid>"
 *
 *   PER ACTION
 *     attempt   data-bw-type="Attempt" | "Send" | "Flash"
 *     rating    data-bw-stars="1".."5"
 *     favorite  data-bw-favorite="true" | "false"
 *                 The DESIRED state AFTER the click, not a toggle. Render the inverse of the
 *                 current state. Absolute values are what make a replay idempotent; a queued
 *                 toggle replayed twice flips back to where it started.
 *     comment   data-bw-text="<css selector of the textarea/input holding the text>"
 *                 The handler reads .value, enqueues it and clears the field.
 *
 *   OPTIONAL optimistic-UI hints (all are CSS selectors resolved against document)
 *     data-bw-on="<selector>"     elements that gain class `bw-opt-on`  (e.g. the filled heart)
 *     data-bw-off="<selector>"    elements that lose class `bw-opt-on`  (e.g. sibling stars)
 *     data-bw-count="<selector>"  element whose numeric text content is incremented by 1
 *     data-bw-pip="none"          suppress the little "queued" dot drawn on the button
 *
 *   OPTIONAL behaviour
 *     data-bw-mode="offline-only" only handle the click while the circuit is down; when the
 *                                 circuit is up the element's own `@onclick` runs instead.
 *                                 Use ONLY if you keep a Blazor handler on the element.
 *
 * Example (favourite button, currently not favourited):
 *   <button data-bw-action="favorite"
 *           data-bw-boulder="@Boulder.Id"
 *           data-bw-favorite="true"
 *           data-bw-on=".fav-icon-filled"
 *           class="icon-btn">…</button>
 *
 * ----------------------------------------------------------------------------
 * OPTIMISTIC STATE vs. SERVER RENDER — how they are kept from fighting
 * ----------------------------------------------------------------------------
 *
 * Two rules make this deterministic:
 *
 *  1. Every optimistic class this file adds is named `bw-opt-*` and is written onto elements
 *     that Blazor renders. When the circuit is alive and Blazor re-renders the component, it
 *     rewrites the `class` attribute from server truth and the optimistic class disappears on
 *     its own. Server truth always wins, with no reconciliation code.
 *
 *  2. The "queued" pip is a `<span class="bw-queued-pip">` this file creates. It is NOT in
 *     Blazor's render tree, so a re-render leaves it alone; it is removed here once the queue
 *     confirms the action reached the server. That is the one piece of state that must outlive
 *     a re-render, because it says "not yet saved".
 *
 * So: style anything reflecting *server* state normally, and let `bw-opt-on` be a purely visual
 * pre-echo of it.
 *
 * ----------------------------------------------------------------------------
 * REFRESHING AFTER A FLUSH
 * ----------------------------------------------------------------------------
 *
 * From a component's OnAfterRenderAsync(firstRender):
 *     _ref = DotNetObjectReference.Create(this);
 *     await JS.InvokeVoidAsync("blocwerkOfflineActions.registerRefresh", _ref);
 * and expose:
 *     [JSInvokable] public async Task OnOfflineQueueFlushed() { await LoadAsync(); StateHasChanged(); }
 * Dispose the reference in DisposeAsync. The method is invoked after any flush that actually
 * sent something, and after a circuit reconnect. Plain JS listeners can instead subscribe to the
 * `bw:offline-flushed` CustomEvent on `document`.
 */
(function () {
    const queue = window.blocwerkOfflineQueue;
    const refreshTargets = [];

    // Guards an accidental double-tap: two clicks on the same button within this window are one
    // action. Ratings and favourites are absolute so a repeat is harmless, but a second attempt
    // is a genuinely different row and must not be created by a fat finger.
    const DOUBLE_TAP_MS = 1200;
    const lastClick = new Map();

    function selectAll(selector) {
        if (!selector) {
            return [];
        }
        try {
            return Array.prototype.slice.call(document.querySelectorAll(selector));
        } catch (err) {
            console.warn('[blocwerk-offline] bad selector', selector, err);
            return [];
        }
    }

    function addPip(element, clientRequestId) {
        if (element.getAttribute('data-bw-pip') === 'none') {
            return;
        }
        const pip = document.createElement('span');
        pip.className = 'bw-queued-pip';
        pip.setAttribute('data-bw-req', clientRequestId);
        pip.setAttribute('aria-hidden', 'true');
        pip.title = 'Saved on this device, waiting to sync';
        element.appendChild(pip);
    }

    function clearPips() {
        selectAll('.bw-queued-pip').forEach(pip => pip.remove());
    }

    function applyOptimistic(element) {
        selectAll(element.getAttribute('data-bw-on')).forEach(n => n.classList.add('bw-opt-on'));
        selectAll(element.getAttribute('data-bw-off')).forEach(n => n.classList.remove('bw-opt-on'));

        selectAll(element.getAttribute('data-bw-count')).forEach(n => {
            const current = parseInt((n.textContent || '').replace(/[^0-9-]/g, ''), 10);
            if (!isNaN(current)) {
                n.textContent = String(current + 1);
            }
        });
    }

    function buildPayload(element) {
        const kind = element.getAttribute('data-bw-action');
        const boulderId = element.getAttribute('data-bw-boulder');

        if (!boulderId) {
            console.warn('[blocwerk-offline] data-bw-boulder missing on', element);
            return null;
        }

        if (kind === 'attempt') {
            // Capture the real moment of the tap. When the circuit/network is down this entry may
            // not reach the server for minutes or hours, and the server-side 60s debounce anchors
            // on this timestamp — without it, a whole offline batch would replay stamped at
            // reconnect time, collapse inside one 60s window, and silently lose genuinely distinct
            // attempts.
            return {
                boulderId: boulderId,
                type: element.getAttribute('data-bw-type') || 'Attempt',
                timestamp: new Date().toISOString()
            };
        }
        if (kind === 'rating') {
            return { boulderId: boulderId, stars: parseInt(element.getAttribute('data-bw-stars'), 10) };
        }
        if (kind === 'favorite') {
            return { boulderId: boulderId, favorite: element.getAttribute('data-bw-favorite') === 'true' };
        }
        if (kind === 'comment') {
            const source = document.querySelector(element.getAttribute('data-bw-text') || '');
            const text = source && source.value ? source.value.trim() : '';
            if (!text) {
                return null;
            }
            return { boulderId: boulderId, text: text, __source: source };
        }

        return null;
    }

    function isDoubleTap(element, kind) {
        const key = kind + '|' + (element.getAttribute('data-bw-boulder') || '') + '|' +
            (element.getAttribute('data-bw-type') || '') + '|' +
            (element.getAttribute('data-bw-stars') || '');
        const now = Date.now();
        const previous = lastClick.get(key) || 0;
        lastClick.set(key, now);
        return now - previous < DOUBLE_TAP_MS;
    }

    function handle(event) {
        const element = event.target instanceof Element
            ? event.target.closest('[data-bw-action]')
            : null;

        if (!element || element.hasAttribute('disabled')) {
            return;
        }

        const kind = element.getAttribute('data-bw-action');
        if (!kind) {
            return;
        }

        const offlineOnly = element.getAttribute('data-bw-mode') === 'offline-only';
        if (offlineOnly && window.blocwerkConnection && window.blocwerkConnection.isUp()) {
            return;
        }

        if (isDoubleTap(element, kind)) {
            event.preventDefault();
            return;
        }

        const payload = buildPayload(element);
        if (!payload) {
            return;
        }

        event.preventDefault();

        const source = payload.__source;
        delete payload.__source;

        applyOptimistic(element);

        queue.enqueue(kind, payload).then(entry => {
            addPip(element, entry.clientRequestId);
            if (source) {
                source.value = '';
                source.dispatchEvent(new Event('input', { bubbles: true }));
            }
        }).catch(err => {
            console.error('[blocwerk-offline] enqueue failed', err);
            window.dispatchEvent(new CustomEvent('bw:offline-error', {
                detail: { message: err && err.message ? err.message : 'Could not save this action.' }
            }));
        });
    }

    document.addEventListener('click', handle, true);

    function notifyRefreshTargets() {
        refreshTargets.slice().forEach(ref => {
            try {
                ref.invokeMethodAsync('OnOfflineQueueFlushed').catch(() => {
                    // Circuit died between the flush and the callback; the next reconnect
                    // refreshes anyway.
                });
            } catch (err) {
                const i = refreshTargets.indexOf(ref);
                if (i >= 0) {
                    refreshTargets.splice(i, 1);
                }
            }
        });
    }

    document.addEventListener('bw:offline-flushed', () => {
        queue.pending().then(entries => {
            if (entries.length === 0) {
                clearPips();
            }
            notifyRefreshTargets();
        });
    });

    window.blocwerkOfflineActions = {
        registerRefresh: function (dotNetRef) {
            refreshTargets.push(dotNetRef);
        },
        unregisterRefresh: function (dotNetRef) {
            const i = refreshTargets.indexOf(dotNetRef);
            if (i >= 0) {
                refreshTargets.splice(i, 1);
            }
        },
        clearPips: clearPips,
        notifyRefreshTargets: notifyRefreshTargets
    };
})();
