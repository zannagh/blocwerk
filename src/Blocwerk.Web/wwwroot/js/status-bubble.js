/*
 * bwStatus — the short status bubbles of the connection / sync pill: offline mode available, switched to offline or
 * online mode, uploading changes, changes uploaded, and changes that could not be uploaded.
 *
 * A bubble is an EVENT ("this just happened"), told once. The pill's lasting states (Offline, Reconnecting, Sign in
 * to sync, Blocwerk is updating…) stay in offline-status.js; while a bubble is up it takes the pill's label, and when
 * it times out the pill falls back to the lasting state, or goes away.
 *
 * API (window.bwStatus), for this app and for the offline client:
 *   show(kind, opts) -> bool   Shows the bubble of `kind` (one of KINDS). opts: { count, detail, onOpen }.
 *                              `count` goes into the label ("Uploading 3 changes"); `onOpen` makes a bubble the
 *                              offline-status pill does not render tappable (the conflict list of another client).
 *                              False when it was not shown: a repeat or a less important kind (see DEDUPE), a kind
 *                              this page does not show (offline-ready on a kiosk), or an unknown kind.
 *   hide(kind)                 Takes the bubble down if `kind` is showing (any bubble when omitted).
 *   current()                  The bubble showing: { kind, label, tone, sticky, live, detail, onOpen }, or null.
 *   subscribe(cb) -> unsub     cb() on every change. A page with a subscriber renders the bubble itself (offline-status.js
 *                              does); a page without one gets a pill of the same look from this file.
 *   label(kind, opts)          The wording, without showing anything.
 *
 * DEDUPE: the same kind with the same count is not shown again while it is up, nor within DEDUPE_MS of it being
 * shown. A bubble up is not replaced by a less important one (KINDS' rank: offline-ready < online < offline, uploading,
 * uploaded < conflict); that one is dropped, not queued. A conflict stays until hidden (the user dismissed it, or the
 * queue has nothing rejected any more).
 *
 * Every bubble is also spoken: politely through a role=status region, a conflict assertively through a role=alert one.
 */
