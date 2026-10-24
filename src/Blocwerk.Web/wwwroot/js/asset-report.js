/*
 * bwAssetReport — what the wall-asset cache holds on this device, for Settings → "Offline storage" and the
 * "Offline mode available" bubble (design 2026-10-21 §6.8, PR 2.9). Reads the ledger of bwAssetCache's active partition.
 *
 *   report():   the section's facts: total bytes (the ledger's count, never estimate()), per wall core / gallery / 3D /
 *               other bytes with its keep-offline pin and readiness, whether storage is persisted, the 3D setting
 *               and how this device fills it, and the iOS-tab install nudge.
 *   setKeepOffline(wallId, on): the per-wall pin. Pinning asks persist(), as §6.8 says ("on Keep offline"); unpinning
 *               also clears 2.8's "Save 3D offline" (save3d), which is a pin plus 3D.
 *   clear():    "Clear offline data" (asset-clear.js): frees this partition's caches and ledger rows now, then
 *               rebuilds them in the background, and tells the other tabs.
 *
 * OFFLINE READY: the wall's manifest is in the ledger with nothing pending (every hash known), it lists at
 * least one core asset, and every core key the fill plans on this device (bwAssetPlan.coreKeys: the fit rendition
 * and 640) has a ledger row. Ledger rows come only from the worker's `asset-cached` (after its put succeeded) and from
 * the start's reconcile against cache.keys(), so a row is a confirmed cache entry.
 *
 * THE BUBBLE (readiness): when a wall becomes offline ready for the first time on this device, bwStatus shows
 * "Offline mode available". The walls already told are kept per partition in localStorage (READY_KEY) with the time
 * of the last bubble. A wall stays "told" while it is not ready for a while (a new panel photo pending, a viewport
 * that wants a rendition not cached yet), and leaves only when it leaves the ledger (the viewer left it, or "Clear
 * offline data"). Told at most once per READY_QUIET_MS, whatever the set holds, so walls that finish one after the
 * other (across a reload, too) and a clear followed by its refill say so once. The bubble's readiness does not
 * depend on the viewport: a core photo counts at its 640 rendition (READY_WIDTH), which the fill plans on every
 * device; Settings' "Offline ready" keeps the device's own rule (isReady with the fit width). Not for a kiosk
 * partition: a kiosk keeps nothing (no-store).
 */
window.bwAssetReport = (function (win) {
    'use strict';

    const REAL_LABEL = /^(anon|[uk]-[a-z2-7]{20})$/;
    const READY_KEY = 'blocwerk-offline-ready';
    const RECHECK_MS = 2000;
    const READY_QUIET_MS = 10 * 60 * 1000;
    const READY_WIDTH = 640;

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
        return core.every(entry => {
            const wanted = Plan().coreKeys(entry, width);
            // A photo offered at neither width: any rendition of it on the device will do.
            return wanted.length > 0 ? wanted.every(key => keys.has(key)) : [...keys].some(key => key.startsWith(entry.sha));
        });
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
        let saved3d = false;
        await state.env.ledger.updateWall(state.active, id, current => {
            saved3d = !!(current && current.save3d);
            return current ? Object.assign(current, on ? { keepOffline: true } : { keepOffline: false, save3d: false }) : undefined;
        });
        if (on && win.navigator.storage && win.navigator.storage.persist) {
            await win.navigator.storage.persist().catch(() => false);
        }
        if (!on && saved3d) {
            // As bwAsset3d.unsave: the budget and the opt-out apply to the wall's 3D again.
            await Cache().evict();
        }
        return true;
    }

    const clear = () => (win.bwAssetClear ? win.bwAssetClear.clear() : Promise.resolve(false));

    const readiness = createReadiness();

    function createReadiness() {
        let timer = null;
        let running = Promise.resolve();

        async function readySet() {
            const state = await active();
            if (!state) {
                return { partition: null, known: new Set(), ready: new Set() };
            }
            const { walls, assets } = await snapshot(state);
            const keys = new Set(assets.map(a => a.key));
            return {
                partition: state.active, known: new Set(walls.map(w => w.wallId)),
                ready: new Set(walls.filter(w => isReady(w, keys, READY_WIDTH)).map(w => w.wallId))
            };
        }

        // { walls: Set, toldAt }, or null when localStorage cannot be read.
        function stored(partition) {
            try {
                const value = JSON.parse(win.localStorage.getItem(`${READY_KEY}:${partition}`) || '{}');
                return { walls: new Set(value.walls || []), toldAt: value.toldAt || 0 };
            } catch (e) {
                return null;
            }
        }

        function store(partition, ready, toldAt) {
            try {
                win.localStorage.setItem(`${READY_KEY}:${partition}`, JSON.stringify({ walls: [...ready].sort(), toldAt }));
            } catch (e) {
                /* no storage: the bubble may repeat on the next load, nothing worse */
            }
        }

        // One check at a time, so two cannot both find the same wall new.
        function check() {
            running = running.then(async () => {
                const { partition, known, ready } = await readySet();
                if (!partition || partition.startsWith('k-')) {
                    return;
                }
                const before = stored(partition);
                if (!before) {
                    return;
                }
                const fresh = [...ready].filter(id => !before.walls.has(id));
                const gone = [...before.walls].filter(id => !known.has(id));
                const tell = fresh.length > 0 && Date.now() - before.toldAt >= READY_QUIET_MS;
                if (fresh.length > 0 || gone.length > 0) {
                    const walls = new Set([...before.walls, ...fresh].filter(id => known.has(id)));
                    store(partition, walls, tell ? Date.now() : before.toldAt);
                }
                if (tell && win.bwStatus) {
                    win.bwStatus.show('offline-ready');
                }
            }).catch(() => undefined);
            return running;
        }

        // Many announcements come in a burst while a fill runs: one recheck per RECHECK_MS at most, once that fill is
        // done, so a fill that makes several walls ready says so once.
        function invalidate() {
            if (!timer) {
                timer = win.setTimeout(() => {
                    timer = null;
                    Promise.resolve(Cache() && Cache().filled()).catch(() => undefined).then(check);
                }, RECHECK_MS);
            }
        }

        return { check, invalidate, readySet };
    }

    function observe() {
        const sw = win.navigator.serviceWorker;
        if (sw && sw.addEventListener) {
            sw.addEventListener('message', event => { if (event.data && event.data.type === 'asset-cached') { readiness.invalidate(); } });
        }
        // A wall opened by enhanced navigation refreshes its manifest; read the ledger again a little later.
        if (win.Blazor && win.Blazor.addEventListener) {
            win.Blazor.addEventListener('enhancedload', () => readiness.invalidate());
        }
        if (win.bwAssetClear) {
            win.bwAssetClear.listen();
        }
        const cache = Cache();
        const started = cache && (cache.ready || cache.boot());
        Promise.resolve(started).then(() => cache.filled()).then(() => readiness.check()).catch(() => undefined);
    }

    return { isReady, summarize, report, setKeepOffline, clear, observe, readiness };
})(window);

if (!window.__bwAssetCacheManual) {
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => window.bwAssetReport.observe(), { once: true });
    } else {
        window.bwAssetReport.observe();
    }
}
