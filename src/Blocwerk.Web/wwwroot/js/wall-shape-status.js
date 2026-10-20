/*
 * Pen policy and status messages for the Shape tool (wall-shape-drag.js).
 *
 *  - penPolicy: once a pen was used, fingers pan and only the pen draws ("pen only"); that mode ends by itself
 *    PEN_ONLY_MS after the last pen input, so a member who put the pencil down can paint with a finger again
 *    without reloading. While a pen is down (and PALM_MS after it lifts) a touch is a resting palm. Pure, so
 *    node tests it (test/js/shape-tools.test.mjs).
 *  - the "Pen only" pill, shown at the photo's corner while that mode is on;
 *  - a toast for an edit the server never confirmed (the circuit dropped while it was being saved).
 *
 * The pill and the toast are appended to <body>, outside every Blazor-rendered element, so no re-render can
 * remove them or be confused by them.
 */
(function (root, factory) {
    'use strict';
    const api = factory();
    if (typeof module === 'object' && module.exports) {
        module.exports = api;
    } else {
        root.bwShapeStatus = api;
    }
})(typeof self !== 'undefined' ? self : this, function () {
    'use strict';

    const PEN_ONLY_MS = 30000;
    const PALM_MS = 600;
    const TOAST_MS = 6000;

    /** The pen state; `now` is injectable for the tests. */
    function penPolicy(now) {
        const clock = now || function () { return performance.now(); };
        const down = new Set();
        let lastPen = -Infinity;
        let upAt = -Infinity;
        return {
            /** Any pen input: down, move or up. */
            touch: function () { lastPen = clock(); },
            down: function (id) { down.add(id); lastPen = clock(); },
            up: function (id) {
                down.delete(id);
                upAt = clock();
                lastPen = upAt;
            },
            /** A touch now is the palm beside the pen. */
            palm: function () { return down.size > 0 || clock() - upAt < PALM_MS; },
            /** Fingers pan rather than paint. */
            penOnly: function () { return down.size > 0 || clock() - lastPen < PEN_ONLY_MS; },
        };
    }

    let pill = null;
    let toastEl = null;
    let toastTimer = 0;

    function element(cls, css) {
        const el = document.createElement('div');
        el.className = cls;
        el.setAttribute('role', 'status');
        el.style.cssText = 'position:fixed;z-index:2000;pointer-events:none;font:600 12px/1.2 system-ui,sans-serif;'
            + 'color:#fff;background:rgba(20,20,28,0.88);border-radius:999px;' + css;
        document.body.appendChild(el);
        return el;
    }

    /** Shows (or hides, with no container) the "Pen only" pill at the photo viewport's top-left corner. */
    function showPenOnly(container) {
        const viewport = container && container.closest('.photo-viewport');
        if (!viewport) {
            if (pill) {
                pill.style.display = 'none';
            }

            return;
        }

        pill = pill || element('shape-pen-only', 'padding:4px 10px;');
        pill.textContent = 'Pen only: fingers pan';
        const r = viewport.getBoundingClientRect();
        pill.style.left = Math.round(r.left + 8) + 'px';
        pill.style.top = Math.round(r.top + 8) + 'px';
        pill.style.display = '';
    }

    function toast(message) {
        toastEl = toastEl || element('shape-edit-toast', 'left:50%;bottom:24px;transform:translateX(-50%);padding:8px 14px;max-width:90vw;text-align:center;');
        toastEl.textContent = message;
        toastEl.style.display = '';
        clearTimeout(toastTimer);
        toastTimer = setTimeout(hideToast, TOAST_MS);
    }

    function hideToast() {
        if (toastEl) {
            toastEl.style.display = 'none';
        }
    }

    return { penPolicy: penPolicy, showPenOnly: showPenOnly, toast: toast, hideToast: hideToast, PEN_ONLY_MS: PEN_ONLY_MS };
});
