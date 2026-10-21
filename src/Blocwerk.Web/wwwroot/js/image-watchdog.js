/*
 * bwImageWatchdog — re-requests an image whose download stalled.
 *
 * A phone's HTTP/2 stream can stop part-way through an image without ever failing: the server has
 * sent every byte (our logs say 200 with the full size), but the browser keeps waiting, paints the
 * rows it has (Safari shows the top strip and leaves the rest transparent) and never fires `load`
 * or `error`. Nothing on the page would ever ask again, so the picture stays half-drawn until a
 * reload. This module looks at the opted-in images every couple of seconds; one that is on screen
 * and still not complete after STALL_MS is asked for again under a fresh url (a `bwretry` query
 * parameter, which every image route ignores and image-res.js keeps out of its base url), up to
 * MAX_RETRIES times per picture.
 *
 * Opt in with `data-bw-res` (the zoomable photos) or `data-bw-watch` (any other <img>). Lazy
 * images that are not on screen are left alone: the browser has not asked for them yet.
 */
window.bwImageWatchdog = (function () {
    'use strict';

    const RETRY_PARAM = 'bwretry';
    const MAX_RETRIES = 3;
    const STALL_MS = 12000;
    const TICK_MS = 2000;
    const SELECTOR = 'img[data-bw-res], img[data-bw-watch]';

    /** `url` without the retry marker, for telling "the same picture again" from "a new picture". */
    function baseOf(url) {
        try {
            const parsed = new URL(url, document.baseURI);
            parsed.searchParams.delete(RETRY_PARAM);
            return parsed.pathname + parsed.search;
        } catch (_) {
            return url;
        }
    }

    /** `url` with the retry marker set to `n`. */
    function retryUrl(url, n) {
        try {
            const parsed = new URL(url, document.baseURI);
            parsed.searchParams.set(RETRY_PARAM, String(n));
            return parsed.pathname + parsed.search;
        } catch (_) {
            return url;
        }
    }

    function onScreen(img) {
        const r = img.getBoundingClientRect();
        return r.width > 0 && r.height > 0 && r.bottom > 0 && r.right > 0
            && r.top < window.innerHeight && r.left < window.innerWidth;
    }

    /**
     * One look at one image at time `now`. Exposed for the tests. The element carries its own state:
     * which picture it is waiting for, since when, and how often that picture was asked for again.
     */
    function check(img, now) {
        const src = img.getAttribute('src');
        if (!src || !img.isConnected) {
            return false;
        }

        const base = baseOf(src);
        if (img._bwWatchBase !== base) {
            img._bwWatchBase = base;
            img._bwWatchTries = 0;
            img._bwWatchSince = null;
        }

        if (img.complete || !onScreen(img)) {
            img._bwWatchSince = null;
            return false;
        }

        if (img._bwWatchSince == null) {
            img._bwWatchSince = now;
            return false;
        }

        if (now - img._bwWatchSince < STALL_MS || img._bwWatchTries >= MAX_RETRIES) {
            return false;
        }

        img._bwWatchTries++;
        img._bwWatchSince = now;
        img.src = retryUrl(src, img._bwWatchTries);
        return true;
    }

    function tick() {
        if (document.visibilityState === 'hidden') {
            return;
        }

        const now = Date.now();
        document.querySelectorAll(SELECTOR).forEach(function (img) { check(img, now); });
    }

    if (typeof window.setInterval === 'function') {
        window.setInterval(tick, TICK_MS);
    }

    return { check: check, tick: tick, STALL_MS: STALL_MS, MAX_RETRIES: MAX_RETRIES };
})();
