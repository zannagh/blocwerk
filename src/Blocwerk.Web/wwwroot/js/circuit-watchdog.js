/*
 * The circuit watchdog: a lost connection the framework says nothing about.
 *
 * Blazor reconnects (and raises the reconnect states blazor-boot.js turns into 'reconnecting' / 'failed') only for a
 * circuit that is fully up. A connection lost while the circuit is still starting, or within its first moments, raises
 * nothing at all: the page keeps reading 'connected' with a dead socket, and the pill and the status bubbles never say
 * offline. A gym with patchy signal is exactly where that happens, on the page load as someone walks in.
 *
 * So this watches the circuit's WebSocket itself (loaded before blazor-boot.js starts Blazor):
 *   - the socket closes and the framework raised no reconnect state within DROP_GRACE_MS;
 *   - or a page that came with interactive server components (a `Blazor:{"type":"server"` marker) still has no open
 *     circuit (blocwerkConnection.circuitOpened) after START_TIMEOUT_MS: the start hung, offline, before or while
 *     opening its socket. (Blazor.start itself resolves at once and says nothing about the circuit.)
 * and either way, if the device is offline or the server does not answer, it calls blocwerkConnection.markDown().
 * That is the same 'failed' the framework's own give-up reports, so the pill says Offline/Reconnecting, the status
 * bubbles say "Switched to offline mode", and the probe gets the circuit back once the server answers. A socket that
 * closes with the server still answering is left alone: that is a navigation, or the framework's own business.
 */
(function (win) {
    'use strict';

    const DROP_GRACE_MS = 2000;
    const START_TIMEOUT_MS = 10000;
    const PROBE_URL = '/alive';
    const PROBE_TIMEOUT_MS = 4000;

    const connection = () => win.blocwerkConnection;

    function serverAnswers() {
        const abort = typeof AbortController === 'function' ? new AbortController() : null;
        const timer = abort ? setTimeout(() => abort.abort(), PROBE_TIMEOUT_MS) : null;
        return fetch(PROBE_URL, { cache: 'no-store', credentials: 'omit', signal: abort ? abort.signal : undefined })
            .then(response => response.ok, () => false)
            .then(ok => {
                clearTimeout(timer);
                return ok;
            });
    }

    function deviceOrServerDown() {
        return win.navigator.onLine === false ? Promise.resolve(true) : serverAnswers().then(ok => !ok);
    }

    // Down only while nothing else has spoken: the framework (a reconnect state since `since`) or, for the start
    // check, a circuit that opened meanwhile.
    function silent(since, untilOpened) {
        const c = connection();
        return !!c && c.status() === 'connected' && c.lastChangeAt() < since && !(untilOpened && c.circuitOpened());
    }

    function checkDown(since, untilOpened) {
        if (!silent(since, untilOpened)) {
            return;
        }
        deviceOrServerDown().then(down => {
            if (down && silent(since, untilOpened)) {
                connection().markDown();
            }
        });
    }

    // Whether the page as loaded asked for a circuit: the prerender's server component markers.
    function wantsCircuit() {
        const doc = win.document;
        if (!doc || typeof doc.createTreeWalker !== 'function') {
            return false;
        }
        const walker = doc.createTreeWalker(doc.documentElement, 128 /* NodeFilter.SHOW_COMMENT */);
        for (let node = walker.nextNode(); node; node = walker.nextNode()) {
            if (/^Blazor:\{"type":"(server|auto)"/.test(node.nodeValue)) {
                return true;
            }
        }
        return false;
    }

    function onSocketClosed() {
        const closedAt = Date.now();
        setTimeout(() => checkDown(closedAt, false), DROP_GRACE_MS);
    }

    const Native = win.WebSocket;
    if (typeof Native === 'function') {
        win.WebSocket = class extends Native {
            constructor(url, protocols) {
                super(url, protocols);
                if (String(url).indexOf('/_blazor') >= 0) {
                    this.addEventListener('close', onSocketClosed);
                }
            }
        };
    }

    const loadedAt = Date.now();
    // Read now: this script runs at the end of <body>, after the markers and before Blazor starts and takes them.
    const wanted = wantsCircuit();
    setTimeout(() => {
        if (wanted) {
            checkDown(loadedAt, true);
        }
    }, START_TIMEOUT_MS);
})(window);
