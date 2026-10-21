/*
 * bwImageResUrl — the url rules of image-res.js: which rendition a url names, and the same url at another one.
 *
 * A legacy image route carries its width as `?w=`; a content-addressed asset (AssetUrls on the server) as a path
 * segment, `/api/walls/{w}/a/{sha}/{width}`. No width at all is the stored original. image-watchdog.js's `bwretry`
 * marker is never part of a base url.
 */
window.bwImageResUrl = (function () {
    'use strict';

    // Must match ImageVariants.Widths on the server. A width that is not on this ladder is
    // refused by the byte routes, so guessing one would only produce a broken image.
    const WIDTHS = [640, 1280, 1920, 2560];

    // ORIGINAL is the top of the ladder: past the widest rendition the stored photo is served, and
    // there is nothing sharper to ask for.
    const ORIGINAL = Number.MAX_SAFE_INTEGER;

    // The query parameter image-watchdog.js adds when it re-requests a stalled image; never part of the base.
    const RETRY_PATTERN = /([?&])bwretry=[^&#]*&?/;

    // A content-addressed asset: the width is the path segment after the sha.
    const HASHED_PATH = /^(\/api\/walls\/[0-9a-fA-F-]{36}\/a\/[0-9a-f]{64})(?:\/(\d+))?$/;

    /** `url` with its width set to `level` (a `w` parameter, or the hashed path's segment), or removed for the original. */
    function urlAt(url, level) {
        try {
            const parsed = new URL(url, document.baseURI);
            const hashed = HASHED_PATH.exec(parsed.pathname);
            if (hashed) {
                return hashed[1] + (level === ORIGINAL ? '' : '/' + level) + parsed.search;
            }

            if (level === ORIGINAL) {
                parsed.searchParams.delete('w');
            } else {
                parsed.searchParams.set('w', String(level));
            }

            return parsed.pathname + parsed.search;
        } catch (_) {
            return url;
        }
    }

    /** The rung `url` asks for: a width on the ladder, or ORIGINAL. */
    function levelOf(url) {
        try {
            const parsed = new URL(url, document.baseURI);
            const hashed = HASHED_PATH.exec(parsed.pathname);
            const w = hashed ? hashed[2] : parsed.searchParams.get('w');
            const level = w ? parseInt(w, 10) : ORIGINAL;
            return WIDTHS.indexOf(level) >= 0 ? level : ORIGINAL;
        } catch (_) {
            return ORIGINAL;
        }
    }

    /** The url without its width and without image-watchdog.js's retry marker (only that parameter is touched). */
    function stripWidth(url) {
        return urlAt(url, ORIGINAL).replace(RETRY_PATTERN, '$1').replace(/[?&]$/, '');
    }

    return { WIDTHS, ORIGINAL, urlAt, levelOf, stripWidth };
})();
