/*
 * bwAssetReport — what the wall-asset cache holds on this device, for Settings → "Offline storage" and the
 * offline-ready mark (design 2026-10-21 §6.8, PR 2.9). Reads the ledger of bwAssetCache's active partition.
 *
 *   report():   the section's facts: total bytes (the ledger's count, never estimate()), per wall core / gallery / 3D /
 *               other bytes with its keep-offline pin and readiness, whether storage is persisted, the 3D setting
 *               and how this device fills it, and the iOS-tab install nudge.
 *   setKeepOffline(wallId, on): the per-wall pin. Pinning asks persist(), as §6.8 says ("on Keep offline"); unpinning
 *               also clears 2.8's "Save 3D offline" (save3d), which is a pin plus 3D.
 *   clear():    "Clear offline data": stops this page's fill and deletes this partition's `bw-a1-{p}-*` caches and
 *               ledger rows. Never `blocwerk-offline` (unsent ticks), `bw-sw-state` or the shell. Wall data is always
 *               cached, so the walls download again on the next page load.
 *
 * OFFLINE READY (the mark): the wall's manifest is in the ledger with nothing pending (every hash known), it lists at
 * least one core asset, and every core key the fill plans on this device (bwAssetPlan.coreKeys: the fit rendition
 * and 640) has a ledger row. Ledger rows come only from the worker's `asset-cached` (after its put succeeded) and from
 * the start's reconcile against cache.keys(), so a row is a confirmed cache entry.
 *
 * Marks are `[data-bw-offline-ready="{wallId}"]` elements (OfflineReadyMark.razor). This module adds `data-ready` to
 * the ready ones; CSS hides the rest. Blazor never owns `data-ready`, so its diffs leave it alone, and a
 * MutationObserver marks the elements a render adds.
 */
