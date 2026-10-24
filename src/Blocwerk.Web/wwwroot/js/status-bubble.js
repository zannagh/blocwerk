/*
 * bwStatus — the short status bubbles of the connection / sync pill: offline mode available, switched to offline or
 * online mode, uploading changes, changes uploaded, and changes that could not be uploaded.
 *
 * A bubble is an EVENT ("this just happened"), told once, for a few seconds. The pill's lasting states (Offline,
 * Reconnecting, Sign in to sync, Blocwerk is updating…, and the badge of actions that could not be uploaded) stay in
 * offline-status.js; while a bubble is up it takes the pill's label, except over an urgent lasting state, and when it
 * times out the pill falls back to the lasting state, or goes away.
 *
 * API (window.bwStatus), for this app and for the offline client:
 *   show(kind, opts) -> bool   Shows the bubble of `kind` (one of KINDS). opts: { count, detail, onOpen }.
 *                              `count` goes into the label ("Uploading 3 changes"); `onOpen` makes a bubble the
 *                              offline-status pill does not render tappable (the conflict list of another client).
 *                              False when it was not shown: a repeat, a bubble that may not replace the one up (see
 *                              REPLACING), a kind this page does not show (offline-ready on a kiosk), or an unknown kind.
 *   hide(kind)                 Takes the bubble down if `kind` is showing (any bubble when omitted).
 *   current()                  The bubble showing: { kind, label, tone, live, detail, onOpen }, or null.
 *   subscribe(cb) -> unsub     cb() on every change. A page with a subscriber renders the bubble itself (offline-status.js
 *                              does); a page without one gets a pill of the same look from this file.
 *   label(kind, opts)          The wording, without showing anything.
 *
 * REPLACING: the newest bubble wins, so offline, online, offline always ends on the true state. Only the same kind with
 * the same count is dropped, while it is on screen. Two exceptions: "Offline mode available" shows only when no other
 * bubble is up, and a conflict, for its few seconds, gives way only to a change of connection or a newer conflict (an
 * "uploaded" from the same flush must not paint over it). The conflict itself stays in the pill as a badge until
 * dismissed (offline-status.js).
 *
 * Every bubble is also spoken: politely through a role=status region, if it is still the one up when spoken; a
 * conflict assertively through a role=alert one, always.
 */
window.bwStatus = (function (win) {
    'use strict';

    const plural = (n, one, many) => (n === 1 ? one : many);
    const changes = n => (typeof n === 'number' && n > 0 ? n + ' ' + plural(n, 'change', 'changes') : 'changes');
    const changesTitle = n => changes(n).replace(/^c/, 'C');

    // tone: the pill's colour (bw-conn-<tone>). ms: how long it stays. detail: the pill panel's sentence, unless show() is
    // given one. yieldsTo: the only kinds that may replace this one while it is up (any kind when absent).
    const KINDS = Object.freeze({
        'offline-ready': {
            tone: 'ok', ms: 5000, text: () => 'Offline mode available',
            detail: 'Your walls are saved on this device, so they open without a connection.'
        },
        online: { tone: 'ok', ms: 3000, text: () => 'Switched to online mode', detail: 'Connected again.' },
        offline: {
            tone: 'warn', ms: 4000, text: () => 'Switched to offline mode',
            detail: 'No connection. Ascents, ratings and comments still work and sync later.'
        },
        uploading: { tone: 'info', ms: 4000, text: o => 'Uploading ' + changes(o.count), detail: 'Sending what you did while offline.' },
        uploaded: { tone: 'ok', ms: 3000, text: o => changesTitle(o.count) + ' uploaded', detail: 'Everything you did while offline is saved.' },
        conflict: {
            tone: 'error', ms: 6000, live: 'assertive', yieldsTo: ['offline', 'online', 'conflict'],
            text: o => changesTitle(o.count) + ' could not be uploaded',
            detail: 'Some actions could not be saved. They are listed below.'
        }
    });

    const subscribers = [];
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
        const key = kind + ':' + (typeof o.count === 'number' ? o.count : '');
        if (showing && (showing.key === key || !mayReplace(showing.kind, kind))) {
            return false;
        }
        showing = {
            kind, key, tone: spec.tone, live: spec.live || 'polite',
            label: spec.text(o), detail: o.detail || spec.detail, onOpen: typeof o.onOpen === 'function' ? o.onOpen : null
        };
        win.clearTimeout(timer);
        timer = win.setTimeout(() => hide(kind), spec.ms);
        announce(showing);
        publish();
        return true;
    }

    function mayReplace(up, next) {
        const yieldsTo = KINDS[up].yieldsTo;
        return next !== 'offline-ready' && (!yieldsTo || yieldsTo.includes(next));
    }

    function hide(kind) {
        if (!showing || (kind && showing.kind !== kind)) {
            return;
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
        // A renderer took over: the pill this file drew before it came goes.
        const own = win.document && win.document.querySelector('.bw-status-host');
        if (own) {
            own.textContent = '';
        }
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
        win.setTimeout(() => {
            if (bubble.live === 'assertive' || showing === bubble) {
                el.textContent = bubble.label;
            }
        }, 50);
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
