// The splat levels of the photo-real view (wall3d-splat.js) in the scene. Spark is imported on first
// use; a level is downloaded by the app (wall3d-splat-fetch.js, so it can be aborted), decoded into a
// SplatMesh behind the level showing and then swapped in. `abort()` stops what loads now: its download
// ends at once and a decode that already runs (Spark cannot stop one) is thrown away when it lands.
import { createAborter, fetchSplatBytes } from './wall3d-splat-fetch.js';

/** Minimum time between two splat sorts on a phone (each one reads back and sorts every splat). */
const LIGHT_SORT_MS = 90;

/**
 * @param ctx.levels   the ladder (wall3d-splat-ladder.js)
 * @param ctx.cull     the splat cull (wall3d-splat-cull.js) or null: splats nobody can see are dropped as the level is built
 * @param ctx.retry    the shader retry (wall3d-splat-safe.js): its renderer / mesh options
 * @param ctx.visible  () => whether the photo-real mode shows (a new level starts visible or hidden)
 * @param ctx.closed   () => true once the view is disposed
 * @param ctx.onShown  () => a level was swapped in
 */
export function createLevelLoader({ renderer, scene, view, levels, light, clip, cull, retry, request, visible, closed, onShown }) {
    const aborter = createAborter();
    let spark = null;
    let sparkRenderer = null;
    let mesh = null;
    let index = -1;                 // the level showing
    let culled = null;              // { kept, total } of the level showing when the cull ran
    let loadingIndex = -1;          // the level loading, -1 when none
    let epoch = 0;                  // bumps on abort(): loads of an older epoch are dropped
    let downloading = 0;            // loads not yet handed to Spark (a decode cannot be aborted)

    async function bytesOf(i, signal, progress) {
        downloading++;
        try {
            spark ??= await import('../lib/spark/spark.module.min.js');
            return await fetchSplatBytes(levels[i].url, signal, progress);
        } finally {
            downloading--;
        }
    }

    function ensureRenderer() {
        if (sparkRenderer) return;
        sparkRenderer = new spark.SparkRenderer({
            renderer, onDirty: () => request(), minSortIntervalMs: light ? LIGHT_SORT_MS : 0,
            ...clip?.rendererOptions, ...retry.rendererOptions,
        });
        clip?.install(sparkRenderer);
        sparkRenderer.visible = visible();
        scene.add(sparkRenderer);
    }

    /** Loads level `i` and swaps it in; false when it was dropped (aborted, disposed, a lost context). */
    async function load(i, progress) {
        const myEpoch = epoch;
        const signal = aborter.signal;
        const stale = () => closed() || myEpoch !== epoch;
        loadingIndex = i;
        let next = null;
        try {
            const fileBytes = await bytesOf(i, signal, progress);
            if (!fileBytes || stale()) return false;
            ensureRenderer();
            next = cull
                ? cull.mesh(spark, fileBytes, retry.meshOptions)
                : new spark.SplatMesh({ fileBytes, fileType: 'spz', ...retry.meshOptions });
            next.matrixAutoUpdate = false;
            next.matrix.fromArray(view.splatMatrix);
            next.matrixWorldNeedsUpdate = true;
            next.visible = false;
            scene.add(next);
            await next.initialized;
        } catch (err) {
            if (next) { scene.remove(next); next.dispose(); }
            throw err;
        } finally {
            loadingIndex = -1;
        }
        if (stale() || !sparkRenderer) {
            scene.remove(next);
            next.dispose();
            return false;
        }
        const old = mesh;
        retry.loaded();
        mesh = next;
        index = i;
        culled = next.cullStats ?? null;
        mesh.visible = visible();
        if (old) { scene.remove(old); old.dispose(); }
        onShown();
        request();
        return true;
    }

    function release() {
        for (const o of [mesh, sparkRenderer]) {
            if (!o) continue;
            scene.remove(o);
            try { o.dispose(); } catch { /* a lost context has nothing left to free */ }
        }
        mesh = null;
        sparkRenderer = null;
        index = -1;
    }

    return {
        get mesh() { return mesh; },
        get sparkRenderer() { return sparkRenderer; },
        get index() { return index; },
        get culled() { return culled; },
        get loadingIndex() { return loadingIndex; },
        get epoch() { return epoch; },
        /** True while a load is still downloading (abortable), false once only decodes run. */
        get downloading() { return downloading > 0; },
        load,
        /** Stops what loads now; the level showing stays. */
        abort() {
            epoch++;
            aborter.abort();
        },
        /** Stops what loads now and releases the level showing (a lost context, a shader retry, dispose). */
        drop() {
            epoch++;
            aborter.abort();
            release();
        },
    };
}
