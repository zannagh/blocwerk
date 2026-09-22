/*
 * bwOrientation — the ONE place that reacts to the window changing shape.
 *
 * Why it exists: several subsystems cache geometry that a rotation invalidates, and before this
 * module nothing re-ran any of them.
 *   - viewport.js writes an INLINE aspect-ratio / --fit-aspect / --crop-scale measured once, on
 *     image load, and a `touch-action` that is only refreshed while zooming. Rotate a phone with
 *     a zoomed photo on screen and the box keeps the old shape while `touch-action: none` eats
 *     every touch — the app looks frozen.
 *   - wall-carousel.js positions pages by pixel offset. A width change silently moves which page
 *     `scrollLeft` corresponds to, and its idle settle then reported a page the user never chose.
 *
 * Both are fixed by re-running the owning module's own code (bwViewport.relayout,
 * wallCarousel.resnap) — nothing here duplicates their internals, and nothing here writes a
 * style on a photo surface: JS's ownership of those inline properties stays inside viewport.js.
 *
 * Debouncing is the whole difficulty. iOS fires `resize` several times per rotation, some of
 * them reporting the PRE-rotation dimensions, and `orientationchange` arrives before layout has
 * settled. So: every signal only restarts a quiet timer; the work runs once the window has held
 * still, and a single confirmation pass runs a little later in case the last reported size was
 * still mid-flight. Sizes are compared against what was last applied, so a burst of identical
 * events costs nothing.
 */
window.bwOrientation = (function () {
    'use strict';

    // Long enough to swallow an iOS rotation burst, short enough that the broken frame is not
    // something the user sits and stares at.
    const QUIET_MS = 180;
    // One late re-check, after the rotation animation and any URL-bar settling are over.
    const CONFIRM_MS = 450;

    let timer = 0;
    let confirmTimer = 0;
    let appliedW = 0;
    let appliedH = 0;

    /*
     * A full-screen takeover (StageOverlay) owns the whole window while it is up, and reaching it
     * CHANGES the window: requestFullscreen plus screen.orientation.lock('landscape') resize it,
     * and leaving does it again. Every one of those resizes used to land here as an ordinary
     * rotation — re-fitting and RESETTING the zoom of every registered viewport on the page behind
     * the overlay (two interop bursts and a re-render per open/close) and re-snapping the carousel
     * under it, none of which the user can even see happening.
     *
     * So the coordinator stands down while the takeover is up; the stage relayouts itself
     * (fullscreen-stage.js). The DOM class is the seam rather than a global flag — the same one
     * takeoverSurfaceOpen() keys on in keyboard-shortcuts.js — so it is true exactly while the
     * overlay is rendered, however it came up or went away.
     *
     * Crucially appliedW/appliedH are NOT updated while it is up: the window comes back to its old
     * size on close, which then compares equal and costs nothing. A device genuinely rotated during
     * the takeover does differ, and gets its pass on close.
     */
    function stageOpen() {
        return document.querySelector('.bw-stage-overlay') !== null;
    }

    function apply(widthChanged) {
        // Photo surfaces first: dropping a zoomed viewport back to fit also restores
        // `touch-action: pan-y`, which is what lets the page (and the carousel under it) be
        // touched again at all.
        if (window.bwViewport && window.bwViewport.relayout) {
            window.bwViewport.relayout(widthChanged);
        }

        // Then the carousel, which needs the new width to be settled before it re-snaps.
        if (widthChanged && window.wallCarousel && window.wallCarousel.resnap) {
            const tracks = document.querySelectorAll('.wall-carousel');
            for (let i = 0; i < tracks.length; i++) {
                window.wallCarousel.resnap(tracks[i]);
            }
        }
    }

    function run() {
        timer = 0;
        if (stageOpen()) {
            return;
        }

        const w = window.innerWidth;
        const h = window.innerHeight;
        if (w === appliedW && h === appliedH) {
            return;
        }

        const widthChanged = w !== appliedW;
        appliedW = w;
        appliedH = h;
        apply(widthChanged);

        // A rotation on iOS can report its final size only after the animation ends; re-check
        // once and redo the work if it moved again. Idempotent, so a no-op is free.
        if (confirmTimer) {
            clearTimeout(confirmTimer);
        }
        confirmTimer = setTimeout(function () {
            confirmTimer = 0;
            if (window.innerWidth !== appliedW || window.innerHeight !== appliedH) {
                schedule();
            }
        }, CONFIRM_MS);
    }

    function schedule() {
        if (timer) {
            clearTimeout(timer);
        }
        timer = setTimeout(run, QUIET_MS);
    }

    function init() {
        if (window._bwOrientationBound) {
            return;
        }
        window._bwOrientationBound = true;
        appliedW = window.innerWidth;
        appliedH = window.innerHeight;

        window.addEventListener('resize', schedule, { passive: true });
        window.addEventListener('orientationchange', schedule, { passive: true });
        if (window.screen && window.screen.orientation && window.screen.orientation.addEventListener) {
            window.screen.orientation.addEventListener('change', schedule);
        }
        // visualViewport fires for the on-screen keyboard and for pinch too, neither of which
        // should re-fit anything — run() ignores those because window.innerWidth/innerHeight
        // are unchanged there.
        if (window.visualViewport && window.visualViewport.addEventListener) {
            window.visualViewport.addEventListener('resize', schedule, { passive: true });
        }
    }

    init();

    return {
        /** Force a pass now (no debounce). Exposed for the console and for future callers. */
        refresh: function () {
            appliedW = -1;
            run();
        },
    };
})();
