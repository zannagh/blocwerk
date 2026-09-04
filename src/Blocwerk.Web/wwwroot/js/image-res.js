/*
 * bwImageRes — picks which rendition of a wall photo the browser should be holding.
 *
 * The server stores the camera original (hold detection depends on it) and serves downscaled
 * renditions off a fixed width ladder via `?w=`. Markup starts every opted-in <img> at a modest
 * width; this module measures what the element is actually displayed at — CSS pixels times the
 * device pixel ratio — and upgrades to the next rung whenever that grows, which is exactly what
 * happens when the viewport zooms in (the content layer widens via `--zoom`, so the img's own
 * clientWidth grows with it).
 *
 * Two rules make the upgrade invisible:
 *   1. Never swap DOWN. Zooming back out would otherwise churn through renditions mid-gesture,
 *      and the higher one is already decoded and correct — there is nothing to gain by dropping it.
 *   2. Never swap to something that is not ready. The replacement is loaded and decoded off-screen
 *      first; only once it can be painted in the same frame is `src` reassigned, so the element
 *      keeps showing the current rendition until the moment it is replaced.
 *
 * Opt in with `data-bw-res` on the <img>. Anything without it is left entirely alone.
 */
window.bwImageRes = (function () {
    'use strict';

    // Must match ImageVariants.Widths on the server. A width that is not on this ladder is
    // refused by the byte routes, so guessing one would only produce a broken image.
    const WIDTHS = [640, 1280, 1920, 2560];

    // ORIGINAL is the top of the ladder: past the widest rendition the stored photo is served, and
    // there is nothing sharper to ask for.
    const ORIGINAL = Number.MAX_SAFE_INTEGER;

    // The full ladder the cap indexes against: every WIDTHS rung, then ORIGINAL as the top.
    const LADDER = WIDTHS.concat([ORIGINAL]);

    // --- Zoom-aware caps (the tunable knobs) -----------------------------------------------------
    // A big/retina display at *fit* has `cssWidth * DPR` already past the top of the ladder, so the
    // raw "ideal" rung would fetch the 2.6-4.2 MB original while the wall is fully zoomed out and no
    // detail is visible. These caps forbid the top rungs until the user has actually zoomed in far
    // enough to use them, so fit stays cheap and crispness is unlocked progressively.
    //
    // Each entry is [zoomBelow, capWidth]: the first whose threshold the current zoom is under wins.
    // Bump FIT_CAP to 2560 if fit ever looks soft on a large display.
    const FIT_CAP = 1920;           // zoom < 1.5 (at/near fit): never 2560/ORIGINAL
    const MID_CAP = 2560;           // 1.5 <= zoom < 3.0: allow 2560, still not ORIGINAL
    const FIT_ZOOM_BELOW = 1.5;     // under this zoom, cap at FIT_CAP
    const MID_ZOOM_BELOW = 3.0;     // under this zoom, cap at MID_CAP; at/above it, ORIGINAL is fair

    /** Ladder index of a cap width (ORIGINAL is the last index). */
    function capIndexFor(zoom) {
        if (zoom < FIT_ZOOM_BELOW) {
            return LADDER.indexOf(FIT_CAP);
        }

        if (zoom < MID_ZOOM_BELOW) {
            return LADDER.indexOf(MID_CAP);
        }

        return LADDER.length - 1; // ORIGINAL
    }

    /**
     * The rendition to request for an element displayed at `cssWidth` device-independent pixels,
     * capped by the current `zoom` (default 1 = fit). The ideal rung comes from the DPR-scaled pixel
     * need exactly as before; the cap only ever pulls the choice DOWN while zoomed out, so it can
     * never force an upgrade past what the pixels need.
     */
    function levelFor(cssWidth, zoom) {
        const needed = cssWidth * (window.devicePixelRatio || 1);
        let idealIndex = WIDTHS.length; // ORIGINAL unless a rung covers `needed`
        for (let i = 0; i < WIDTHS.length; i++) {
            if (WIDTHS[i] >= needed) {
                idealIndex = i;
                break;
            }
        }

        const index = Math.min(idealIndex, capIndexFor(typeof zoom === 'number' && zoom > 0 ? zoom : 1));
        return LADDER[index];
    }

    /** `url` with its `w` parameter set to `level`, or removed entirely for the original. */
    function urlAt(url, level) {
        try {
            const parsed = new URL(url, document.baseURI);
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

    /**
     * What the element is showing right now. Read from the markup on first sight so a server
     * re-render that resets `src` is picked up rather than trusted blindly from JS state.
     */
    function currentLevel(img) {
        const src = img.getAttribute('src') || '';
        try {
            const w = new URL(src, document.baseURI).searchParams.get('w');
            const level = w ? parseInt(w, 10) : ORIGINAL;
            return WIDTHS.indexOf(level) >= 0 ? level : ORIGINAL;
        } catch (_) {
            return ORIGINAL;
        }
    }

    /**
     * Loads and decodes the replacement off-screen, then swaps it in. The pending URL is recorded
     * on the element so a second, higher upgrade started while this one is in flight simply wins:
     * the older callback sees it is no longer pending and drops its result instead of downgrading.
     */
    function upgrade(img, level) {
        const url = urlAt(img._bwBase, level);
        img._bwPending = url;

        const next = new Image();
        next.decoding = 'async';

        const swap = function () {
            if (img._bwPending !== url || !next.naturalWidth) {
                return;
            }

            img._bwPending = null;
            img._bwLevel = level;
            img.src = url;
        };

        // decode() resolves only once the bitmap is ready to paint, which is the whole point — the
        // assignment below then reuses it from cache without a blank frame. Browsers that reject or
        // lack it fall back to the load event, which is one frame less certain but never blank.
        next.onload = function () {
            if (!next.decode) {
                swap();
            }
        };
        next.onerror = function () {
            if (img._bwPending === url) {
                img._bwPending = null;
            }
        };
        next.src = url;

        if (next.decode) {
            next.decode().then(swap, function () {
                if (next.complete) {
                    swap();
                }
            });
        }
    }

    /** Measures one opted-in image and upgrades it if the viewport now needs more pixels. */
    function evaluate(img) {
        if (!img.isConnected) {
            return;
        }

        const src = img.getAttribute('src');
        if (!src) {
            return;
        }

        // The base URL keeps whatever query the markup carried (the share token) and never the
        // width — every request is built from it, so repeated upgrades cannot stack parameters.
        if (img._bwBase === undefined || img._bwBaseFor !== stripWidth(src)) {
            img._bwBase = stripWidth(src);
            img._bwBaseFor = img._bwBase;
            img._bwLevel = currentLevel(img);
        }

        const displayed = img.clientWidth;
        if (!(displayed > 0)) {
            return;
        }

        const wanted = levelFor(displayed, lastZoom);
        const held = img._bwLevel || currentLevel(img);

        // Upgrades only. Zooming out must not trade the sharp rendition back for a smaller one.
        if (wanted <= held || img._bwPending) {
            return;
        }

        upgrade(img, wanted);
    }

    function stripWidth(url) {
        return urlAt(url, ORIGINAL);
    }

    /**
     * Coalesces the storm of calls a pinch or wheel gesture produces into one measurement per
     * frame. Measuring is a layout read, so doing it per event would be the expensive part.
     */
    // The zoom the most recent refresh reported, consulted by evaluate() when the debounced pass
    // runs. A gesture is monotonic within a frame, so the last value wins; anything that schedules
    // without a zoom (a resize, an unaware caller) resets this to 1 = fit, the conservative cap.
    let lastZoom = 1;

    let pending = null;
    function schedule(root) {
        if (pending) {
            pending.add(root);
            return;
        }

        pending = new Set([root]);
        window.requestAnimationFrame(function () {
            const roots = pending;
            pending = null;
            roots.forEach(function (el) {
                if (!el || typeof el.querySelectorAll !== 'function') {
                    return;
                }

                el.querySelectorAll('img[data-bw-res]').forEach(evaluate);
            });
        });
    }

    // A resize (rotation, window drag, entering fullscreen) changes the displayed size just as much
    // as a zoom does, and nothing else would notice.
    window.addEventListener('resize', function () { lastZoom = 1; schedule(document); });

    return {
        /**
         * Re-measure the opted-in images under `root` (an element, or omitted for the whole page).
         * Pass the viewport's current `zoom` so the variant choice can be capped while zoomed out;
         * omit it (or pass a non-positive value) to be treated as fit (zoom 1), the conservative cap.
         * Safe to call as often as you like — it is debounced to one pass per animation frame and
         * does nothing at all when no image needs a bigger rendition.
         */
        refresh: function (root, zoom) {
            lastZoom = typeof zoom === 'number' && zoom > 0 ? zoom : 1;
            schedule(root || document);
        },
    };
})();
