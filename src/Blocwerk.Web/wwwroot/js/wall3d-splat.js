// Photo-real mode of the 3D wall view (wall3d.js): the capture's Gaussian splat, rendered by Spark
// 2.2 (wwwroot/lib/spark, MIT) and laid over the solved facets. Spark is ~2.7 MB, so it is imported
// only when somebody turns the mode on. The splat file stays in the splat worker's own coordinates;
// `view.splatMatrix` (column-major, from frame.json's toWorldMm) moves it into the view's world frame
// (wall-geometry millimetres, z up), where the facets and holds already are.
//
// While the mode is on the view renders continuously: Spark sorts splats in a worker and needs the
// frames after a camera move to show the re-sorted result.

/** Spark packs splats into 2048-wide texture arrays; anything smaller cannot hold a wall. */
const MIN_TEXTURE_SIZE = 2048;

export class PhotoRealUnsupportedError extends Error {}

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

    async function load() {
        if (!supported(renderer)) throw new PhotoRealUnsupportedError('WebGL 2 with large texture arrays is required.');
        const { SparkRenderer, SplatMesh } = await import('../lib/spark/spark.module.min.js');
        if (disposed) return;
        sparkRenderer = new SparkRenderer({ renderer });
        mesh = new SplatMesh({
            url: view.splatUrl,
            fileType: 'spz',
            onProgress: e => onProgress(e && e.lengthComputable && e.total > 0 ? e.loaded / e.total : null),
        });
        mesh.matrixAutoUpdate = false;
        mesh.matrix.fromArray(view.splatMatrix);
        mesh.matrixWorldNeedsUpdate = true;
        mesh.visible = false;
        scene.add(sparkRenderer, mesh);
        await mesh.initialized;
    }

    function apply() {
        if (mesh) mesh.visible = active;
        for (const part of facetParts) part.visible = !active;
    }

    return {
        get available() { return !!(view.splatUrl && view.splatMatrix && view.splatMatrix.length === 16); },
        get active() { return active; },
        get loaded() { return !!mesh && mesh.isInitialized; },

        /** Switches the mode; resolves once the scene shows what was asked for. Throws on failure. */
        async setActive(on) {
            if (disposed || on === active) return;
            if (on) {
                loading ??= load().catch(err => { loading = null; throw err; });
                await loading;
                if (disposed) return;
            }
            active = on;
            apply();
        },

        dispose() {
            if (disposed) return;
            disposed = true;
            active = false;
            apply();
            if (mesh) { scene.remove(mesh); mesh.dispose(); }
            if (sparkRenderer) { scene.remove(sparkRenderer); sparkRenderer.dispose(); }
            mesh = null;
            sparkRenderer = null;
        },
    };
}
