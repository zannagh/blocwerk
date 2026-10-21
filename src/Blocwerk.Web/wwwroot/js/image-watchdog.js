/*
 * bwImageWatchdog — re-requests an image whose download stalled.
 *
 * A phone's HTTP/2 stream can stop part-way through an image without ever failing: the server has
 * sent every byte (our logs say 200 with the full size), but the browser keeps waiting, paints the
 * rows it has (Safari shows the top strip and leaves the rest transparent) and never fires `load`
 * or `error`. Nothing on the page would ever ask again, so the picture stays half-drawn until a
 * reload. This module looks at the opted-in images every couple of seconds; one that is in view and
 * still not complete after its stall time is asked for again under a fresh url (a `bwretry` query
 * parameter, which every image route ignores and image-res.js keeps out of its base url), up to
 * MAX_RETRIES times per picture.
 *
 * The stall time doubles with every retry, so a picture that is merely slow is not rendered again and
 * again: review crops are rendered per request on the server, so they also start from a longer wait.
 * Time in a background tab does not count, and an image only counts as in view once an
 * IntersectionObserver says so — that sees the clipping of scroll containers, so a lazy image below a
 * scroller's fold (which the browser has not even asked for) is left alone.
 *
 * Opt in with `data-bw-res` (the zoomable photos) or `data-bw-watch` (any other <img>).
 */
window.bwImageWatchdog = (function () {
    'use strict';

    const RETRY_PATTERN = /([?&])bwretry=[^&#]*&?/;
    const MAX_RETRIES = 3;
    const STALL_MS = 12000;        // photos (data-bw-res): renditions are cached on the server
    const WATCH_STALL_MS = 20000;  // data-bw-watch, e.g. review crops: rendered on every request
    const TICK_MS = 2000;
    const SELECTOR = 'img[data-bw-res], img[data-bw-watch]';

    const observer = typeof window.IntersectionObserver === 'function'
        ? new window.IntersectionObserver(function (entries) {
            entries.forEach(function (e) { e.target._bwInView = e.isIntersecting; });
        })
        : null;

    /** `url` without the retry marker; only that one parameter is touched. */
    function baseOf(url) {
        return url.replace(RETRY_PATTERN, '$1').replace(/[?&]$/, '');
    }

    /** `url` with the retry marker set to `n`. */
    function retryUrl(url, n) {
        const base = baseOf(url);
        return base + (base.indexOf('?') >= 0 ? '&' : '?') + 'bwretry=' + n;
    }

    /** How long a picture may load before it is asked for again, after `tries` retries already. */
    function stallMsFor(img, tries) {
        const first = img.hasAttribute('data-bw-watch') ? WATCH_STALL_MS : STALL_MS;
        return first * Math.pow(2, tries);
    }

    function inView(img) {
        if (observer) {
            if (!img._bwObserved) {
                img._bwObserved = true;
                observer.observe(img);
            }

            return img._bwInView === true;
        }

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

        if (img.complete || !inView(img)) {
            img._bwWatchSince = null;
            return false;
        }

        if (img._bwWatchSince == null) {
            img._bwWatchSince = now;
            return false;
        }

        if (img._bwWatchTries >= MAX_RETRIES || now - img._bwWatchSince < stallMsFor(img, img._bwWatchTries)) {
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

    /** Back from the background: start every clock again, so the hidden time does not count as a stall. */
    function resume() {
        if (document.visibilityState === 'hidden') {
            return;
        }

        document.querySelectorAll(SELECTOR).forEach(function (img) { img._bwWatchSince = null; });
    }

    if (typeof document.addEventListener === 'function') {
        document.addEventListener('visibilitychange', resume);
    }

    if (typeof window.setInterval === 'function') {
        window.setInterval(tick, TICK_MS);
    }

    return { check: check, tick: tick, resume: resume, stallMsFor: stallMsFor, MAX_RETRIES: MAX_RETRIES };
})();
