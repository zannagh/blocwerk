/*
 * bwWheelPager — turns a trackpad's horizontal wheel stream into discrete "step one" intents.
 *
 * Two surfaces in the wall UI page sideways off a trackpad and they are NESTED: the panel viewer
 * steps panel-to-panel (viewport.js), and the carousel it sits inside pages between wall pages
 * (wall-carousel.js). They had grown separate copies of this recogniser, and the copies had
 * drifted — the carousel learned to tell a momentum tail from a second push, the panel stepper
 * never did, so a flick that paged the carousel twice only ever stepped one panel. One recogniser,
 * two configurations.
 *
 * Why a wheel stream needs a recogniser at all: `scroll-snap-type: x mandatory` only commits a
 * page once the scroller has travelled past roughly half a slide, and a wheel burst does not
 * accumulate — each small deltaX is snapped back before the next arrives. Measured on the 5-page
 * carousel at 520px wide, a SINGLE 300px deltaX changed page, while ten 30px events (the same
 * 300px, the shape a real trackpad emits) changed nothing. So the gesture is cancelled and paged
 * by hand off an accumulator we control.
 *
 * The four rules that make a flick feel like a flick:
 *
 *   * YIELD TO WHOEVER SPOKE FIRST. An event that is already `defaultPrevented` was consumed by a
 *     layer nearer the finger, and `preventDefault` does not stop propagation — so without this an
 *     outer surface pages on a gesture an inner one has already spent. A consumed gesture is
 *     consumed WHOLE, because the tail of a pan arrives un-prevented once the inner surface hits
 *     its limit, and that remainder would otherwise accumulate into a step nobody asked for.
 *
 *   * AXIS LOCK. The axis is decided from ACCUMULATED travel and then held for the gesture. Judging
 *     each event alone let the diagonal noise in a real flick leak through as native scroll between
 *     our own steps, and the surface lurched because two mechanisms were moving it at once.
 *
 *   * A GESTURE ENDS ON AN IDLE GAP, measured from event timestamps. macOS keeps emitting wheel
 *     events for up to a second of momentum after the fingers leave the pad, so "gesture over"
 *     cannot mean "events stopped" without also meaning "wait out the tail".
 *
 *   * THE TAIL IS TOLD APART FROM A NEW PUSH BY ITS SHAPE, not by waiting for it. One commit
 *     latches the gesture so a single flick steps exactly once; the latch is released by a
 *     direction reversal, or by RE-ACCELERATION. Momentum decays monotonically, so a |deltaX| that
 *     climbs back well above the quietest event SINCE THE PEAK is a second push. Measuring the
 *     quiet point only after the peak is what keeps one flick from counting twice: the step
 *     threshold is usually crossed on the flick's RISING edge, which seeds the detector with a
 *     small delta that the flick's own peak would otherwise beat two events later.
 */
