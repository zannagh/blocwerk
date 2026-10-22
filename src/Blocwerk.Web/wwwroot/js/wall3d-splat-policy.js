// When the photo-real view (wall3d-splat.js) changes its level of detail. The view renders on demand
// (wall3d-loop.js), so the frame-time measurement that drives the ladder runs as a short probe: after
// a level shows, the loop draws back to back, uncapped, until the monitor has decided (WARMUP +
// SAMPLE frames, about a second); then it stops. Outside a probe only a too-slow level counts.
//
// Sustained load: a phone that keeps rendering (a long orbit, a slow drag around the wall) heats up
// even when each frame is fast. After SUSTAINED_MS of rendering with no rest longer than REST_MS in
// between, a light device on Detail: auto steps down one level and stays there for this view.
import { createFrameMonitor } from './wall3d-splat-ladder.js';

/** Continuous rendering after which a phone on Detail: auto steps down one level. */
const SUSTAINED_MS = 20_000;
/** A pause between frames at least this long counts as rest (the run starts over). */
const REST_MS = 1500;

/** Back-off before a probe that failed, was aborted or read 'stay' runs again (x attempt number). */
const RETRY_MS = +(new URLSearchParams(location.search).get('splatRetryMs') || 15_000);
/** Re-probes per view session; after these the level on show stays. */
const MAX_RETRIES = 4;
/** The cap level showing this long without a step down counts as running fine (it is never probed). */
export const STEADY_MS = +(new URLSearchParams(location.search).get('splatSteadyMs') || 10_000);

/**
 * Re-arms the step-up probe: a step that failed, was aborted, or a probe that read 'stay' would
 * otherwise leave the view on its level until a reload. `schedule` starts a back-off, `due` says
 * (once) when it has passed. The attempts are bounded per session.
 */
export function createReprobe() {
    let at = 0;
    let pending = false;
    let attempts = 0;
    return {
        get attempts() { return attempts; },
        get pending() { return pending; },
        /** Milliseconds from the last `schedule` until `due`. */
        get delay() { return RETRY_MS * attempts + RETRY_MS; },
        /** False when the attempts are used up. */
        schedule(now = performance.now()) {
            pending = attempts < MAX_RETRIES;
            at = now + RETRY_MS * (attempts + 1);
            return pending;
        },
        cancel() { pending = false; },
        due(now) {
            if (!pending || now < at) return false;
            pending = false;
            attempts++;
            return true;
        },
    };
}

/**
 * `light`: phone-class device. Returns { probing, shown(canStepUp), stop(), frame(now, sustained) };
 * `frame` answers 'up' | 'down' | 'stay' | 'sustained' | null.
 */
export function createLadderPolicy(light) {
    const monitor = createFrameMonitor(light);
    let probing = false;
    let runStart = 0;
    let lastFrame = 0;
    return {
        monitor,
        /** True while a probe measures the level on show (the loop then draws uncapped). */
        get probing() { return probing; },
        /** A level shows: probe it when there is a level above it to step up to. */
        shown(canStepUp) {
            monitor.reset();
            probing = canStepUp;
        },
        stop() { probing = false; },
        /** One drawn frame; `sustained`: whether the sustained-load rule applies right now. */
        frame(now, sustained) {
            if (!lastFrame || now - lastFrame > REST_MS) runStart = now;
            lastFrame = now;
            const decision = monitor.frame(now);
            if (probing && decision) {
                probing = false;
                return decision;
            }
            if (decision === 'down') return 'down';
            if (light && sustained && now - runStart > SUSTAINED_MS) {
                runStart = now;
                return 'sustained';
            }
            return null;
        },
    };
}

/** Drawn frames the armed level needs on top of STEADY_MS: a view that shows but hardly draws proves nothing. */
export const STEADY_FRAMES = 30;

/**
 * The cap level is never probed (there is nothing above it to step up to), so its success is recorded here instead.
 * Only active, visible time counts: `arm(index, now)` when the level shows in an active, visible view; `spoil()` when
 * it stops being that (the mode left, the page hidden, a lost context) or on a step down or a sustained-load step.
 * `due(index, now)` is called once per drawn frame and is true once, when the armed level is still the one showing
 * STEADY_MS later and has drawn at least `frames` frames. Then the next visit steps straight up to it
 * (wall3d-splat-ladder.js stepTarget) instead of re-downloading every level below it.
 */
export function createSteadyWatch(ms = STEADY_MS, frames = STEADY_FRAMES) {
    let armed = -1;
    let since = 0;
    let drawn = 0;
    return {
        get delay() { return ms; },
        arm(index, now) { armed = index; since = now; drawn = 0; },
        spoil() { armed = -1; },
        due(index, now) {
            if (armed < 0 || index !== armed) return false;
            drawn++;
            if (now - since < ms || drawn < frames) return false;
            armed = -1;
            return true;
        },
    };
}
