/*
 * bwSwGuard — the worker-loss guard (plan 2026-10-22, PR 3.9; from the WebKit offline flake, PR #207).
 *
 * When WebKit's networking process dies (on iOS under memory pressure; Linux WPE's libsoup crash in CI), the page
 * keeps `navigator.serviceWorker.controller` but the worker behind it is gone: it answers no message, the page's
 * requests bypass it, and the next offline navigation gets the browser's error page instead of the offline client.
 * A reload attaches the page to a live worker again.
 *
 * So on start, and when the page comes back after being hidden for RECHECK_HIDDEN_MS, a controlled page asks the
 * worker `sw-info`. Every worker since blocwerk-shell-v4 answers it. Only when two asks in a row go unanswered
 * (PING_MS each: a cold worker start can be slow) is the worker taken as lost, and then:
 *   - reload, when the server answers /alive (so the reload lands on a real page), no editor holds unsaved work
 *     (bwEditGuard), and this tab has not reloaded for this within RELOAD_EVERY_MS (sessionStorage);
 *   - otherwise fall back: `data-bw-worker="lost"` on <html> and a `bw-worker-lost` event, for the page to show.
 * A page with no controller has nothing to lose and is left alone. Nothing here throws into the page.
 */
window.bwSwGuard = (function (win) {
    'use strict';

    const PROTO = 1;
    const PING_MS = 4000;
    const ALIVE_MS = 3000;
    const RELOAD_KEY = 'bw-sw-guard-reload';
    const RELOAD_EVERY_MS = 5 * 60 * 1000;
    const RECHECK_HIDDEN_MS = 30 * 1000;

    const state = { checking: null, hiddenAt: null };

    // true: the controller answered; false: it did not; null: there is no controller to ask.
    function ping() {
        const sw = win.navigator.serviceWorker;
        const controller = sw && sw.controller;
        if (!controller || typeof win.MessageChannel !== 'function') {
            return Promise.resolve(null);
        }
        return new Promise(resolve => {
            const channel = new win.MessageChannel();
            const timer = win.setTimeout(() => resolve(false), PING_MS);
            channel.port1.onmessage = event => {
                win.clearTimeout(timer);
                resolve(!!event.data && event.data.proto === PROTO && event.data.type === 'sw-info');
            };
            try {
                controller.postMessage({ proto: PROTO, type: 'sw-info' }, [channel.port2]);
            } catch (e) {
                win.clearTimeout(timer);
                resolve(false);
            }
        });
    }

    function serverUp() {
        const controller = typeof win.AbortController === 'function' ? new win.AbortController() : null;
        const timer = win.setTimeout(() => controller && controller.abort(), ALIVE_MS);
        return win.fetch('/alive', { cache: 'no-store', credentials: 'same-origin', signal: controller ? controller.signal : undefined })
            .then(response => response.ok, () => false)
            .finally(() => win.clearTimeout(timer));
    }

    function lastReload() {
        try {
            return Number(win.sessionStorage.getItem(RELOAD_KEY)) || 0;
        } catch (e) {
            return Date.now();
        }
    }

    function mayReload() {
        const editing = !!(win.bwEditGuard && typeof win.bwEditGuard.isEditing === 'function' && win.bwEditGuard.isEditing());
        return !editing && Date.now() - lastReload() >= RELOAD_EVERY_MS;
    }

    async function lost() {
        if (mayReload() && await serverUp()) {
            try {
                win.sessionStorage.setItem(RELOAD_KEY, String(Date.now()));
            } catch (e) {
                // lastReload() reads "just now" without storage, so this reload is the only one.
            }
            win.location.reload();
            return 'reloading';
        }
        win.document.documentElement.setAttribute('data-bw-worker', 'lost');
        win.dispatchEvent(new win.CustomEvent('bw-worker-lost'));
        return 'lost';
    }

    async function examine() {
        const first = await ping();
        if (first !== false) {
            return first === null ? 'uncontrolled' : 'alive';
        }
        const second = await ping();
        if (second !== false) {
            return second === null ? 'uncontrolled' : 'alive';
        }
        return lost();
    }

    // One check at a time; resolves 'alive', 'uncontrolled', 'reloading' or 'lost'.
    function check() {
        if (!state.checking) {
            state.checking = examine().catch(() => 'alive').then(result => {
                state.checking = null;
                return result;
            });
        }
        return state.checking;
    }

    function onVisibility() {
        if (win.document.visibilityState === 'hidden') {
            state.hiddenAt = Date.now();
        } else if (state.hiddenAt !== null && Date.now() - state.hiddenAt >= RECHECK_HIDDEN_MS) {
            state.hiddenAt = null;
            check();
        }
    }

    function boot() {
        if (!win.navigator.serviceWorker) {
            return Promise.resolve('uncontrolled');
        }
        win.document.addEventListener('visibilitychange', onVisibility);
        state.first = check();
        return state.first;
    }

    return { boot, check, get first() { return state.first; } };
})(window);

if (!window.__bwSwGuardManual) {
    window.bwSwGuard.boot();
}
