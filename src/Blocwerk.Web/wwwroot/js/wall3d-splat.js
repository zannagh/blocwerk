// Photo-real mode of the 3D wall view (wall3d.js): the capture's Gaussian splat, rendered by Spark
// 2.2 (wwwroot/lib/spark, MIT) and laid over the solved facets. Spark is ~2.7 MB, so it is imported
// only when somebody turns the mode on. The splat file stays in the splat worker's own coordinates;
// `view.splatMatrix` (column-major, from frame.json's toWorldMm) moves it into the view's world frame
// (wall-geometry millimetres, z up), where the facets and holds already are.
//
// While the mode is on the view renders continuously: Spark sorts splats in a worker and needs the
// frames after a camera move to show the re-sorted result.
//
// Phones and low-memory devices get the pruned mobile level of detail (`view.splatMobileUrl`, ~180k
// splats, SpzDecimator on the server) and a 1× pixel ratio while the mode is on: a half-million-splat
// capture sorted and blended at 2× every frame can make iOS Safari drop the WebGL context, which
// shows as a black canvas. `?splatLod=full|mobile` on the page overrides the pick (testing).

/** Spark packs splats into 2048-wide texture arrays; anything smaller cannot hold a wall. */
const MIN_TEXTURE_SIZE = 2048;

/** At most this much device memory (GB, `navigator.deviceMemory`; Chromium only) counts as low. */
const LOW_MEMORY_GB = 4;
/** GPUs whose largest texture is smaller than this are phone-class. */
const DESKTOP_TEXTURE_SIZE = 8192;

export class PhotoRealUnsupportedError extends Error {}

/**
 * Whether this device should get the light splat: little memory, a phone-class GPU, or a phone /
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

function supported(renderer) {
    const gl = renderer.getContext();
    return typeof WebGL2RenderingContext !== 'undefined'
        && gl instanceof WebGL2RenderingContext
        && gl.getParameter(gl.MAX_TEXTURE_SIZE) >= MIN_TEXTURE_SIZE
        && gl.getParameter(gl.MAX_ARRAY_TEXTURE_LAYERS) >= 1;
}

/**
 * @param ctx.renderer   the view's THREE.WebGLRenderer
 * @param ctx.scene      the view's scene
 * @param ctx.view       the Wall3DView payload (splatUrl, splatMatrix)
 * @param ctx.facetParts objects hidden while the photo-real scene shows (facets, textures, markers, …)
 * @param ctx.onProgress (fraction 0..1 or null when unknown) while the file downloads
 */
export function createPhotoReal({ renderer, scene, view, facetParts, onProgress }) {
    let sparkRenderer = null;
    let mesh = null;
    let loading = null;
    let active = false;
    let disposed = false;
    let broken = false;
    const light = prefersLightSplat(renderer);
    const fullPixelRatio = renderer.getPixelRatio();

    async function load() {
        if (broken) throw new PhotoRealUnsupportedError('The photo-real view failed on this device.');
        if (!supported(renderer)) throw new PhotoRealUnsupportedError('WebGL 2 with large texture arrays is required.');
        const { SparkRenderer, SplatMesh } = await import('../lib/spark/spark.module.min.js');
        if (disposed) return;
        sparkRenderer = new SparkRenderer({ renderer });
        mesh = new SplatMesh({
            url: light && view.splatMobileUrl ? view.splatMobileUrl : view.splatUrl,
            fileType: 'spz',
            // A late progress event must not re-open the "Loading…" bubble over a finished scene.
            onProgress: e => { if (!mesh?.isInitialized) onProgress(e && e.lengthComputable && e.total > 0 ? e.loaded / e.total : null); },
        });
        mesh.matrixAutoUpdate = false;
        mesh.matrix.fromArray(view.splatMatrix);
        mesh.matrixWorldNeedsUpdate = true;
        mesh.visible = false;
        scene.add(sparkRenderer, mesh);
        await mesh.initialized;
    }

    function apply() {
        // The SparkRenderer draws its last sorted splats itself, so it is hidden too: with only the
        // mesh hidden, an on-demand redraw after switching back still showed the splat.
        if (mesh) mesh.visible = active;
        if (sparkRenderer) sparkRenderer.visible = active;
        for (const part of facetParts) part.visible = !active;
        if (light) renderer.setPixelRatio(active ? 1 : fullPixelRatio);
    }

    function release() {
        if (mesh) { scene.remove(mesh); mesh.dispose(); }
        if (sparkRenderer) { scene.remove(sparkRenderer); sparkRenderer.dispose(); }
        mesh = null;
        sparkRenderer = null;
        loading = null;
    }

    return {
        get available() { return !!(view.splatUrl && view.splatMatrix && view.splatMatrix.length === 16); },
        /** Whether this device got the light (mobile) splat and pixel ratio. */
        get light() { return light && !!view.splatMobileUrl; },
        get active() { return active; },
        get loaded() { return !!mesh && mesh.isInitialized; },

        /** Switches the mode; resolves once the scene shows what was asked for. Throws on failure. */
        async setActive(on) {
            if (disposed || on === active) return;
            if (on) {
                loading ??= load().catch(err => { loading = null; throw err; });
                await loading;
                if (disposed) return;
                if (broken) throw new PhotoRealUnsupportedError('The photo-real view failed on this device.');
            }
            active = on;
            apply();
        },

        /**
         * Gives up on the mode after a render failure (lost WebGL context, shader error): the splat is
         * dropped and every later `setActive(true)` throws PhotoRealUnsupportedError.
         */
        fail() {
            broken = true;
            active = false;
            apply();
            release();
        },

        dispose() {
            if (disposed) return;
            disposed = true;
            active = false;
            apply();
            release();
        },
    };
}
