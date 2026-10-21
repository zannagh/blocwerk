/*
 * bwAssetCache — the page side of the on-device wall-asset cache (design 2026-10-21 §6.6, §6.8; PR 2.7).
 *
 * The service worker (sw-asset-cache.js) stores and serves the hashed `/a/` assets on its own; this module decides
 * what the device should hold and when to let go of it:
 *   start: the page's partition decision and wipes (asset-session.js) → ledger reconcile against the caches → the
 *          viewer's walls from /api/me/cache-summary → each wall's manifest, 3 at a time (asset-manifest.js) →
 *          eviction (asset-budget.js) → the background fill (asset-plan.js, asset-fill.js).
 *   on a wall page: that wall's manifest is fetched (with the page's share token), it goes first in the fill, and
 *          leaving it runs eviction again.
 *   on `asset-cached` / `asset-refused` from the worker: the ledger records the asset / the fill learns of a refusal.
 *   a fill that stopped resumes: on visibilitychange (hidden), on `online` (a network failure), on a timer (another
 *          tab held the lock, or the worker was too slow to confirm).
 *
 * WALL DATA IS ALWAYS CACHED for the viewer's walls (no setting turns it off). 3D is cached unless the viewer opted
 * out (bwPrefs.getCache3d(), default on), and background-filled only on an unmetered connection. An old worker that
 * does not answer `sw-info` (a missing reply is normal) gets no background fill; the wipe rules still run. Every
 * failure here leaves the page streaming, never broken.
 */