window.bwStatus = (function (win) {
    'use strict';

    const DEDUPE_MS = 30000;
    const plural = (n, one, many) => (n === 1 ? one : many);
    const changes = n => (typeof n === 'number' && n > 0 ? n + ' ' + plural(n, 'change', 'changes') : 'changes');
    const changesTitle = n => changes(n).replace(/^c/, 'C');

    // rank: a bubble up is not replaced by one of a lower rank. tone: the pill's colour (bw-conn-<tone>). ms: how long
    // it stays; 0 = until hidden. detail: the pill panel's sentence, unless show() is given one. replaces: the one kind
    // of a higher rank this one takes down all the same (back online within seconds of going offline).
    const KINDS = Object.freeze({
        'offline-ready': {
            rank: 0, tone: 'ok', ms: 5000, text: () => 'Offline mode available',
            detail: 'Your walls are saved on this device, so they open without a connection.'
        },
        online: { rank: 1, replaces: 'offline', tone: 'ok', ms: 3000, text: () => 'Switched to online mode', detail: 'Connected again.' },
        offline: {
            rank: 2, tone: 'warn', ms: 4000, text: () => 'Switched to offline mode',
            detail: 'No connection. Ascents, ratings and comments still work and sync later.'
        },
        uploading: { rank: 2, tone: 'info', ms: 4000, text: o => 'Uploading ' + changes(o.count), detail: 'Sending what you did while offline.' },
        uploaded: { rank: 2, tone: 'ok', ms: 3000, text: o => changesTitle(o.count) + ' uploaded', detail: 'Everything you did while offline is saved.' },
        conflict: {
            rank: 3, tone: 'error', ms: 0, live: 'assertive', text: o => changesTitle(o.count) + ' could not be uploaded',
            detail: 'Some actions could not be saved. They are listed below.'
        }
    });

    const subscribers = [];
    const lastShown = new Map();
    let showing = null;
    let timer = null;

    function label(kind, opts) {
        const spec = KINDS[kind];
        return spec ? spec.text(opts || {}) : '';
    }

    function isKiosk() {
        const body = win.document && win.document.body;
        return !!(body && body.dataset && body.dataset.bwKiosk);
    }

    function show(kind, opts) {
        const spec = KINDS[kind];
        const o = opts || {};
        if (!spec || (kind === 'offline-ready' && isKiosk())) {
            return false;
        }
        if (showing && spec.rank < KINDS[showing.kind].rank && spec.replaces !== showing.kind) {
            return false;
        }
        const key = kind + ':' + (typeof o.count === 'number' ? o.count : '');
        const now = Date.now();
        if ((showing && showing.key === key) || (lastShown.has(key) && now - lastShown.get(key) < DEDUPE_MS)) {
            return false;
        }
        lastShown.set(key, now);
        showing = {
            kind, key, tone: spec.tone, sticky: spec.ms === 0, live: spec.live || 'polite',
            label: spec.text(o), detail: o.detail || spec.detail, onOpen: typeof o.onOpen === 'function' ? o.onOpen : null
        };
        win.clearTimeout(timer);
        timer = spec.ms ? win.setTimeout(() => hide(kind), spec.ms) : null;
        announce(showing);
        publish();
        return true;
    }

    function hide(kind) {
        if (!showing || (kind && showing.kind !== kind)) {
            return;
        }
        // A conflict taken down (dismissed, or resolved) may come back for the next one at once.
        if (showing.sticky) {
            lastShown.delete(showing.key);
        }
        showing = null;
        win.clearTimeout(timer);
        timer = null;
        publish();
    }

    function current() {
        return showing ? Object.assign({}, showing) : null;
    }

    function subscribe(cb) {
        subscribers.push(cb);
        return () => {
            const i = subscribers.indexOf(cb);
            if (i >= 0) {
                subscribers.splice(i, 1);
            }
        };
    }

    function publish() {
        if (subscribers.length === 0) {
            renderOwn();
            return;
        }
        subscribers.slice().forEach(cb => {
            try {
                cb();
            } catch (e) {
                /* a renderer's failure is not the bubble's */
            }
        });
    }

    function region(id, live) {
        const doc = win.document;
        let el = doc.getElementById(id);
        if (!el && doc.body) {
            el = doc.createElement('div');
            el.id = id;
            el.className = 'bw-status-live';
            el.setAttribute('role', live === 'assertive' ? 'alert' : 'status');
            el.setAttribute('aria-live', live);
            doc.body.appendChild(el);
        }
        return el;
    }

    // Emptied first and filled a moment later, so a screen reader speaks the same words again for a new event.
    function announce(bubble) {
        const el = region(bubble.live === 'assertive' ? 'bw-status-alert' : 'bw-status-polite', bubble.live);
        if (!el) {
            return;
        }
        el.textContent = '';
        win.setTimeout(() => { el.textContent = bubble.label; }, 50);
    }

    // The bubble of a page without offline-status.js: the pill's look, in a host of its own on <body>.
    function renderOwn() {
        const doc = win.document;
        if (!doc || !doc.body) {
            return;
        }
        let host = doc.querySelector('.bw-status-host');
        if (!host) {
            host = doc.createElement('div');
            host.className = 'bw-status-host';
            doc.body.appendChild(host);
        }
        host.textContent = '';
        if (!showing) {
            return;
        }
        host.appendChild(pill(doc, showing));
    }

    /** A pill element for `bubble` (bw-conn-pill look); a button when it has onOpen. */
    function pill(doc, bubble) {
        const el = doc.createElement(bubble.onOpen ? 'button' : 'div');
        el.className = 'bw-conn-pill bw-conn-' + bubble.tone + ' bw-status-bubble';
        el.setAttribute('data-bw-status', bubble.kind);
        if (bubble.onOpen) {
            el.type = 'button';
            el.addEventListener('click', () => bubble.onOpen());
        }
        const dot = doc.createElement('span');
        dot.className = 'bw-conn-dot';
        el.appendChild(dot);
        const text = doc.createElement('span');
        text.className = 'bw-conn-label';
        text.textContent = bubble.label;
        el.appendChild(text);
        return el;
    }

    return { KINDS, show, hide, current, subscribe, label };
})(window);
