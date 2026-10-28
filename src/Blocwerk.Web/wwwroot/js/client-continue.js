/*
 * bwClientContinue — "Continue offline" in the Server page's reconnect UI (Phase 3 plan §1, PR 3.14).
 *
 * When the circuit drops (blocwerkConnection: reconnecting, failed or rejected) or the device goes offline, the page
 * asks its worker whether the offline client can show this path (`client-target`: a complete client version that
 * covers the route, a `u-`/`anon` partition, no lock; service-worker.js). The connection pill's panel (offline-status.js)
 * then offers "Continue offline", a full navigation to the client's /v/ twin, which the worker serves from the device.
 * Never while a deploy is announced (bwMaintenance: the page reloads itself into the new version), and never for a path
 * the page has left since it asked.
 *
 * The other way: a Server page opened by the client's hand-back (client-handback.js sets sessionStorage `bw-handback`
 * just before it navigates) tells window.bwStatus "online" once, when the page has it.
 * Nothing here throws into the page.
 */
window.bwClientContinue = (function (win, doc) {
    'use strict';

    const PROTO = 1;
    const ASK_MS = 3000;
    const HANDBACK_KEY = 'bw-handback';
    const HANDBACK_FRESH_MS = 60000;
    const STAY_KEY = 'bw-stay-offline';
    const EDITING_MESSAGE = 'Unsaved changes in the editor will be lost. Continue offline anyway?';

    const state = { path: null, target: null, asking: null };

    const here = () => win.location.pathname + win.location.search;

    function down() {
        const connection = win.blocwerkConnection ? win.blocwerkConnection.status() : 'connected';
        const queue = win.blocwerkOfflineQueue ? win.blocwerkOfflineQueue.state() : { online: true };
        return connection !== 'connected' || queue.online === false;
    }

    function deploying() {
        const m = win.bwMaintenance;
        return !!(m && typeof m.active === 'function' && m.active());
    }

    function askWorker(path) {
        const sw = win.navigator.serviceWorker;
        const controller = sw && sw.controller;
        if (!controller || typeof win.MessageChannel !== 'function') {
            return Promise.resolve(null);
        }
        return new Promise(resolve => {
            const channel = new win.MessageChannel();
            const timer = win.setTimeout(() => resolve(null), ASK_MS);
            channel.port1.onmessage = event => {
                win.clearTimeout(timer);
                const data = event.data;
                resolve(data && data.proto === PROTO && data.type === 'client-target' && typeof data.target === 'string' ? data.target : null);
            };
            try {
                controller.postMessage({ proto: PROTO, type: 'client-target', path }, [channel.port2]);
            } catch (e) {
                win.clearTimeout(timer);
                resolve(null);
            }
        });
    }

    /** Asks the worker for this path's client URL, then redraws the pill. Resolves the target (or null). */
    function refresh() {
        const path = here();
        if (state.asking && state.asking.path === path) {
            return state.asking.done;
        }
        const done = askWorker(path).catch(() => null).then(target => {
            if (state.asking && state.asking.done === done) {
                state.asking = null;
            }
            state.path = path;
            state.target = target && target.startsWith('/v/') ? target : null;
            if (win.blocwerkStatus && typeof win.blocwerkStatus.render === 'function') {
                win.blocwerkStatus.render();
            }
            return state.target;
        });
        state.asking = { path, done };
        return done;
    }

    /** The client URL "Continue offline" opens now, or null: only while the connection is down and no deploy is announced. */
    function target() {
        return down() && !deploying() && state.path === here() ? state.target : null;
    }

    function go() {
        const url = target();
        if (!url) {
            return false;
        }
        // The circuit may come back and keep an open editor's work; the client is read-only and starts clean.
        if (win.bwEditGuard && win.bwEditGuard.isEditing() && !(typeof win.confirm === 'function' && win.confirm(EDITING_MESSAGE))) {
            return false;
        }
        try {
            win.sessionStorage.setItem(STAY_KEY, String(Date.now()));
        } catch (e) {
            // Without it the client hands back on the first tap while the probe says online.
        }
        win.location.assign(url);
        return true;
    }

    function onChange() {
        if (down() && !(state.path === here() && state.target)) {
            refresh();
        }
    }

    // Opened by the client's hand-back: "Switched to online mode", once.
    function welcomeBack() {
        let at = null;
        try {
            at = Number(win.sessionStorage.getItem(HANDBACK_KEY));
            win.sessionStorage.removeItem(HANDBACK_KEY);
        } catch (e) {
            return false;
        }
        if (!at || Date.now() - at > HANDBACK_FRESH_MS || Date.now() < at) {
            return false;
        }
        try {
            if (win.bwStatus && typeof win.bwStatus.show === 'function') {
                win.bwStatus.show('online');
            }
        } catch (e) {
            // A bubble is a courtesy.
        }
        return true;
    }

    if (win.blocwerkConnection) {
        win.blocwerkConnection.subscribe(onChange);
    }
    if (win.blocwerkOfflineQueue) {
        win.blocwerkOfflineQueue.subscribe(onChange);
    }
    if (doc.readyState === 'loading') {
        doc.addEventListener('DOMContentLoaded', () => win.setTimeout(welcomeBack, 0));
    } else {
        win.setTimeout(welcomeBack, 0);
    }

    return { refresh, target, go, welcomeBack };
})(window, document);
