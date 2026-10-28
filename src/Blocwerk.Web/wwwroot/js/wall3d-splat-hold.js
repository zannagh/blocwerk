// The offline client's hold on the photo-real view (plan 2026-10-22, PR 3.13): the client never puts a splat in memory
// in its first ~20 s after boot, while the WebAssembly runtime's start still holds 100–200 MiB extra (the 3.8 gate:
// a splat mounted then cost WebKit ~130 MiB more). The client (v/js/client-3d.js) sets the hold before it mounts the
// view; wall3d-splat-levels.js waits for it before it imports Spark or downloads a level, so the view shows Schematic
// meanwhile and a photo-real pick (or a ?mode=photoreal deep link) switches once the hold ends. The Server pages never
// set one: photoRealHold() resolves at once there.

let held = null;

/** Holds every photo-real level load until `until` (a promise) settles; a falsy `until` lifts the hold. */
export function holdPhotoReal(until) {
    held = until ? Promise.resolve(until).then(() => undefined, () => undefined) : null;
}

/** Whether a hold is set (it may already have ended). */
export function photoRealHeld() {
    return held !== null;
}

/** Resolves once the hold ends, or at once without one; an abort of `signal` resolves it early (the caller drops the load). */
export function photoRealHold(signal) {
    if (!held || signal?.aborted) return Promise.resolve();
    return new Promise(resolve => {
        held.then(resolve);
        signal?.addEventListener('abort', () => resolve(), { once: true });
    });
}
