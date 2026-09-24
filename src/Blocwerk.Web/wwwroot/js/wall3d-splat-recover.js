// Device class, render resolution and lost-context recovery of the photo-real view (wall3d-splat.js).
//
// iOS Safari drops a page's WebGL context when a frame runs too long or the tab uses too much GPU
// memory. three.js keeps the context restorable (it calls preventDefault on `webglcontextlost` and
// rebuilds its own state on `webglcontextrestored`); the Spark scene is dropped at the loss and
// rebuilt after the restore, one level lower and at a lower resolution. A browser that does not give
// the context back on its own is asked to (WEBGL_lose_context), and one that never does ends the
// mode quietly. So does losing the smallest level twice.
import { rememberFailure } from './wall3d-splat-ladder.js';

/** At most this much device memory (GB, `navigator.deviceMemory`; Chromium only) counts as low. */
const LOW_MEMORY_GB = 4;
/** GPUs whose largest texture is smaller than this are phone-class. */
const DESKTOP_TEXTURE_SIZE = 8192;
/** Backing-store pixels of the canvas on a light device while photo-real shows (before any loss). */
const LIGHT_MAX_PIXELS = 2_000_000;
/** Pixel ratio cap of a light device while the camera moves (full resolution again once it settles). */
const LIGHT_MOVING_RATIO = 1.25;
/** Spark packs splats into 2048-wide texture arrays; anything smaller cannot hold a wall. */
const MIN_TEXTURE_SIZE = 2048;
/** Each lost context scales the resolution by this, down to MIN_SCALE. */
const LOSS_SCALE = 0.75;
const MIN_SCALE = 0.5;
/** Waits for the browser to restore the context on its own, then asks; then gives up. */
const ASK_RESTORE_MS = 1500;
const GIVE_UP_MS = 10000;
/** Losses of the smallest level after which the mode gives up. */
const MAX_SMALLEST_LOSSES = 2;

/**
 * Whether this device should get the light treatment: little memory, a phone-class GPU, or a phone /
 * tablet (mobile UA, iPadOS posing as a Mac, or a coarse touch-only pointer).
 */
export function prefersLightSplat(renderer) {
    const forced = new URLSearchParams(location.search).get('splatLod');
    if (forced === 'full' || forced === 'mobile') return forced === 'mobile';
    const gl = renderer.getContext();
    const memory = navigator.deviceMemory;
    const ua = navigator.userAgent || '';
    const mobileUa = /Android|iPhone|iPad|iPod|Mobile/i.test(ua);
    const iPadOs = /Macintosh/.test(ua) && navigator.maxTouchPoints > 1;
    const touchOnly = navigator.maxTouchPoints > 0 && window.matchMedia('(pointer: coarse)').matches
        && !window.matchMedia('(any-pointer: fine)').matches;
    return (typeof memory === 'number' && memory <= LOW_MEMORY_GB)
        || gl.getParameter(gl.MAX_TEXTURE_SIZE) < DESKTOP_TEXTURE_SIZE
        || mobileUa || iPadOs || touchOnly;
}

/** WebGL 2 with texture arrays large enough for Spark. */
export function supportsPhotoReal(renderer) {
    const gl = renderer.getContext();
    return typeof WebGL2RenderingContext !== 'undefined'
        && gl instanceof WebGL2RenderingContext
        && gl.getParameter(gl.MAX_TEXTURE_SIZE) >= MIN_TEXTURE_SIZE
        && gl.getParameter(gl.MAX_ARRAY_TEXTURE_LAYERS) >= 1;
}

/**
 * The canvas resolution while photo-real shows: up to 2× on a light device with at most LIGHT_MAX_PIXELS
 * backing pixels, the page's own ratio elsewhere; both scaled down after every lost context. While
 * the camera moves (`interact(true)`, from the render loop, inside a frame) a light device drops to
 * LIGHT_MOVING_RATIO: a moving splat shows no fine detail anyway, and the pixels are most of its cost.
 */
export function createRenderScale(renderer, light) {
    const full = renderer.getPixelRatio();
    let scale = 1;
    let on = false;
    let moving = false;
    function update() {
        let ratio = full;
        if (on) {
            ratio = (light ? Math.min(full, 2) : full) * scale;
            const c = renderer.domElement;
            const area = Math.max(1, c.clientWidth * c.clientHeight);
            if (light) ratio = Math.min(ratio, Math.sqrt(LIGHT_MAX_PIXELS * scale * scale / area));
            if (light && moving) ratio = Math.min(ratio, LIGHT_MOVING_RATIO * scale);
            ratio = Math.max(0.25, ratio);
        }
        // setPixelRatio resizes (and clears) the drawing buffer: only when it really changes.
        if (Math.abs(renderer.getPixelRatio() - ratio) > 1e-3) renderer.setPixelRatio(ratio);
    }
    return {
        lower() { scale = Math.max(MIN_SCALE, scale * LOSS_SCALE); },
        apply(value) { on = value; update(); },
        interact(value) { moving = value; update(); },
    };
}

/**
 * @param ctx.renderer      the view's renderer
 * @param ctx.say           (event, extra) diagnostics report
 * @param ctx.culprit       () → the level that was showing or loading when the context went
 * @param ctx.culpritIndex  () → its index
 * @param ctx.drop          () drops the Spark scene (its GPU objects died with the context)
 * @param ctx.resume        (index) rebuilds the scene at that level once the context is back
 * @param ctx.giveUp        () ends the mode quietly
 */
export function createRecovery({ renderer, say, culprit, culpritIndex, drop, resume, giveUp }) {
    // Fetched while the context is alive: a lost context answers getExtension with null.
    const loseExt = renderer.getContext().getExtension('WEBGL_lose_context');
    let recovering = false;
    let lostCount = 0;
    let smallestLosses = 0;
    let resumeAt = 0;
    let lostAt = 0;
    const timers = [];

    function clearTimers() {
        while (timers.length) clearTimeout(timers.pop());
    }

    return {
        get recovering() { return recovering; },
        get lostCount() { return lostCount; },

        /** Handles `webglcontextlost`; always true (the loss is ours to recover from). */
        lost(event) {
            event?.preventDefault?.();
            if (recovering) return true;
            recovering = true;
            lostCount++;
            lostAt = performance.now();
            const i = culpritIndex();
            const level = culprit();
            if (level) rememberFailure(level);
            if (i === 0) smallestLosses++;
            resumeAt = Math.max(0, i - 1);
            say('lost', { level: i, splats: level?.splats ?? null });
            drop();
            // Asked even when giving up: the modelled view needs the context back as well.
            timers.push(setTimeout(() => {
                try { loseExt?.restoreContext(); } catch { /* the browser restores on its own, or not at all */ }
            }, ASK_RESTORE_MS));
            if (smallestLosses >= MAX_SMALLEST_LOSSES) {
                // Keep `recovering` so nothing restarts the scene; the modelled view takes over.
                say('give-up', { detail: 'smallest level lost twice' });
                giveUp();
                return true;
            }
            timers.push(setTimeout(() => {
                if (!recovering) return;
                say('give-up', { detail: 'context not restored' });
                giveUp();
            }, GIVE_UP_MS));
            return true;
        },

        /** Handles `webglcontextrestored` (three.js has rebuilt its own state by now). */
        restored() {
            if (!recovering || smallestLosses >= MAX_SMALLEST_LOSSES) return;
            clearTimers();
            recovering = false;
            say('restored', { elapsedMs: performance.now() - lostAt, level: resumeAt });
            resume(resumeAt);
        },

        cancel() {
            clearTimers();
        },
    };
}
