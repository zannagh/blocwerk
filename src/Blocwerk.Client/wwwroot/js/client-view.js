/*
 * bwClientView — what the offline client's views (Phase 3 PR 3.11) need from the browser, read through
 * JsClientViewEnv. Loaded by the shell after client-store.js; it fetches nothing and names no further file.
 *
 *   fitWidth()  the photo rendition the device cache holds for this screen: asset-plan.js's fitWidth over the window
 *               (the width ladder's first rung at or above the CSS width times the pixel ratio, capped at FIT_CAP).
 *               The Server pages cache exactly that rung and the 640 thumbnail, so the client asks for that URL and
 *               the worker answers it from the device. The node test pins it to asset-plan.js's own function.
 *   tzOffset()  minutes to add to UTC for local time now (the client runs without time zone data).
 *   tzOffsets('ms,ms')  the same at each of the given moments (a summer date across a DST change), as 'm,m'.
 * The theme: the Server pages' stored choice (theme.js, `blocwerk-theme`) is applied at load, so the client looks
 * like the page it stands in for.
 */
window.bwClientView = (function (win) {
    'use strict';

    const WIDTHS = [640, 1280, 1920, 2560];
    const FIT_CAP = 1920;
    const THEME_KEY = 'blocwerk-theme';

    function fitWidthOf(cssWidth, dpr) {
        const needed = (cssWidth || 0) * (dpr || 1);
        const rung = WIDTHS.find(w => w >= needed) || WIDTHS[WIDTHS.length - 1];
        return Math.min(rung, FIT_CAP);
    }

    function applyTheme() {
        try {
            const stored = win.localStorage.getItem(THEME_KEY);
            if (stored === 'light' || stored === 'dark') {
                win.document.documentElement.setAttribute('data-theme', stored);
            }
        } catch {
            // No storage: the system's scheme decides, as on the Server pages.
        }
    }

    applyTheme();

    return {
        fitWidthOf,
        fitWidth: () => fitWidthOf(win.innerWidth, win.devicePixelRatio),
        tzOffset: () => -new Date().getTimezoneOffset(),
        tzOffsets: list => String(list || '').split(',').filter(Boolean)
            .map(ms => -new Date(Number(ms)).getTimezoneOffset()).join(',')
    };
})(window);
