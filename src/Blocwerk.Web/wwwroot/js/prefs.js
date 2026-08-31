/*
 * bwPrefs — tiny cookie-backed client preferences the server can also read.
 *
 * Only the boulder-setting experience toggle lives here for now. The value is written as a
 * plain cookie (not localStorage) so the server can read it on the initial page load and pick
 * the right experience with no client-side flash. Path=/ + a one-year max-age + SameSite=Lax
 * mirrors cookie-consent.js.
 */
window.bwPrefs = (function () {
    'use strict';

    const EXPERIENCE_KEY = 'blocwerk-boulder-experience';
    const LAST_PAGE_KEY = 'blocwerk-last-page';
    const ZOOM_LENS_MAG_KEY = 'blocwerk-zoom-lens-mag';
    const ZOOM_LENS_MAG_DEFAULT = 8;
    const ZOOM_LENS_MAG_MIN = 2;
    const ZOOM_LENS_MAG_MAX = 16;
    const ONE_YEAR = 60 * 60 * 24 * 365;

    function readCookie(name) {
        const prefix = name + '=';
        const parts = document.cookie ? document.cookie.split(';') : [];
        for (let i = 0; i < parts.length; i++) {
            const c = parts[i].trim();
            if (c.indexOf(prefix) === 0) {
                return decodeURIComponent(c.substring(prefix.length));
            }
        }
        return null;
    }

    function writeCookie(name, value) {
        // Secure only over https, so the cookie still writes on a plain-http localhost dev server.
        const secure = location.protocol === 'https:' ? ';Secure' : '';
        document.cookie = name + '=' + encodeURIComponent(value) +
            ';path=/;max-age=' + ONE_YEAR + ';SameSite=Lax' + secure;
    }

    // Paths we never remember as the "last page", so the homepage redirect can't loop or drop a
    // returning user back into a flow that doesn't make sense to resume. This MUST stay consistent
    // with the server-side LastPageRedirect.IsSafeTarget check:
    //   - "/" itself (that's where the redirect starts — recording it would loop),
    //   - "/account" (settings/profile), "/login", "/logout" (auth flow),
    //   - "/join/{token}" invite links,
    //   - "/home" (the resume redirect itself — recording it would loop),
    //   - any path with a "/shared/" segment (e.g. "/walls/{id}/boulders/{bid}/shared/{token}")
    //     — don't resurrect a one-off share-token URL.
    const NON_RECORDABLE = /^\/$|^\/(account|login|logout)(\/|$)|^\/join(\/|$)|^\/home(\/|$)|\/shared\//;

    // A single UUID path segment, and a full boulder-DETAIL path built from two of them:
    //   "/walls/{wallId}/boulders/{boulderId}". We record such a detail as its boulder LIST
    //   ("/walls/{wallId}?view=boulders") instead, so returning from a boulder lands on the list
    //   rather than resuming an individual boulder. The trailing "$" means a "/shared/{token}"
    //   variant never matches here (it carries extra segments and is excluded above anyway).
    const UUID = '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}';
    const BOULDER_DETAIL = new RegExp('^/walls/(' + UUID + ')/boulders/' + UUID + '$');

    function isRecordablePath(path) {
        if (!path) {
            return false;
        }
        return !NON_RECORDABLE.test(path);
    }

    function recordLastPage() {
        // Defensive: never let a blocked cookie store or an odd location throw and break navigation.
        try {
            const path = location.pathname;
            if (!isRecordablePath(path)) {
                return;
            }

            // Collapse a boulder-detail view to its boulder list so returning lands on the list.
            const detail = path.match(BOULDER_DETAIL);
            const value = detail
                ? '/walls/' + detail[1] + '?view=boulders'
                : path + location.search;

            writeCookie(LAST_PAGE_KEY, value);
        } catch (e) {
            /* storage blocked or unavailable — silently skip. */
        }
    }

    // Record on the first load and on every SPA/enhanced navigation. Enhanced navigation morphs the
    // server DOM over ours without a full page load, so `enhancedload` (mirroring theme.js) is the
    // primary hook; popstate covers back/forward, and the initial call covers the first paint.
    function install() {
        try {
            recordLastPage();

            window.addEventListener('popstate', recordLastPage);

            // Blazor.start() runs later (blazor-boot.js), so poll briefly for the API and attach the
            // enhancedload listener once it exists rather than assuming it's ready at head-parse time.
            let tries = 0;
            const timer = setInterval(function () {
                tries++;
                if (window.Blazor && window.Blazor.addEventListener) {
                    window.Blazor.addEventListener('enhancedload', recordLastPage);
                    clearInterval(timer);
                } else if (tries > 100) {
                    clearInterval(timer);
                }
            }, 100);
        } catch (e) {
            /* never block page startup on recording. */
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', install);
    } else {
        install();
    }

    return {
        getExperience: function () {
            const v = readCookie(EXPERIENCE_KEY);
            return v === 'new' || v === 'old' ? v : null;
        },
        setExperience: function (value) {
            const v = value === 'new' ? 'new' : 'old';
            writeCookie(EXPERIENCE_KEY, v);
            return v;
        },
        // Zoom-lens magnification: the long-press magnifier shows the wall photo at this multiple
        // of its ORIGINAL resolution. Stored as a plain integer cookie, clamped to a sane range.
        // Read defensively — an unset/garbage/out-of-range cookie falls back to the default.
        getZoomLensMag: function () {
            const n = parseInt(readCookie(ZOOM_LENS_MAG_KEY), 10);
            if (!isNaN(n) && n >= ZOOM_LENS_MAG_MIN && n <= ZOOM_LENS_MAG_MAX) {
                return n;
            }
            return ZOOM_LENS_MAG_DEFAULT;
        },
        setZoomLensMag: function (value) {
            let n = parseInt(value, 10);
            if (isNaN(n)) {
                n = ZOOM_LENS_MAG_DEFAULT;
            }
            n = Math.max(ZOOM_LENS_MAG_MIN, Math.min(ZOOM_LENS_MAG_MAX, n));
            writeCookie(ZOOM_LENS_MAG_KEY, String(n));
            return n;
        },
        recordLastPage: recordLastPage
    };
})();