window.bwAssetReport = (function (win) {
    'use strict';

    const REAL_LABEL = /^(anon|[uk]-[a-z2-7]{20})$/;
    const MARK_ATTR = 'data-bw-offline-ready';
    const PREFIX = 'bw-a1-';
    const RECHECK_MS = 1000;

    const Cache = () => win.bwAssetCache;
    const Plan = () => win.bwAssetPlan;

    // The ledger's count, else (an original announced without a Content-Length) the manifest's size of it.
    const bytesOf = (asset, entry) => asset.bytes || (asset.width || !entry ? 0 : entry.bytes || 0);

    function bucketOf(entry) {
        if (entry.tier === 'core') {
            return 'core';
        }
        if (entry.kind === 'gallery') {
            return 'gallery';
        }
        return entry.tier === 'view3d' ? 'view3d' : 'other';
    }

    /** Whether a wall row works offline on this device: see the header. `keys`: a Set of cached asset keys. */
    function isReady(wall, keys, width) {
        if (!wall || !wall.fetchedAt || (wall.pending || 0) > 0) {
            return false;
        }
        const core = (wall.entries || []).filter(e => e.tier === 'core');
        if (core.length === 0 || core.some(e => !e.sha)) {
            return false;
        }
        return core.every(entry => Plan().coreKeys(entry, width).every(key => keys.has(key)));
    }

    /**
     * Pure: { total, walls: [{ wallId, core, gallery, view3d, other, total, keepOffline, ready, lastOpened }] }. An asset
     * two walls list counts in both walls' sizes, and once in `total`.
     */
    function summarize(walls, assets, width) {
        const keys = new Set(assets.map(a => a.key));
        const entryOf = new Map();
        walls.forEach(w => (w.entries || []).forEach(e => { if (e.sha && !entryOf.has(e.sha)) { entryOf.set(e.sha, e); } }));
        const total = assets.reduce((sum, a) => sum + bytesOf(a, entryOf.get(a.sha)), 0);
        const rows = walls.map(wall => {
            const mine = new Map((wall.entries || []).filter(e => e.sha).map(e => [e.sha, e]));
            const row = {
                wallId: wall.wallId, core: 0, gallery: 0, view3d: 0, other: 0, total: 0, keepOffline: !!wall.keepOffline,
                saved3d: !!(wall.keepOffline && wall.save3d), ready: isReady(wall, keys, width), lastOpened: wall.lastOpened || 0
            };
            for (const asset of assets) {
                const entry = mine.get(asset.sha);
                if (entry) {
                    const bytes = bytesOf(asset, entry);
                    row[bucketOf(entry)] += bytes;
                    row.total += bytes;
                }
            }
            return row;
        });
        rows.sort((a, b) => b.lastOpened - a.lastOpened || String(a.wallId).localeCompare(String(b.wallId)));
        return { total, walls: rows };
    }

    const fitWidth = () => Plan().fitWidth(win.innerWidth, win.devicePixelRatio);

    function isIosTab() {
        const nav = win.navigator;
        const ua = nav.userAgent || '';
        const ios = /iPhone|iPad|iPod/.test(ua) || (/Macintosh/.test(ua) && nav.maxTouchPoints > 1);
        let standalone = nav.standalone === true;
        try {
            standalone = standalone || (!!win.matchMedia && win.matchMedia('(display-mode: standalone)').matches);
        } catch (e) {
            /* no matchMedia: not standalone */
        }
        return ios && !standalone;
    }

    async function persisted() {
        try {
            const storage = win.navigator.storage;
            return !!(storage && storage.persisted && await storage.persisted());
        } catch (e) {
            return false;
        }
    }

    // The started cache's state under a real label, or null (no cache on this page: none, unknown, offline, booting failed).
    async function active() {
        const cache = Cache();
        if (!cache) {
            return null;
        }
        await (cache.ready || Promise.resolve()).catch(() => undefined);
        const state = cache.state();
        return state.env && typeof state.active === 'string' && REAL_LABEL.test(state.active) ? state : null;
    }

    async function snapshot(state) {
        return { walls: await state.env.ledger.wallsOf(state.active), assets: await state.env.ledger.assetsOf(state.active) };
    }

    async function report() {
        const cache = Cache();
        const state = await active();
        const install = win.bwInstall && typeof win.bwInstall.instructions === 'function' ? win.bwInstall.instructions() : null;
        const facts = {
            caching: !!state, kiosk: !!state && state.active.startsWith('k-'), persisted: await persisted(),
            keep3d: cache ? cache.keep3d() : true, mobile: Plan() ? Plan().isMobile(win.navigator) : false,
            background3d: cache ? cache.fills3dInBackground() : false, iosTab: isIosTab(), installHint: isIosTab() ? install : null,
            paused: !!(state && state.runner && state.runner.streaming), total: 0, walls: []
        };
        if (!state) {
            return facts;
        }
        const { walls, assets } = await snapshot(state);
        return Object.assign(facts, summarize(walls, assets, fitWidth()));
    }

    async function setKeepOffline(wallId, on) {
        const state = await active();
        const id = String(wallId).toLowerCase();
        if (!state || !(await state.env.ledger.getWall(state.active, id))) {
            return false;
        }
        // Through the ledger's one merge (updateWall), so a manifest round in flight cannot drop the pin. Unpinning also
        // clears 2.8's save3d: a saved-3D wall is keepOffline + save3d, and a pin coming back must not revive it.
        await state.env.ledger.updateWall(state.active, id, current => (current
            ? Object.assign(current, on ? { keepOffline: true } : { keepOffline: false, save3d: false })
            : undefined));
        if (on && win.navigator.storage && win.navigator.storage.persist) {
            await win.navigator.storage.persist().catch(() => false);
        }
        return true;
    }

    async function clear() {
        const state = await active();
        if (!state) {
            return false;
        }
        const partition = state.active;
        state.stopped = true;
        // The running fill ends at its next task; its last announcements are in by then, so nothing lands after the wipe.
        await Cache().filled().catch(() => undefined);
        for (const suffix of ['img', '3d']) {
            try {
                await state.env.caches.delete(`${PREFIX}${partition}-${suffix}`);
            } catch (e) {
                /* storage unavailable: the reconcile on the next start catches up */
            }
        }
        const { walls, assets } = await snapshot(state);
        for (const wall of walls) {
            await state.env.ledger.deleteWall(partition, wall.wallId);
        }
        for (const asset of assets) {
            await state.env.ledger.deleteAsset(partition, asset.key);
        }
        await marks.refresh();
        return true;
    }

    const marks = createMarks();

    function createMarks() {
        let ready = null;
        let timer = null;

        async function readySet() {
            const state = await active();
            if (!state) {
                return new Set();
            }
            const { walls, assets } = await snapshot(state);
            const keys = new Set(assets.map(a => a.key));
            return new Set(walls.filter(w => isReady(w, keys, fitWidth())).map(w => w.wallId));
        }

        async function apply() {
            const elements = win.document.querySelectorAll ? [...win.document.querySelectorAll(`[${MARK_ATTR}]`)] : [];
            if (elements.length === 0) {
                return;
            }
            ready = ready || readySet().catch(() => new Set());
            const set = await ready;
            for (const el of elements) {
                if (set.has(String(el.getAttribute(MARK_ATTR)).toLowerCase())) {
                    el.setAttribute('data-ready', '');
                } else {
                    el.removeAttribute('data-ready');
                }
            }
        }

        // Many announcements come in a burst while a fill runs: one recheck per second at most.
        function invalidate() {
            if (!timer) {
                timer = win.setTimeout(() => { timer = null; ready = null; apply(); }, RECHECK_MS);
            }
        }

        // Now: after a clear, and once the start's fill has run.
        function refresh() {
            ready = null;
            return apply();
        }

        return { apply, invalidate, refresh, readySet };
    }

    function observe() {
        const sw = win.navigator.serviceWorker;
        if (sw && sw.addEventListener) {
            sw.addEventListener('message', event => { if (event.data && event.data.type === 'asset-cached') { marks.invalidate(); } });
        }
        if (typeof win.MutationObserver === 'function' && win.document.body) {
            let queued = false;
            new win.MutationObserver(() => {
                if (!queued) {
                    queued = true;
                    win.setTimeout(() => { queued = false; marks.apply(); }, 100);
                }
            }).observe(win.document.body, { childList: true, subtree: true, attributes: true, attributeFilter: [MARK_ATTR] });
        }
        // A wall opened by enhanced navigation refreshes its manifest; read the ledger again a little later.
        if (win.Blazor && win.Blazor.addEventListener) {
            win.Blazor.addEventListener('enhancedload', () => marks.invalidate());
        }
        const cache = Cache();
        const started = cache && (cache.ready || cache.boot());
        Promise.resolve(started).then(() => marks.refresh()).then(() => cache.filled()).then(() => marks.refresh()).catch(() => undefined);
    }

    return { isReady, summarize, report, setKeepOffline, clear, observe, marks };
})(window);

if (!window.__bwAssetCacheManual) {
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => window.bwAssetReport.observe(), { once: true });
    } else {
        window.bwAssetReport.observe();
    }
}
