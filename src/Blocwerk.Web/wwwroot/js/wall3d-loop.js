// Render scheduling of the 3D wall view (wall3d.js). A frame is drawn only when something asks for
// one: a camera move or tween, a texture landing, a Spark sort finishing, the photo-real ladder
// measuring a level. Once nothing moves the loop stops, so an idle view costs no GPU time at all —
// the photo-real mode included (it used to render every frame, which made phones hot).
//
// Also here, because they all hang off the same loop:
//   pause    — nothing draws while the page is hidden (tab switch, app switch, page hide) or the
//              stage is off screen / not laid out (IntersectionObserver); a frame asked for meanwhile
//              is drawn once the view shows again;
//   cap      — `capped()` limits the frame rate (photo-real on a phone: 30 fps while it moves);
//   interact — `onInteract(true)` before the first frame of a camera move, `onInteract(false)` once
//              the camera has been still for SETTLE_MS (the photo-real view drops its resolution in
//              between); both run inside a frame, right before it draws, so no cleared canvas shows.

/** Stillness after which a move counts as over (full resolution again). */
const SETTLE_MS = 300;
/** Frame interval of a capped loop (30 fps); a vsync early still counts. */
const CAPPED_MS = 1000 / 30 - 4;

/**
 * @param ctx.container    the stage element (its visibility pauses the loop)
 * @param ctx.draw         (now) draws one frame; returns true while the camera still moves
 * @param ctx.more         () → true while something else wants frames (photo-real measuring)
 * @param ctx.capped       () → true while the frame rate is capped
 * @param ctx.onInteract   (moving) when a camera move starts / has settled
 */
export function createRenderLoop({ container, draw, more = () => false, capped = () => false, onInteract = () => {} }) {
    let raf = 0;
    let disposed = false;
    let drawing = false;
    let pending = false;            // a frame was asked for while paused
    let hidden = document.visibilityState === 'hidden';
    let offscreen = false;
    let lastDraw = 0;
    let moving = false;             // what the view wants: a move is on
    let applied = false;            // what onInteract last got
    let settleTimer = 0;
    let frames = 0;

    const paused = () => hidden || offscreen;

    function schedule() {
        if (raf || disposed) return;
        if (paused()) {
            pending = true;
            return;
        }
        raf = requestAnimationFrame(frame);
    }

    function settleLater() {
        clearTimeout(settleTimer);
        settleTimer = setTimeout(() => {
            settleTimer = 0;
            if (!moving) return;
            moving = false;
            schedule();             // one more frame at full resolution
        }, SETTLE_MS);
    }

    function frame(now) {
        raf = 0;
        if (paused()) {
            pending = true;
            return;
        }
        if (capped() && lastDraw && now - lastDraw < CAPPED_MS) {
            raf = requestAnimationFrame(frame);
            return;
        }
        if (applied !== moving) {
            applied = moving;
            onInteract(moving);
        }
        lastDraw = now;
        frames++;
        drawing = true;
        let stillMoving = false;
        try {
            stillMoving = draw(now);
        } finally {
            drawing = false;
        }
        if (stillMoving) interact();
        if (stillMoving || more() || applied !== moving) schedule();
    }

    /** A camera move is on (a drag, a tween, a pinch): lower resolution until it settles. */
    function interact() {
        moving = true;
        settleLater();
    }

    function setHidden(value) {
        hidden = value;
        resume();
    }

    function resume() {
        if (paused() || !pending) return;
        pending = false;
        lastDraw = 0;
        schedule();
    }

    const onVisibility = () => setHidden(document.visibilityState === 'hidden');
    const onPageHide = () => setHidden(true);
    const onPageShow = () => setHidden(document.visibilityState === 'hidden');
    document.addEventListener('visibilitychange', onVisibility);
    window.addEventListener('pagehide', onPageHide);
    window.addEventListener('pageshow', onPageShow);
    const io = typeof IntersectionObserver === 'function'
        ? new IntersectionObserver(entries => {
            const e = entries[entries.length - 1];
            offscreen = !e.isIntersecting;
            resume();
        })
        : null;
    io?.observe(container);

    return {
        /** Asks for a frame. Ignored while one draws: the draw's own result decides about the next. */
        request() {
            if (!drawing) schedule();
        },
        interact() {
            interact();
            schedule();
        },
        /** Draws right now, outside the loop (the screenshot harness). */
        drawNow(now) {
            drawing = true;
            try {
                draw(now);
            } finally {
                drawing = false;
            }
        },
        get paused() { return paused(); },
        get running() { return raf !== 0; },
        /** Frames drawn since the view mounted (perf checks). */
        get frames() { return frames; },
        dispose() {
            disposed = true;
            if (raf) cancelAnimationFrame(raf);
            raf = 0;
            clearTimeout(settleTimer);
            document.removeEventListener('visibilitychange', onVisibility);
            window.removeEventListener('pagehide', onPageHide);
            window.removeEventListener('pageshow', onPageShow);
            io?.disconnect();
        },
    };
}
