/*
 * bwAssetClear — Settings → "Clear offline data" (design 2026-10-21 §6.8, PR 2.9): free the space now, then rebuild
 * what the device needs. Wall data is always cached, so a clear is never a way to stop caching.
 *
 *   clear():  stops this page's fill (`state.stopped`, which the runner reads as "hidden" at its next task) and waits for
 *             the running one, at most STOP_WAIT_MS (a large splat download must not hold the button). Then it deletes
 *             this partition's `bw-a1-{p}-img` / `-3d` caches and ledger rows: never `blocwerk-offline` (unsent ticks),
 *             `bw-sw-state` or the shell, and `meta.activePartition` stays. Then it rebuilds: `stopped` and the fresh
 *             set are reset and every wall of the viewer (and the open wall) is refetched and filled, core first.
 *   Other tabs of the same partition hear `clearing` (they stop their fill) and `cleared` (they rebuild) over a
 *   BroadcastChannel, or a localStorage event where there is none. A tab that never hears `cleared` (the clearing tab
 *   closed) rebuilds after RESUME_MS anyway.
 */
window.bwAssetClear = (function (win) {
    'use strict';

    const CHANNEL = 'bw-asset-clear';
    const PREFIX = 'bw-a1-';
    const STOP_WAIT_MS = 10000;
    const RESUME_MS = 30000;
    const REAL_LABEL = /^(anon|[uk]-[a-z2-7]{20})$/;

    const Cache = () => win.bwAssetCache;
    const wait = ms => new Promise(resolve => win.setTimeout(resolve, ms));
    let bus = null;
    let resumeTimer = null;

    function caching(state) {
        return !!state && !!state.env && typeof state.active === 'string' && REAL_LABEL.test(state.active);
    }

    async function started() {
        const cache = Cache();
        if (!cache) {
            return null;
        }
        await (cache.ready || Promise.resolve()).catch(() => undefined);
        const state = cache.state();
        return caching(state) ? state : null;
    }

    function post(message) {
        try {
            if (bus) {
                bus.postMessage(message);
            } else if (win.localStorage) {
                win.localStorage.setItem(CHANNEL, JSON.stringify(Object.assign({ at: Date.now(), nonce: Math.random() }, message)));
                win.localStorage.removeItem(CHANNEL);
            }
        } catch (e) {
            /* no channel: other tabs refill on their next load */
        }
    }

    // The running fill ends at its next task once `stopped` is set; a stuck download is not waited for past STOP_WAIT_MS.
    const drained = () => Promise.race([Cache().filled().catch(() => undefined), wait(STOP_WAIT_MS)]);

    function stop(state) {
        state.stopped = true;
        return drained();
    }

    async function wipe(state) {
        const partition = state.active;
        for (const suffix of ['img', '3d']) {
            try {
                await state.env.caches.delete(`${PREFIX}${partition}-${suffix}`);
            } catch (e) {
                /* storage unavailable: the reconcile on the next start catches up */
            }
        }
        for (const wall of await state.env.ledger.wallsOf(partition)) {
            await state.env.ledger.deleteWall(partition, wall.wallId);
        }
        for (const asset of await state.env.ledger.assetsOf(partition)) {
            await state.env.ledger.deleteAsset(partition, asset.key);
        }
    }

    // Fill again from scratch: the manifests of the viewer's walls (and the open wall), each followed by a fill.
    async function rebuild(state) {
        state.stopped = false;
        state.unsureRuns = 0;
        state.fresh.clear();
        const summary = await Cache().summary();
        const walls = [...new Set([state.openWallId, ...(summary ? summary.walls : [])].filter(Boolean))];
        for (const wallId of walls) {
            await Cache().manifestChanged(wallId);
        }
    }

    const refreshMarks = () => (win.bwAssetReport ? win.bwAssetReport.marks.refresh() : Promise.resolve());

    async function clear() {
        const state = await started();
        if (!state) {
            return false;
        }
        const partition = state.active;
        post({ type: 'clearing', partition });
        await stop(state);
        await wipe(state);
        post({ type: 'cleared', partition });
        await refreshMarks();
        // Not awaited: the button returns once the space is free; the refill runs in the background.
        rebuild(state).then(refreshMarks).catch(() => undefined);
        return true;
    }

    async function onRemote(message) {
        const state = await started();
        if (!state || !message || message.partition !== state.active) {
            return;
        }
        win.clearTimeout(resumeTimer);
        resumeTimer = null;
        if (message.type === 'clearing') {
            state.stopped = true;
            resumeTimer = win.setTimeout(() => { resumeTimer = null; rebuild(state).then(refreshMarks).catch(() => undefined); }, RESUME_MS);
        } else if (message.type === 'cleared') {
            state.stopped = true;
            await drained();
            await refreshMarks();
            await rebuild(state);
            await refreshMarks();
        }
    }

    function listen() {
        try {
            if (typeof win.BroadcastChannel === 'function') {
                bus = new win.BroadcastChannel(CHANNEL);
                bus.onmessage = event => { onRemote(event.data); };
                return;
            }
        } catch (e) {
            bus = null;
        }
        win.addEventListener('storage', event => {
            if (event.key === CHANNEL && event.newValue) {
                try {
                    onRemote(JSON.parse(event.newValue));
                } catch (e) {
                    /* not ours */
                }
            }
        });
    }

    return { STOP_WAIT_MS, RESUME_MS, clear, listen };
})(window);
