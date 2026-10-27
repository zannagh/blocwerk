/*
 * bwClient3dStage — mounts the Server's own 3D viewer (Web's js/wall3d.js) into the offline client's 3D view (Phase 3
 * PR 3.13), with the view bwClient3d builds from the device, and frees it again. The modules load only here, on the
 * first mount: a page that never opens 3D loads none of them. The shell names every one of them in its import map
 * (OfflineClient3DAssets), so the worker precaches them with the client and serves them offline.
 *
 * THE FIRST ~20 S (decided, from the 3.8 gate): the client never puts a splat in memory before HOLD_MS after its
 * first render (the layout's `bw-client-ready` mark), while the WebAssembly runtime's start still holds 100–200 MiB
 * extra. Each mount sets wall3d-splat-hold.js's hold to the time left, so the view shows Schematic first and a
 * photo-real pick, or a ?mode=photoreal deep link, switches once the hold ends. Tests shorten it with
 * `window.__bwClient3dHoldMs` and swap the modules with `window.__bwClient3dModules`.
 *
 *   mount(element, requestJson) → 'ok' | 'missing' (no longer ready on the device) | 'unavailable' (the modules did
 *     not load: offline and not precached) | 'model' (the model could not be drawn) | 'webgl' (no WebGL) | 'stale'
 *     (unmounted or mounted again meanwhile). The request: { partition, wallId, sha, token, boulder, mode, roleColors }.
 *   unmount(element)  frees the viewer and its WebGL context; a mount still running drops what it gets.
 */
window.bwClient3dStage = (function (win) {
    'use strict';

    const HOLD_MS = 20000;
    const READY_MARK = 'bw-client-ready';
    const MODEL_FAILED = 'wall3d-model:';
    const mounts = new WeakMap();
    let seq = 0;

    function modules() {
        if (typeof win.__bwClient3dModules === 'function') {
            return win.__bwClient3dModules();
        }
        return Promise.all([import('/js/wall3d.js'), import('/js/wall3d-splat-hold.js')]);
    }

    /** Milliseconds until a splat may load: HOLD_MS after the first render (the page start without the mark). */
    function holdLeft(now, mark, holdMs) {
        const from = mark && Number.isFinite(mark.startTime) ? mark.startTime : 0;
        return Math.max(0, from + holdMs - now);
    }

    function currentHoldLeft() {
        const override = win.__bwClient3dHoldMs;
        const holdMs = Number.isFinite(override) ? override : HOLD_MS;
        return holdLeft(win.performance.now(), win.performance.getEntriesByName(READY_MARK)[0], holdMs);
    }

    function hold(holdModule) {
        const left = currentHoldLeft();
        holdModule.holdPhotoReal(left > 0 ? new Promise(resolve => win.setTimeout(resolve, left)) : null);
    }

    const isCurrent = (element, token) => (mounts.get(element) || {}).token === token;

    function disposeQuietly(handle) {
        try {
            handle?.dispose();
        } catch (e) {
            /* the context is gone already */
        }
    }

    async function mount(element, requestJson) {
        const request = JSON.parse(requestJson);
        unmount(element);
        const token = ++seq;
        mounts.set(element, { token, handle: null });
        const view = await win.bwClient3d.view(request.partition, request.wallId, request.sha, request.token, request.boulder);
        if (!isCurrent(element, token)) {
            return 'stale';
        }
        if (!view) {
            return 'missing';
        }
        let wall3d;
        try {
            const [viewer, holdModule] = await modules();
            hold(holdModule);
            wall3d = viewer;
        } catch (e) {
            return isCurrent(element, token) ? 'unavailable' : 'stale';
        }
        if (!isCurrent(element, token)) {
            return 'stale';
        }
        let handle;
        try {
            handle = await wall3d.mount(element, view, {
                roleColors: request.roleColors || {},
                boulderFocus: !!request.boulder,
                initialPreset: 'front',
                initialMode: request.mode || 'schematic',
                hintOnce: true,
                highlight: null,
            });
        } catch (err) {
            return !isCurrent(element, token) ? 'stale' : String(err && err.message).startsWith(MODEL_FAILED) ? 'model' : 'webgl';
        }
        if (!isCurrent(element, token)) {
            disposeQuietly(handle);
            return 'stale';
        }
        mounts.get(element).handle = handle;
        return 'ok';
    }

    function unmount(element) {
        const current = element ? mounts.get(element) : null;
        if (current) {
            mounts.delete(element);
            disposeQuietly(current.handle);
        }
    }

    return { HOLD_MS, holdLeft, mount, unmount };
})(window);