window.bwAssetCache = (function (win) {
    'use strict';

    const PROTO = 1;
    const WALL_PATH = /^\/walls\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})(?:\/|$)/i;
    const REAL_LABEL = /^(anon|[uk]-[a-z2-7]{20})$/;
    const SEEN_MAX = 512;
    const MANIFEST_PARALLEL = 3;

    const Wipe = () => win.bwAssetWipe;
    const Budget = () => win.bwAssetBudget;
    const Plan = () => win.bwAssetPlan;
    const Fill = () => win.bwAssetFill;
    const Manifest = () => win.bwAssetManifest;
    const Store = () => win.bwAssetStore;

    const state = {
        env: null, active: null, capable: false, summary: null, budget: null, context: null,
        openWallId: null, tokens: new Map(), waiters: new Map(), seen: new Map(), runner: null, filling: null, refill: false,
        pendingTimers: new Map(), retry: null, fresh: new Set(), started: null, summaryLoaded: null
    };
    let summaryResolve = () => undefined;
    state.summaryLoaded = new Promise(resolve => { summaryResolve = resolve; });

    const now = () => Date.now();
    const keep3d = () => !(win.bwPrefs && typeof win.bwPrefs.getCache3d === 'function' && win.bwPrefs.getCache3d() === false);
    const caching = () => typeof state.active === 'string' && REAL_LABEL.test(state.active);
    const snapshot = async () => ({ walls: await state.env.ledger.wallsOf(state.active), assets: await state.env.ledger.assetsOf(state.active) });

    function wallOfPage() {
        const match = WALL_PATH.exec(win.location.pathname || '');
        return match ? match[1].toLowerCase() : null;
    }

    function onWorkerMessage(event) {
        const data = event.data;
        const known = data && (data.type === 'asset-cached' || data.type === 'asset-refused');
        if (!known || data.proto !== PROTO || typeof data.sha !== 'string' || !caching()) {
            return;
        }
        const key = Plan().keyOf(data.sha, data.width);
        const outcome = data.type === 'asset-cached' ? 'stored' : 'refused';
        state.seen.set(key, outcome);
        if (state.seen.size > SEEN_MAX) {
            state.seen.delete(state.seen.keys().next().value);
        }
        if (outcome === 'stored') {
            state.env.ledger.putAsset({ partition: state.active, key, sha: data.sha, width: data.width || null, cache: null, bytes: data.bytes || 0, cachedAt: now(), verifiedAt: now() });
        }
        const waiter = state.waiters.get(key);
        if (waiter) {
            state.waiters.delete(key);
            waiter(outcome);
        }
    }

    const dropWall = wallId => Wipe().dropWall(state.env, state.active, wallId);

    async function refreshWall(wallId, opened) {
        const member = !!(state.summary && state.summary.walls.includes(wallId));
        const token = state.tokens.get(wallId) || null;
        const result = await Manifest().refresh(state.env, state.active, wallId, { token, member, opened, dropWall });
        if (result.outcome === 'updated' || result.outcome === 'unchanged') {
            state.fresh.add(wallId);
        }
        win.clearTimeout(state.pendingTimers.get(wallId));
        if (result.pending > 0 && result.outcome !== 'dropped') {
            state.pendingTimers.set(wallId, win.setTimeout(() => {
                refreshWall(wallId, false).then(() => scheduleFill());
            }, Manifest().PENDING_RETRY_MS));
        }
        return result;
    }

    async function refreshAll(wallIds) {
        const queue = wallIds.slice();
        const worker = async () => {
            while (queue.length > 0) {
                const wallId = queue.shift();
                await refreshWall(wallId, wallId === state.openWallId);
            }
        };
        await Promise.all(Array.from({ length: Math.min(MANIFEST_PARALLEL, queue.length) }, worker));
    }

    async function evict(snap) {
        const { walls, assets } = snap || await snapshot();
        // An orphan is only an orphan when every manifest it could be in was read on this page.
        const orphans = !!state.summary && walls.every(w => state.fresh.has(w.wallId));
        const keys = Budget().planEviction({ walls, assets, budget: state.budget, openWallId: state.openWallId, keep3d: keep3d(), orphans, now: now() });
        await Wipe().deleteKeys(state.env, state.active, keys);
    }

    async function freeOneWall() {
        const { walls, assets } = await snapshot();
        const keys = Budget().oneWallToFree({ walls, assets, openWallId: state.openWallId });
        await Wipe().deleteKeys(state.env, state.active, keys);
        return keys.length > 0;
    }

    async function fillOnce() {
        // A worker serving another identity's label would store nothing for this page: wait, backing off.
        const served = await Store().workerPartition(win);
        if (served && REAL_LABEL.test(served) && served !== state.active) {
            return 'mismatch';
        }
        const { walls, assets } = await snapshot();
        const classified = Budget().classify(walls, assets);
        const bytesIn = test => classified.filter(test).reduce((sum, item) => sum + (item.asset.bytes || 0), 0);
        const tasks = Plan().planFill({
            walls, cachedKeys: new Set(assets.map(a => a.key)), openWallId: state.openWallId, keep3d: keep3d(),
            metered: Plan().isMetered(win.navigator), mobile: Plan().isMobile(win.navigator), budget: state.budget,
            usedImages: bytesIn(item => item.tier === 'core' || item.tier === 'lazy'), used3d: bytesIn(item => item.tier === 'view3d'),
            width: Plan().fitWidth(win.innerWidth, win.devicePixelRatio), tokens: state.tokens
        });
        if (tasks.length === 0) {
            return 'done';
        }
        const result = await state.runner.run(tasks);
        await evict();
        return result;
    }

    // One fill at a time; a request during a run reruns once it ends (the plan is rebuilt from the ledger).
    function scheduleFill() {
        if (!state.capable || !caching() || !state.runner || state.runner.streaming || state.stopped) {
            return Promise.resolve();
        }
        if (state.filling) {
            state.refill = true;
            return state.filling;
        }
        state.filling = (async () => {
            let result = 'done';
            do {
                state.refill = false;
                try {
                    result = await fillOnce();
                } catch (e) {
                    result = 'offline';
                }
            } while (state.refill && result === 'done');
            state.filling = null;
            Fill().resumeAfter(win, result, scheduleFill, state);
        })();
        return state.filling;
    }

    function createRunner() {
        return Fill().createRunner({
            fetch: (url, init) => win.fetch(url, init),
            verify: task => Store().verify(win, state.seen, state.waiters, state.active, task),
            partition: () => state.active, hidden: () => win.document.visibilityState === 'hidden',
            online: () => win.navigator.onLine !== false, forget: task => state.seen.delete(task.key),
            concurrency: () => Fill().concurrencyOf(win.navigator), freeOneWall, lock: fn => Fill().lock(win.navigator, fn)
        });
    }

    // A share link's token, held in memory only (never in the ledger), for that wall's manifest and fill URLs.
    function rememberToken(wallId) {
        const token = Wipe().tokenOf(win.location);
        if (wallId && token) {
            state.tokens.set(wallId, token);
        }
    }

    // A wall page opened, left, or swapped for another.
    async function onLocation() {
        if (!caching()) {
            return;
        }
        const wallId = wallOfPage();
        rememberToken(wallId);
        if (wallId === state.openWallId) {
            return;
        }
        state.openWallId = wallId;
        if (wallId) {
            await refreshWall(wallId, true);
        }
        await evict();
        await scheduleFill();
    }

    // Rule 5 on start: every wall not in the viewer's list goes, a share-opened one included (its token is not kept
    // past the page that had it, so it could not be refreshed anyway), except the wall open right now.
    async function dropStaleWalls(walls) {
        const kept = [];
        for (const wall of walls) {
            if (state.summary && !state.summary.walls.includes(wall.wallId) && wall.wallId !== state.openWallId) {
                await dropWall(wall.wallId);
            } else {
                kept.push(wall.wallId);
            }
        }
        return kept;
    }

    async function startCaching() {
        state.context = await Budget().detect(win);
        state.budget = Budget().budgetFor(state.context);
        if (Budget().shouldRequestPersist(state.context) && win.navigator.storage && win.navigator.storage.persist) {
            win.navigator.storage.persist().catch(() => undefined);
        }
        await Store().reconcile(win, state.env.ledger, state.active, now());
        state.summary = await Store().loadSummary(win);
        summaryResolve(state.summary);
        state.openWallId = wallOfPage();
        rememberToken(state.openWallId);
        const known = await dropStaleWalls(await state.env.ledger.wallsOf(state.active));
        await refreshAll([...new Set([state.openWallId, ...(state.summary ? state.summary.walls : []), ...known].filter(Boolean))]);
        await evict();
        state.capable = await state.probe;
        state.runner = createRunner();
        // Not awaited: `ready` means "decided and reconciled", not "filled".
        scheduleFill();
    }

    async function start() {
        const ledger = await win.bwAssetLedger.open();
        state.env = { caches: win.caches, ledger: win.bwAssetSession.guard(ledger), origin: win.location.origin, fetch: (u, i) => win.fetch(u, i), now, partition: () => state.active };
        if (win.navigator.serviceWorker && win.navigator.serviceWorker.addEventListener) {
            win.navigator.serviceWorker.addEventListener('message', onWorkerMessage);
        }
        state.probe = Store().probeWorker(win);
        state.active = await win.bwAssetSession.begin(win, ledger);
        if (caching() && win.caches) {
            await startCaching();
        }
    }

    function boot() {
        if (!state.started) {
            state.started = start().catch(() => undefined).then(() => summaryResolve(state.summary));
            Wipe().installLogout(win, () => win.bwAssetLedger.open());
            const recheck = () => { state.started.then(onLocation).catch(() => undefined); };
            win.addEventListener('popstate', recheck);
            if (win.Blazor && win.Blazor.addEventListener) {
                win.Blazor.addEventListener('enhancedload', recheck);
            }
            win.setInterval(recheck, 2000);
        }
        return state.started;
    }

    const whenStarted = fn => (state.started || Promise.resolve()).then(() => (caching() ? fn() : undefined));

    return {
        boot,
        get ready() { return state.started; },
        state: () => state,
        // Rule 5's hook for a pushed access.revoked; also the manifest's 404.
        dropWall: wallId => whenStarted(() => dropWall(String(wallId).toLowerCase())),
        // A `wall.manifest` / `wall.resync` for a wall: refetch it and fill what changed.
        manifestChanged: wallId => whenStarted(() => refreshWall(String(wallId).toLowerCase(), false).then(() => scheduleFill())),
        // The 3D opt-out (2.9's settings and the high-storage notice).
        setKeep3d: function (on) {
            if (win.bwPrefs && typeof win.bwPrefs.setCache3d === 'function') {
                win.bwPrefs.setCache3d(!!on);
            }
            return whenStarted(() => (on ? scheduleFill() : evict()));
        },
        keep3d,
        // Whether this device background-fills 3D (2.8's data-volume warning on opening 3D reads it): never on a phone
        // or tablet (bwAssetPlan.isMobile), nor on a metered connection, nor with 3D opted out.
        fills3dInBackground: () => keep3d() && !Plan().isMobile(win.navigator) && !Plan().isMetered(win.navigator),
        summary: () => state.summaryLoaded,
        // The running fill, for tests and 2.9's progress: resolves when the queue is drained.
        filled: () => state.filling || Promise.resolve()
    };
})(window);

if (!window.__bwAssetCacheManual) {
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => window.bwAssetCache.boot(), { once: true });
    } else {
        window.bwAssetCache.boot();
    }
}
