/*
 * bwAssetClear — Settings → "Clear offline data" (design 2026-10-21 §6.8, PR 2.9): free the space now, then rebuild
 * what the device needs. Wall data is always cached, so a clear is never a way to stop caching.
 *
 *   clear():  stops this page's fill (`state.stopped`, which the runner reads as "hidden" at its next task) and waits for
 *             the running one, at most STOP_WAIT_MS (a large splat download must not hold the button). Then it deletes
 *             this partition's `bw-a1-{p}-img` / `-3d` caches and ledger rows (walls, assets, mine), and a locked partition
 *             with its lock (PR 3.5): never `blocwerk-offline` (unsent ticks),
 *             `bw-sw-state` or the shell, and `meta.activePartition` stays. Then it rebuilds: `stopped` and the fresh
 *             set are reset and every wall of the viewer (and the open wall) is refetched and filled, core first.
 *   Other tabs of the same partition hear `clearing` (they stop their fill) and `cleared` (they rebuild) over a
 *   BroadcastChannel, and `locked` (PR 3.5: the 401 lock locked or wiped it; they stop for good) over the same
 *   channel, or a localStorage event where there is none. A tab that never hears `cleared` (the clearing tab
 *   closed) rebuilds after RESUME_MS anyway.
 * ONE TRANSPORT per page (channel()): every post goes over the page's one BroadcastChannel, whether or not it listens,
 *   so a page that only announces (the offline client) still reaches the Server tabs; a channel never hears its own
 *   posts, so subscribe() (the client's store) hears only the other tabs. localStorage only where there is no channel.
 */
window.bwAssetClear = (function (win) {
    'use strict';

    const CHANNEL = 'bw-asset-clear';
    const STOP_WAIT_MS = 10000;
    const RESUME_MS = 30000;
    const REAL_LABEL = /^(anon|[uk]-[a-z2-7]{20})$/;

    const Cache = () => win.bwAssetCache;
    const wait = ms => new Promise(resolve => win.setTimeout(resolve, ms));
    const handlers = new Set();
    const remote = message => { onRemote(message); };
    let bus;
    let storageHooked = false;
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

    // The page's one channel, opened on first use; null without BroadcastChannel (localStorage stands in).
    function channel() {
        if (bus === undefined) {
            try {
                bus = typeof win.BroadcastChannel === 'function' ? new win.BroadcastChannel(CHANNEL) : null;
            } catch (e) {
                bus = null;
            }
            if (bus) {
                bus.onmessage = event => deliver(event.data);
            }
        }
        return bus;
    }

    function deliver(message) {
        handlers.forEach(fn => {
            try {
                fn(message);
            } catch (e) {
                /* one subscriber's failure is not the others' */
            }
        });
    }

    function post(message) {
        try {
            const open = channel();
            if (open) {
                open.postMessage(message);
            } else if (win.localStorage) {
                win.localStorage.setItem(CHANNEL, JSON.stringify(Object.assign({ at: Date.now(), nonce: Math.random() }, message)));
                win.localStorage.removeItem(CHANNEL);
            }
        } catch (e) {
            /* no channel: other tabs refill on their next load */
        }
    }

    /** Calls `fn(message)` for every message another tab posts on the channel (or, without one, through localStorage). */
    function subscribe(fn) {
        handlers.add(fn);
        if (channel() || storageHooked) {
            return;
        }
        storageHooked = true;
        win.addEventListener('storage', event => {
            if (event.key === CHANNEL && event.newValue) {
                try {
                    deliver(JSON.parse(event.newValue));
                } catch (e) {
                    /* not ours */
                }
            }
        });
    }

    // The running fill ends at its next task once `stopped` is set; a stuck download is not waited for past STOP_WAIT_MS.
    const drained = () => Promise.race([Cache().filled().catch(() => undefined), wait(STOP_WAIT_MS)]);

    function stop(state) {
        state.stopped = true;
        return drained();
    }

    // This partition's caches and rows (walls, assets, mine), and a locked one with its lock (asset-lock.js).
    async function wipe(state) {
        await win.bwAssetWipe.dropPartition(state.env, state.active);
        if (win.bwAssetLock && typeof win.bwAssetLock.clear === 'function') {
            await win.bwAssetLock.clear();
        }
    }

    // Fill again from scratch: the manifests of the viewer's walls (and the open wall), each followed by a fill.
    async function rebuild(state) {
        state.stopped = false;
        state.unsureRuns = 0;
        state.fresh.clear();
        const summary = await Cache().summary();
        const walls = [...new Set([state.openWallId, ...(summary ? summary.walls : [])].filter(Boolean))];
        // All at once: manifestChanged resolves with the whole fill, so awaiting each in turn would hold the next wall's
        // core behind this wall's 3D. The fills coalesce (scheduleFill's refill), and the plan puts every wall's core first.
        await Promise.all(walls.map(wallId => Cache().manifestChanged(wallId)));
    }

    const refreshMarks = () => (win.bwAssetReport ? win.bwAssetReport.readiness.check() : Promise.resolve());

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
        if (message.type === 'locked') {
            // Another tab locked or wiped this partition (asset-lock.js): this tab stops and shows none of it either.
            win.bwAssetLock.stopTab(state);
            await refreshMarks();
            return;
        }
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

    // The Server page's own reaction to the other tabs (asset-report.js calls it once; a second call adds nothing).
    function listen() {
        subscribe(remote);
    }

    // `announce`: asset-lock.js tells the other tabs of a partition it locked or wiped ({ type: 'locked', partition }),
    // and the offline client its own Clear (`clearing` / `cleared`).
    return { STOP_WAIT_MS, RESUME_MS, clear, listen, subscribe, announce: post };
})(window);
