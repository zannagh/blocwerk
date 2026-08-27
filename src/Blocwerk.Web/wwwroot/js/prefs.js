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
    //   - any path with a "/shared/" segment (e.g. "/walls/{id}/boulders/{bid}/shared/{token}")
    //     — don't resurrect a one-off share-token URL.
    const NON_RECORDABLE = /^\/$|^\/account|^\/login|^\/logout|^\/join(\/|$)|\/shared\//;

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
            writeCookie(LAST_PAGE_KEY, path + location.search);
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
        recordLastPage: recordLastPage
    };
})();
