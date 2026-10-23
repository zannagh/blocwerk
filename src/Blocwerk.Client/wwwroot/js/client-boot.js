/*
 * The offline client's start (plan 2026-10-22, PR 3.8). blazor.webassembly.js is loaded with autostart="false" and
 * started here, so a failed start never leaves the shell on "Opening the offline view…". A rejected Blazor.start(), an
 * unhandled error or rejection before the first render (the layout's `bw-client-ready` mark), or no first render within
 * WATCHDOG_MS replaces #app with "Couldn't open the offline view", a Retry button (a reload) and an "Open online" link
 * to the same path outside /v. A failed runtime download does not reject Blazor.start(): its promise never settles,
 * and the runtime reports the failure only as unhandled rejections, hence the listeners.
 */
(function (win, doc) {
    'use strict';

    const READY_MARK = 'bw-client-ready';
    const WATCHDOG_MS = 20000;
    const BASE = '/v/';
    let failed = false;

    const ready = () => win.performance.getEntriesByName(READY_MARK).length > 0;

    // The Server page of this client path: the base dropped, the query kept, never a protocol-relative URL.
    function onlinePath() {
        const path = win.location.pathname;
        const rest = path.toLowerCase().startsWith(BASE) ? path.slice(BASE.length) : '';
        return '/' + rest.replace(/^[\/\\]+/, '') + win.location.search;
    }

    function show(reason) {
        if (failed || ready()) {
            return;
        }
        failed = true;
        console.error('Blocwerk offline client: the start failed', reason);
        const box = doc.createElement('section');
        box.className = 'bw-client-empty bw-client-failed';
        box.setAttribute('role', 'alert');
        const title = doc.createElement('h1');
        title.textContent = "Couldn't open the offline view";
        const text = doc.createElement('p');
        text.textContent = 'Something went wrong while it was starting.';
        const retry = doc.createElement('button');
        retry.type = 'button';
        retry.className = 'bw-client-retry';
        retry.textContent = 'Retry';
        retry.addEventListener('click', () => win.location.reload());
        const online = doc.createElement('a');
        online.className = 'bw-client-online';
        online.href = onlinePath();
        online.textContent = 'Open online';
        box.append(title, text, retry, online);
        (doc.getElementById('app') || doc.body).replaceChildren(box);
    }

    win.addEventListener('unhandledrejection', e => show(e.reason));
    win.addEventListener('error', e => show(e.error || e.message));
    win.setTimeout(() => show(new Error(`no first render within ${WATCHDOG_MS} ms`)), WATCHDOG_MS);
    try {
        win.Blazor.start().catch(show);
    } catch (e) {
        show(e);
    }
})(window, document);
