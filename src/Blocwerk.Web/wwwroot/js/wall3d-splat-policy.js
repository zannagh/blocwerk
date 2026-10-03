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

/**
 * `light`: phone-class device. Returns { probing, shown(canStepUp), stop(), frame(now, sustained) };
 * `frame` answers 'up' | 'down' | 'sustained' | null.
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
                return decision === 'stay' ? null : decision;
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
