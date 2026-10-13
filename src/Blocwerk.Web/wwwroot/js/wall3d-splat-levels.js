// The splat levels of the photo-real view (wall3d-splat.js) in the scene. Spark is imported on first
// use; a level is downloaded by the app (wall3d-splat-fetch.js, so it can be aborted), decoded into a
// SplatMesh behind the level showing and then swapped in. `abort()` stops what loads now: its download
// ends at once and a decode that already runs (Spark cannot stop one) is thrown away when it lands.
// A decode that never answers (a stuck Spark worker, seen on iOS) fails after DECODE_TIMEOUT_MS instead
// of leaving the view on its level without a word; every stage is handed to `onStage` for the server log.
import { createAborter, fetchSplatBytes } from './wall3d-splat-fetch.js';

/** Minimum time between two splat sorts on a phone (each one reads back and sorts every splat). */
const LIGHT_SORT_MS = 90;
/** A decode without an answer for this long (while the page shows) fails; `?splatDecodeMs=` for tests. */
const DECODE_TIMEOUT_MS = +(new URLSearchParams(location.search).get('splatDecodeMs') || 30_000);

const since = t => Math.round(performance.now() - t);

/** `promise`, or an error once it has not settled after `ms` with the page showing (a hidden tab is paused). */
function within(promise, ms) {
    return new Promise((resolve, reject) => {
        let timer = 0;
        const done = (settle, value) => {
            clearTimeout(timer);
            document.removeEventListener('visibilitychange', arm);
            settle(value);
        };
        function arm() {
            clearTimeout(timer);
            if (document.visibilityState === 'hidden') return;
            timer = setTimeout(() => done(reject, new Error(`no answer for ${Math.round(ms / 1000)} s`)), ms);
        }
        document.addEventListener('visibilitychange', arm);
        arm();
        promise.then(v => done(resolve, v), e => done(reject, e));
    });
}

/**
 * @param ctx.levels   the ladder (wall3d-splat-ladder.js)
 * @param ctx.cull     the splat cull (wall3d-splat-cull.js) or null: splats nobody can see are dropped as the level is built
 * @param ctx.retry    the shader retry (wall3d-splat-safe.js): its renderer / mesh options
 * @param ctx.visible  () => whether the photo-real mode shows (a new level starts visible or hidden)
 * @param ctx.closed   () => true once the view is disposed
 * @param ctx.onShown  () => a level was swapped in
 * @param ctx.onStage  (stage, index, detail) => a level was 'downloaded' / 'decoded' (the diagnostics)
 */
export function createLevelLoader({ renderer, scene, view, levels, light, clip, cull, retry, request, visible, closed, onShown, onStage }) {
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

    /**
     * Loads level `i` and swaps it in; false when it was dropped (aborted, disposed, a lost context).
     * An error carries `stage`: 'download' or 'decode'.
     */
    async function load(i, progress) {
        const myEpoch = epoch;
        const signal = aborter.signal;
        const stale = () => closed() || myEpoch !== epoch;
        loadingIndex = i;
        let next = null;
        let stage = 'download';
        const startedAt = performance.now();
        try {
            const fileBytes = await bytesOf(i, signal, progress);
            if (!fileBytes || stale()) return false;
            onStage?.('downloaded', i, `${(fileBytes.length / 1048576).toFixed(1)} MB in ${since(startedAt)} ms`);
            stage = 'decode';
            const decodeAt = performance.now();
            ensureRenderer();
            next = cull
                ? cull.mesh(spark, fileBytes, retry.meshOptions)
                : new spark.SplatMesh({ fileBytes, fileType: 'spz', ...retry.meshOptions });
            next.matrixAutoUpdate = false;
            next.matrix.fromArray(view.splatMatrix);
            next.matrixWorldNeedsUpdate = true;
            next.visible = false;
            scene.add(next);
            await within(next.initialized, DECODE_TIMEOUT_MS);
            const c = next.cullStats;
            onStage?.('decoded', i, c
                ? `decode ${c.decodeMs} ms, cull ${c.cullMs} ms, kept ${c.kept}/${c.total}`
                : `decode ${since(decodeAt)} ms`);
        } catch (err) {
            if (next) { scene.remove(next); next.dispose(); }
            if (err && typeof err === 'object') err.stage = stage;
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
