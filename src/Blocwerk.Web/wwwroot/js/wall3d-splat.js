// Photo-real mode of the 3D wall view (wall3d.js): the capture's Gaussian splat, rendered by Spark
// 2.2 (wwwroot/lib/spark, MIT) and laid over the solved facets. Spark is ~2.7 MB, so it is imported
// only when somebody turns the mode on. The splat file stays in the splat worker's own coordinates;
// `view.splatMatrix` (column-major, from frame.json's toWorldMm) moves it into the view's world frame
// (wall-geometry millimetres, z up), where the facets and holds already are.
//
// Streaming instead of all-or-nothing: the scene comes as a level-of-detail ladder
// (wall3d-splat-ladder.js). The smallest level shows first; while a short probe finds frames fast the
// view steps up one level at a time (the next level loads behind the one showing, then replaces it),
// up to what the device may take (wall3d-splat-policy.js; Detail: Ultra climbs to the full scene
// without measuring, wall3d-splat-detail.js). A lost WebGL context (iOS Safari drops it
// when a frame or the tab is too heavy) is not an error: the view waits for the context to come back
// and resumes one level lower at a lower resolution (wall3d-splat-recover.js). Only when even the smallest level is lost twice does it give
// up, quietly, back to Schematic. Every step is reported to the server log (wall3d-splat-diag.js).
// A shader that fails (iOS's Metal translator) is retried once on a plainer Spark path.
//
// Rendered on demand (wall3d-loop.js): Spark re-sorts only after a camera move and its `onDirty`
// asks for the frame showing the finished sort; phones space sorts LIGHT_SORT_MS apart.
import { createDetailBadge, ladderOf, levelCap, pinnedLevel, remembered, rememberSuccess, sizeOf } from './wall3d-splat-ladder.js';
import { storeDetail, storedDetail } from './wall3d-splat-detail.js';
import { createLadderPolicy } from './wall3d-splat-policy.js';
import { deviceFacts, report } from './wall3d-splat-diag.js';
import { PHOTO_REAL_FAILED } from './wall3d-modes.js';
import { createRecovery, createRenderScale, prefersLightSplat, supportsPhotoReal as supported } from './wall3d-splat-recover.js';
import { releaseTextures } from './wall3d-stage.js';
import { createShaderRetry } from './wall3d-splat-safe.js';

export { prefersLightSplat };

/** Minimum time between two splat sorts on a phone (each one reads back and sorts every splat). */
const LIGHT_SORT_MS = 90;

export class PhotoRealUnsupportedError extends Error {}

/**
 * @param ctx.renderer      the view's THREE.WebGLRenderer
 * @param ctx.scene         the view's scene
 * @param ctx.view          the Wall3DView payload (splatLevels / splatUrl, splatMatrix)
 * @param ctx.facetParts    objects hidden while the photo-real scene shows (facets, textures, markers, …)
 * @param ctx.photoTextures the facet photo group, whose GPU textures are freed while photo-real shows
 * @param ctx.onProgress    (fraction 0..1 or null when unknown) while the first level downloads
 * @param ctx.onGiveUp      (message) when even the smallest level cannot be shown on this device
 * @param ctx.clip          the splat clip (wall3d-splat-clip.js): near fade, mats, ghosted facets
 * @param ctx.request       asks the render loop for a frame (a sort finished, a level swapped in)
 */