window.bwWheelPager = (function () {
    'use strict';

    // Well under the gap between two deliberate flicks, and far under the momentum tail — the tail
    // is ended by re-acceleration, not by this.
    const IDLE_MS = 140;
    // Travel at which the axis stops being re-evaluated and locks for the gesture.
    const AXIS_LOCK_PX = 24;
    // How far horizontal travel must beat vertical to count as a sideways gesture at all.
    const AXIS_DOMINANCE = 1.5;
    // How far |deltaX| must climb back above the quietest event since the peak to be a fresh push.
    // Momentum jitters, so this needs headroom in both forms, plus an absolute floor: a tail decays
    // to nothing, and `min * 2` eventually clears any ratio.
    const RISE_RATIO = 2;
    const RISE_FLOOR_PX = 6;
    const RISE_MIN_PX = 8;
    // A rise only counts once the gesture has demonstrably decayed this far below its peak.
    const DECAYED_FRACTION = 0.5;
    // deltaMode 1 is lines and 2 is pages (Firefox, some mice); normalise to pixels so every
    // threshold means the same thing on every device.
    const LINE_PX = 16;

    /**
     * @param {object} options
     *   stepPx   - accumulated horizontal travel that commits one step.
     *   onStep   - (dir, event) => void. dir is +1 for "next/right", -1 for "previous/left".
     *   accepts  - (event) => bool. False abandons the gesture WITHOUT cancelling the event: the
     *              caller's own bail-outs (edit mode, zoom, ctrl/shift) live here.
     *   canStep  - (dir) => bool. False lets the event through UNCANCELLED, which is how an inner
     *              surface hands a gesture it cannot use out to the one around it.
     *   pageSize - () => px, for deltaMode 2. Optional.
     */
    function create(options) {
        const stepPx = options.stepPx;
        const onStep = options.onStep;
        const accepts = options.accepts || function () { return true; };
        const canStep = options.canStep || function () { return true; };
        const pageSize = options.pageSize || function () { return 800; };

        let accX = 0;
        let accY = 0;
        let axis = 0; // 0 undecided, 1 horizontal, -1 vertical
        let latched = false;
        let quietest = Infinity;
        let loudest = 0;
        let dir = 0;
        let consumed = false;
        let at = 0;

        function reset() {
            accX = 0;
            accY = 0;
            axis = 0;
            latched = false;
            quietest = Infinity;
            loudest = 0;
            consumed = false;
        }

        function commit(travel) {
            latched = true;
            dir = Math.sign(accX);
            quietest = travel;
            loudest = travel;
        }

        function onWheel(e) {
            const now = e.timeStamp || performance.now();
            if (now - at > IDLE_MS) {
                reset();
            }

            at = now;

            if (e.defaultPrevented) {
                consumed = true;
            }

            if (consumed) {
                return;
            }

            if (!accepts(e)) {
                reset();
                return;
            }

            const scale = e.deltaMode === 1 ? LINE_PX : (e.deltaMode === 2 ? pageSize() : 1);
            const dx = e.deltaX * scale;
            const dy = e.deltaY * scale;
            accX += dx;
            accY += dy;

            // Undecided gestures re-evaluate every event; past the lock distance the axis stands, so
            // late vertical drift cannot hand a horizontal flick back to the native scroller.
            if (axis === 0) {
                const horizontal = Math.abs(accX) > Math.abs(accY) * AXIS_DOMINANCE;
                if (Math.max(Math.abs(accX), Math.abs(accY)) >= AXIS_LOCK_PX) {
                    axis = horizontal ? 1 : -1;
                } else if (!horizontal) {
                    return;
                }
            }

            if (axis === -1) {
                return;
            }

            if (!canStep(accX > 0 ? 1 : -1)) {
                return;
            }

            if (e.cancelable) {
                e.preventDefault();
            }

            const travel = Math.abs(dx);
            if (latched) {
                if (dx !== 0 && Math.sign(dx) !== dir) {
                    // A reversal is unambiguously a new gesture, whatever the tail is doing.
                    reset();
                    accX = dx;
                    accY = dy;
                    axis = 1;
                } else if (quietest < loudest * DECAYED_FRACTION
                    && travel >= RISE_MIN_PX
                    && travel > Math.max(quietest * RISE_RATIO, quietest + RISE_FLOOR_PX)) {
                    const was = dir;
                    reset();
                    accX = dx;
                    accY = dy;
                    axis = 1;
                    dir = was;
                } else {
                    // A new peak restarts the decay measurement; only events after it can show that
                    // the gesture is running out of energy.
                    if (travel >= loudest) {
                        loudest = travel;
                        quietest = travel;
                    } else {
                        quietest = Math.min(quietest, travel);
                    }

                    return;
                }
            }

            if (Math.abs(accX) < stepPx) {
                return;
            }

            const stepDir = accX > 0 ? 1 : -1;
            commit(travel);
            onStep(stepDir, e);
        }

        return { onWheel: onWheel, reset: reset };
    }

    return { create: create };
})();
