/*
 * bwAssetBudget — how much the wall-asset cache may hold on this device, and what goes first when it holds more
 * (design 2026-10-21 §6.8, with the 2026-10-21 decisions).
 *
 * BUDGETS ORDER, THEY NEVER EXCLUDE. Every wall the viewer belongs to is cached: its `core` tier is always filled and
 * never chosen as a victim. A budget only decides how far the background fill reaches past `core` (the 3D tier) and
 * which assets are evicted first when the ledger's byte count is over it. If the browser itself refuses (quota, a full
 * device), the fill falls back to streaming (asset-fill.js); this file never makes that call.
 *
 * Usage is the ledger's own byte count. navigator.storage.estimate() is read for its quota only, and only to shrink a
 * budget (budget = min(quota × 0.5, cap)): its usage figure is bogus on iOS.
 *
 * Deviation from the §6.8 table: the Safari tab and a Firefox without persistence get a 250 MB 3D budget instead of 0,
 * because 3D is now cached by default (opt-out). An incognito-sized quota keeps 0.
 */
window.bwAssetBudget = (function () {
    'use strict';

    const MB = 1024 * 1024;
    // [images, 3d] caps per context.
    const CAPS = {
        chromium: [1024 * MB, 2048 * MB],
        firefox: [400 * MB, 250 * MB],
        firefoxPersisted: [400 * MB, 1024 * MB],
        appleApp: [1024 * MB, 1024 * MB],
        safariTab: [250 * MB, 250 * MB],
        constrained: [100 * MB, 0]
    };
    // A quota this small is a private window or a crippled profile.
    const CONSTRAINED_QUOTA = 512 * MB;
    const TIER_RANK = { core: 0, view3d: 1, lazy: 2 };

    function isStandalone(win) {
        try {
            if (win.matchMedia && win.matchMedia('(display-mode: standalone)').matches) {
                return true;
            }
        } catch (e) {
            /* fall through */
        }
        return win.navigator.standalone === true;
    }

    function engineOf(nav) {
        const ua = nav.userAgent || '';
        if (/Firefox\//.test(ua)) {
            return 'firefox';
        }
        // Every iOS browser is WebKit; desktop Safari says Safari without Chrome/Chromium/Edg.
        if (/iPhone|iPad|iPod/.test(ua) || (/Macintosh/.test(ua) && nav.maxTouchPoints > 1)) {
            return 'webkit';
        }
        if (/Safari\//.test(ua) && !/Chrome\/|Chromium\/|Edg\//.test(ua)) {
            return 'webkit';
        }
        return 'chromium';
    }

    /** The budget context: { name, engine, standalone, persisted, quota }. Never throws. */
    async function detect(win) {
        const nav = win.navigator;
        const engine = engineOf(nav);
        const standalone = isStandalone(win);
        let quota = null;
        let persisted = false;
        try {
            const est = nav.storage && nav.storage.estimate ? await nav.storage.estimate() : null;
            quota = est && Number.isFinite(est.quota) ? est.quota : null;
            persisted = nav.storage && nav.storage.persisted ? await nav.storage.persisted() : false;
        } catch (e) {
            quota = null;
        }
        let name;
        if (quota === null || quota < CONSTRAINED_QUOTA) {
            name = 'constrained';
        } else if (engine === 'webkit') {
            name = standalone ? 'appleApp' : 'safariTab';
        } else if (engine === 'firefox') {
            name = persisted ? 'firefoxPersisted' : 'firefox';
        } else {
            name = 'chromium';
        }
        return { name, engine, standalone, persisted, quota };
    }

    /** { images, view3d } bytes for a context from detect(). */
    function budgetFor(context) {
        const [images, view3d] = CAPS[context.name] || CAPS.constrained;
        const half = context.quota === null ? Infinity : context.quota * 0.5;
        return { images: Math.min(images, half), view3d: Math.min(view3d, half) };
    }

    // persist() is asked silently where it never prompts: Chromium and the installed Apple app. Firefox prompts, so it
    // waits for an explicit "keep offline" (2.9).
    function shouldRequestPersist(context) {
        return !context.persisted && (context.engine === 'chromium' || context.name === 'appleApp');
    }

    /**
     * Per asset key, the tier it plays for the walls that list it (the lowest rank wins: a sha that is one wall's core
     * is never evicted as another's lazy) and those walls. Assets no manifest lists are `orphan`.
     */
    function classify(walls, assets) {
        const bySha = new Map();
        for (const wall of walls) {
            for (const entry of wall.entries || []) {
                const known = bySha.get(entry.sha) || { tier: entry.tier, kind: entry.kind, splats: entry.splats, bytes: entry.bytes, walls: [] };
                if ((TIER_RANK[entry.tier] ?? 9) < (TIER_RANK[known.tier] ?? 9)) {
                    known.tier = entry.tier;
                }
                known.walls.push(wall);
                bySha.set(entry.sha, known);
            }
        }
        return assets.map(asset => {
            const known = bySha.get(asset.sha);
            return known
                ? Object.assign({ asset }, known)
                : { asset, tier: 'orphan', kind: null, walls: [] };
        });
    }

    const recencyOf = item => Math.max(0, ...item.walls.map(w => w.lastOpened || 0));
    const isPinned = (item, openWallId) => item.walls.some(w => w.wallId === openWallId || w.keepOffline);
    // The ledger's count, else (an original announced without a Content-Length) the manifest's size of it.
    const bytesOf = item => item.asset.bytes || (item.asset.width ? 0 : item.bytes || 0);

    /**
     * Splat levels past one per wall: the smallest (what every visit starts on) and the largest cached (what this
     * device stepped up to) stay; every level between them goes. That is the "one LOD" rule plus its starter.
     */
    function supersededSplats(items) {
        const byWall = new Map();
        for (const item of items) {
            if (item.kind !== 'splat') {
                continue;
            }
            const wallId = item.walls[0] && item.walls[0].wallId;
            byWall.set(wallId, (byWall.get(wallId) || []).concat([item]));
        }
        const out = [];
        for (const levels of byWall.values()) {
            levels.sort((a, b) => (a.splats || Infinity) - (b.splats || Infinity));
            out.push(...levels.slice(1, -1));
        }
        return out;
    }

    // Least recently opened wall first, then oldest cached; the open wall and keep-offline walls never.
    function victimsOver(items, budget, openWallId) {
        let used = items.reduce((sum, item) => sum + bytesOf(item), 0);
        const out = [];
        const candidates = items
            .filter(item => !isPinned(item, openWallId))
            .sort((a, b) => recencyOf(a) - recencyOf(b) || (a.asset.cachedAt || 0) - (b.asset.cachedAt || 0));
        for (const item of candidates) {
            if (used <= budget) {
                break;
            }
            out.push(item);
            used -= bytesOf(item);
        }
        return out;
    }

    /**
     * The asset keys to evict, in order. `state`: { walls, assets, budget: {images, view3d}, openWallId, keep3d, orphans }.
     *   1. orphans, when `orphans` says every wall's manifest is current: no manifest of this partition lists them any
     *      more (a wall update replaced them). Offline, or with a ledger that lost its walls, nothing is an orphan;
     *   2. every 3D asset when 3D is opted out, else the superseded splat levels;
     *   3. 3D over the 3D budget;
     *   4. lazy over the image budget (core counts toward the budget but is never a victim).
     */
    function planEviction(state) {
        const items = classify(state.walls, state.assets);
        const chosen = new Set();
        const take = list => list.forEach(item => chosen.add(item.asset.key));
        if (state.orphans) {
            take(items.filter(item => item.tier === 'orphan'));
        }
        const view3d = items.filter(item => item.tier === 'view3d');
        if (!state.keep3d) {
            take(view3d);
        } else {
            take(supersededSplats(view3d));
            const left = view3d.filter(item => !chosen.has(item.asset.key));
            take(victimsOver(left, state.budget.view3d, state.openWallId));
        }
        // Core takes its share of the image budget first; lazy gets what is left.
        const coreBytes = items.filter(item => item.tier === 'core').reduce((sum, item) => sum + bytesOf(item), 0);
        const lazy = items.filter(item => item.tier === 'lazy');
        take(victimsOver(lazy, Math.max(0, state.budget.images - coreBytes), state.openWallId));
        return [...chosen];
    }

    /** One wall's evictable keys (3D, then lazy), least recently opened non-open wall: the QuotaExceeded retry. */
    function oneWallToFree(state) {
        const items = classify(state.walls, state.assets)
            .filter(item => (item.tier === 'view3d' || item.tier === 'lazy') && !isPinned(item, state.openWallId));
        if (items.length === 0) {
            return [];
        }
        const oldest = Math.min(...items.map(recencyOf));
        return items.filter(item => recencyOf(item) === oldest).map(item => item.asset.key);
    }

    return { CAPS, TIER_RANK, detect, budgetFor, shouldRequestPersist, classify, planEviction, oneWallToFree };
})();