export function createPhotoReal({ renderer, scene, view, facetParts, photoTextures, onProgress, onGiveUp, clip, request = () => {} }) {
    const levels = ladderOf(view);
    const light = prefersLightSplat(renderer);
    const facts = { ...deviceFacts(renderer), mobile: light };
    const scale = createRenderScale(renderer, light);
    const policy = createLadderPolicy(light);
    let detailMode = storedDetail();    // 'auto' | 'high' | 'ultra' (wall3d-splat-detail.js)
    let onLevel = null;             // the Detail toggle's label follows the level drawn
    let cap = levels.length > 0 ? levelCap(levels, light, detailMode) : 0;
    let spark = null;               // the Spark module, once imported
    let sparkRenderer = null;
    let mesh = null;
    let index = -1;                 // the level showing
    let loadingIndex = -1;          // the level loading, -1 when none
    let epoch = 0;                  // bumps on a lost context: loads of an older epoch are dropped
    let loading = null;
    let stepping = null;            // the epoch of the step in flight, null when none
    let active = false;
    let disposed = false;
    let broken = false;
    let startedAt = 0;

    const state = extra => ({
        level: index, levels: levels.length, splats: index >= 0 ? levels[index].splats : null,
        frameMs: policy.monitor.median || null, elapsedMs: startedAt ? performance.now() - startedAt : null,
        lostCount: recovery.lostCount, safeSplats: remembered().failSplats ?? null, mobile: light, ...extra,
    });
    const say = (event, extra) => report(event, renderer, facts, state(extra));
    const detail = createDetailBadge(renderer.domElement.parentElement);
    const retry = createShaderRetry(renderer, say, () => !disposed && !broken && onGiveUp?.(PHOTO_REAL_FAILED));

    const recovery = createRecovery({
        renderer, say,
        culprit: () => levels[Math.max(index, loadingIndex, 0)],
        culpritIndex: () => Math.max(index, loadingIndex, 0),
        drop() {
            epoch++;
            release();
            detail.show('Restoring detail…');
        },
        resume(lowered) {
            cap = Math.min(cap, lowered);
            scale.lower();
            if (active) scale.apply(true);
            stepTo(lowered, true);
        },
        giveUp() {
            detail.hide();
            onGiveUp?.('Photo-real is taking a break on this device, so the view shows Schematic.');
        },
    });

    async function loadLevel(i, progress) {
        spark ??= await import('../lib/spark/spark.module.min.js');
        if (disposed) return false;
        const myEpoch = epoch;
        if (!sparkRenderer) {
            sparkRenderer = new spark.SparkRenderer({
                renderer, onDirty: () => request(), minSortIntervalMs: light ? LIGHT_SORT_MS : 0,
                ...clip?.rendererOptions, ...retry.rendererOptions,
            });
            clip?.install(sparkRenderer);
            sparkRenderer.visible = active;
            scene.add(sparkRenderer);
        }
        const next = new spark.SplatMesh({
            url: levels[i].url,
            fileType: 'spz',
            ...retry.meshOptions,
            // A late progress event must not re-open the "Loading…" bubble over a finished scene.
            onProgress: progress ? e => { if (!next.isInitialized) progress(e && e.lengthComputable && e.total > 0 ? e.loaded / e.total : null); } : undefined,
        });
        next.matrixAutoUpdate = false;
        next.matrix.fromArray(view.splatMatrix);
        next.matrixWorldNeedsUpdate = true;
        next.visible = false;
        scene.add(next);
        loadingIndex = i;
        try {
            await next.initialized;
        } catch (err) {
            scene.remove(next);
            next.dispose();
            throw err;
        } finally {
            loadingIndex = -1;
        }
        if (disposed || myEpoch !== epoch || !sparkRenderer) {
            scene.remove(next);
            next.dispose();
            return false;
        }
        const old = mesh;
        retry.loaded();
        mesh = next;
        index = i;
        mesh.visible = active;
        if (old) { scene.remove(old); old.dispose(); }
        policy.shown(index < cap && detailMode !== 'ultra');
        onLevel?.();
        request();
        return true;
    }

    /** Loads level `i` behind the one showing and swaps it in; a failure caps the ladder where it is. */
    function stepTo(i, recovering = false) {
        // A step of an older context epoch (lost mid-download) does not block the resume.
        if (stepping === epoch || disposed || broken) return;
        const myEpoch = epoch;
        stepping = myEpoch;
        detail.show(recovering ? 'Restoring detail…' : 'Loading detail…');
        loadLevel(i, null)
            .then(ok => { if (ok) say(recovering ? 'resumed' : 'level'); })
            .catch(err => {
                console.warn('wall3d: photo-real level failed', err);
                cap = Math.max(0, Math.min(cap, index));
                say('level-failed', { detail: String(err?.message || err), level: i });
            })
            .finally(() => {
                if (stepping !== myEpoch) return;
                stepping = null;
                if (!recovery.recovering) detail.hide();
            });
    }

    async function start() {
        if (broken) throw new PhotoRealUnsupportedError('The photo-real view failed on this device.');
        if (!supported(renderer)) throw new PhotoRealUnsupportedError('WebGL 2 with large texture arrays is required.');
        if (levels.length === 0) throw new Error('No photo-real scene.');
        startedAt = performance.now();
        const first = pinnedLevel(levels.length) ?? 0;
        say('start', { level: first, detail: `cap ${cap}, levels ${levels.map(l => l.splats).join('/')}` });
        try {
            await loadLevel(first, onProgress);
        } catch (err) {
            say('level-failed', { level: first, detail: String(err?.message || err) });
            await loadLevel(first, onProgress);      // one retry: a flaky phone connection
        }
        say('level');
    }

    function apply() {
        // The SparkRenderer draws its last sorted splats itself: hidden too, or a redraw shows them.
        if (mesh) mesh.visible = active;
        if (sparkRenderer) sparkRenderer.visible = active;
        for (const part of facetParts) part.visible = !active;
        if (active) releaseTextures(photoTextures);
        scale.apply(active);
        if (!active) detail.hide();
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
        get available() { return !!(levels.length > 0 && view.splatMatrix && view.splatMatrix.length === 16); },
        get light() { return light; },
        get active() { return active; },
        get loaded() { return !!mesh && mesh.isInitialized; },
        /** The level showing, the levels and the cap (diagnostics, the screenshot harness). */
        get level() { return { index, cap, count: levels.length, splats: index >= 0 ? levels[index].splats : 0, stepping: stepping !== null, recovering: recovery.recovering }; },

        /** Switches the mode; resolves once the scene shows what was asked for. Throws on failure. */
        async setActive(on) {
            if (disposed || on === active) return;
            if (on) {
                loading ??= start().catch(err => { loading = null; throw err; });
                await loading;
                if (disposed) return;
                if (broken) throw new PhotoRealUnsupportedError('The photo-real view failed on this device.');
            }
            active = on;
            apply();
        },

        /** Called every drawn frame while the mode shows: measures and steps the ladder. */
        frame(now) {
            if (!active || !mesh || stepping !== null || recovery.recovering || broken) return;
            const next = index + 1;
            const ultra = detailMode === 'ultra';
            if (next <= cap && (ultra || sizeOf(levels[next]) <= (remembered().okSplats ?? 0))) {
                policy.stop();
                stepTo(next);                     // Ultra, or ran fine here before: no need to measure
                return;
            }
            if (ultra) return;                    // Ultra keeps the full level: no step-down for load
            const decision = policy.frame(now, detailMode === 'auto' && index > 1);
            if (decision === 'up') {
                rememberSuccess(levels[index]);
                if (next <= cap) stepTo(next);
            } else if ((decision === 'down' || decision === 'sustained') && index > 0) {
                cap = index - 1;
                say(decision === 'down' ? 'slow' : 'sustained', { detail: `median ${policy.monitor.median.toFixed(1)} ms` });
                stepTo(index - 1);
            }
        },

        /** True while the ladder wants back-to-back frames (a probe measuring the level on show). */
        get wantsFrames() { return active && !!mesh && policy.probing && stepping === null && !recovery.recovering; },
        /** Whether the loop caps the frame rate (a phone, outside a probe). */
        get capped() { return active && light && !policy.probing; },
        /** A camera move started / settled: a phone renders at a lower resolution in between. */
        interact(moving) { scale.interact(moving); },
        /** The Detail choice: 'auto' | 'high' | 'ultra' (wall3d-splat-detail.js). */
        get detail() { return detailMode; },
        /** Whether the Detail toggle shows: a ladder to choose on; High only lifts a phone's cap. */
        get detailChoice() { return levels.length > 1; },
        get highLifts() { return light && levels.length > 0 && levelCap(levels, true, 'high') > levelCap(levels, true, 'auto'); },
        /** Called when the level drawn changes (the toggle's label). */
        set onLevel(fn) { onLevel = fn; },
        /** A new Detail choice: moves the cap and steps down to it, or lets the ladder climb. */
        setDetail(mode) {
            detailMode = mode;
            storeDetail(mode);
            cap = levelCap(levels, light, detailMode);
            if (!mesh || broken) return;
            if (index > cap) stepTo(cap);
            else policy.shown(index < cap && mode !== 'ultra');
            request();
        },

        /** A lost WebGL context: true when photo-real handles it (it had started), else false. */
        contextLost: event => !disposed && !broken && !!loading && recovery.lost(event),
        contextRestored: () => recovery.restored(),

        /** Gives up after a render failure: every later `setActive(true)` throws (a restore still runs). */
        fail(reason, shader) {
            if (reason && !broken) say('failed', { detail: reason, shader });
            broken = true;
            active = false;
            apply();
            release();
            loading = null;
        },

        /**
         * A shader failed (describeShaderFailure): reloads the level on the plainer Spark path
         * (wall3d-splat-safe.js), once. False when there is nothing (left) to retry: Schematic then.
         */
        retrySimple(failure) {
            if (disposed || broken || !loading) return false;
            const level = Math.max(0, index);
            // Not mid-render: three.js reports the failure while it draws the scene being released.
            return retry.start(failure, () => { epoch++; release(); stepTo(level, true); });
        },

        dispose() {
            if (disposed) return;
            disposed = true;
            active = false;
            recovery.cancel();
            apply();
            release();
            detail.remove();
        },
    };
}
