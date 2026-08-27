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

    return {
        getExperience: function () {
            const v = readCookie(EXPERIENCE_KEY);
            return v === 'new' || v === 'old' ? v : null;
        },
        setExperience: function (value) {
            const v = value === 'new' ? 'new' : 'old';
            writeCookie(EXPERIENCE_KEY, v);
            return v;
        }
    };
})();
