/*
 * bwClientPrecache — the page's half of the offline client's precache (plan 2026-10-22 §1, PR 3.9).
 *
 * The page triggers it, the worker does it (service-worker.js, the offline client section). Once this page's asset-cache start and its first fill
 * are done, and only when:
 *   - the page names a client version (its `bw-client-version` meta) and Save-Data is off (any connection type counts);
 *   - the page caches for a person (`u-…`), a share viewer (`anon`) or a kiosk tablet (`k-…`, PR 3.15) with a durable
 *     ledger, and the device holds at least one wall of that partition, so there is something to open offline (a kiosk
 *     that may keep nothing is `none`, and is never precached);
 *   - the worker answers `sw-info` with a `client` field (blocwerk-shell-v5 or later; an older one is left alone);
 *   - the worker's complete version is not already this page's;
 *   - no failed attempt for this version started within RETRY_MS (a failing fill is not retried on every page);
 * it posts `client-precache` and records the outcome in localStorage (MARKER_KEY: `{ version, at, complete, with3d, at3d }`), the
 * page's completeness marker. The worker's own record decides the hand-off; the marker only spares the repeat.
 * THE 3D CODE (PR 3.13). The message's `group3d` says whether the 3D view's code (~920 KiB) comes along: `precache` on a
 * desktop with 3D kept; `opened` on a phone or tablet (bwAssetPlan.isMobile, the asset cache's own test: no background
 * 3D there), where the worker takes it only once a 3D view was opened online on the device; `none` with the 3D opt-out,
 * unless a wall of the partition is saved for offline 3D (`keepOffline` and `save3d`): that device is `opened`. The
 * worker answers `with3d` (all the 3D code is there); a desktop whose complete version lacks it asks again, at most
 * once per RETRY_MS (`at3d`). `store3d()` is a 3D view opened online (asset-3d.js): unless the device is `none`, the
 * worker stores the 3D code into the complete version and keeps it from then on.
 * THE KIOSK STAMP (PR 3.15). On a kiosk tablet (`k-…`) every page, once its fill is done, also writes
 * `{ label, at }` to the worker's state cache (KIOSK_FRESH_KEY): `at` is the newest manifest success (`fetchedAt`) of the
 * partition's rows. The worker hands a `k-` tablet to the client only within KIOSK_MAX_AGE of it, as the client reads the
 * row itself, so a tablet revoked, re-paired or changed while offline stops showing its copy after 7 days, as a `u-` lock does.
 * Nothing here throws into the page.
 */
window.bwClientPrecache = (function (win) {
    'use strict';

    const MARKER_KEY = 'bw-client-precache';
    const RETRY_MS = 10 * 60 * 1000;
    // A cold 2–3 MB fill on a slow connection; the worker keeps going after the page stops waiting.
    const ANSWER_MS = 5 * 60 * 1000;
    const VERSION = /^[0-9a-f]{16}$/;
    const LABEL = /^(anon|[uk]-[a-z2-7]{20})$/;

    const KIOSK_LABEL = /^k-[a-z2-7]{20}$/;
    const KIOSK_FRESH_KEY = '/__bw/kiosk-fresh';
    const STATE_CACHE = 'bw-sw-state';

    const state = { done: null, stamped: null };

    function pageVersion() {
        const meta = win.document.querySelector('meta[name="bw-client-version"]');
        const value = meta ? meta.getAttribute('content') : null;
        return VERSION.test(value || '') ? value : null;
    }

    const saveData = () => !!(win.navigator.connection && win.navigator.connection.saveData);

    // A wall of the partition saved for offline 3D: its 3D shows whatever the opt-out says (client-3d.js).
    async function savedFor3d(cache) {
        const s = cache.state();
        if (!s.env || s.env.ledger.degraded || !LABEL.test(s.active || '')) {
            return false;
        }
        return (await s.env.ledger.wallsOf(s.active)).some(row => row.keepOffline && row.save3d);
    }

    // The 3D code's part of the precache (see the header).
    async function group3d(cache) {
        if (typeof cache.keep3d === 'function' && !cache.keep3d()) {
            return (await savedFor3d(cache)) ? 'opened' : 'none';
        }
        const plan = win.bwAssetPlan;
        const mobile = !plan || plan.isMobile(win.navigator, typeof win.matchMedia === 'function' ? q => win.matchMedia(q) : null);
        return mobile ? 'opened' : 'precache';
    }

    function readMarker() {
        try {
            const marker = JSON.parse(win.localStorage.getItem(MARKER_KEY) || 'null');
            return marker && typeof marker === 'object' ? marker : null;
        } catch (e) {
            return null;
        }
    }

    function writeMarker(marker) {
        try {
            win.localStorage.setItem(MARKER_KEY, JSON.stringify(marker));
        } catch (e) {
            // No storage: the worker's record still decides; the page only asks again next time.
        }
    }

    // The asset cache's partition and ledger allow it, and the device holds a wall of that partition.
    async function hasOfflineWalls(cache) {
        const s = cache.state();
        if (!LABEL.test(s.active || '') || !s.env || s.env.ledger.degraded) {
            return false;
        }
        return (await s.env.ledger.wallsOf(s.active)).length > 0;
    }

    async function run(cache, store) {
        const version = pageVersion();
        if (!version || saveData() || !cache || !store) {
            return 'skipped';
        }
        await cache.ready;
        await cache.filled();
        if (!(await hasOfflineWalls(cache))) {
            return 'skipped';
        }
        const info = await store.askWorker(win);
        if (!info || !('client' in info)) {
            return 'unsupported';
        }
        const group = await group3d(cache);
        const marker = readMarker();
        const mine = !!(marker && marker.version === version);
        const had3d = mine && !!marker.with3d;
        const tried3d = mine && Date.now() - Number(marker.at3d) < RETRY_MS;
        if (info.client === version && (group !== 'precache' || had3d || tried3d)) {
            writeMarker({ version, at: Date.now(), complete: true, with3d: had3d, at3d: mine ? marker.at3d : undefined });
            return 'complete';
        }
        // Only a failed attempt waits (a complete version that lacks the 3D code never does): a marker that says complete
        // while the worker says not means it lost its copy.
        if (info.client !== version && marker && marker.version === version && marker.complete === false && Date.now() - Number(marker.at) < RETRY_MS) {
            return 'waiting';
        }
        const at3d = group === 'precache' ? Date.now() : undefined;
        // A top-up of a complete version stays complete whatever its answer; a fill is not until it says so.
        writeMarker({ version, at: Date.now(), complete: info.client === version, at3d });
        const answer = await store.askWorker(win, 'client-precache', 'client-precached', ANSWER_MS, { group3d: group });
        if (!answer || !answer.complete) {
            return 'failed';
        }
        writeMarker({ version: answer.version, at: Date.now(), complete: true, with3d: answer.with3d === true, at3d });
        return answer.version === version ? 'complete' : 'other';
    }

    // The kiosk stamp (see the header): written only after a fill on a durable `k-` ledger with a successful manifest.
    async function stamp(cache) {
        if (!cache) {
            return null;
        }
        await cache.ready;
        await cache.filled();
        const s = cache.state();
        if (!KIOSK_LABEL.test(s.active || '') || !s.env || s.env.ledger.degraded) {
            return null;
        }
        const at = Math.max(0, ...(await s.env.ledger.wallsOf(s.active)).map(row => Number(row.fetchedAt) || 0));
        if (at <= 0) {
            return null;
        }
        const value = { label: s.active, at };
        await (await win.caches.open(STATE_CACHE)).put(KIOSK_FRESH_KEY, new Response(JSON.stringify(value)));
        return value;
    }

    function boot() {
        if (!state.done) {
            state.stamped = stamp(win.bwAssetCache).catch(() => null);
            state.done = run(win.bwAssetCache, win.bwAssetStore).catch(() => 'failed');
        }
        return state.done;
    }

    // A 3D view opened online (see the header); nothing to wait for. Never with the opt-out and no wall saved for 3D.
    async function store3d() {
        const cache = win.bwAssetCache;
        const store = win.bwAssetStore;
        try {
            const group = cache && store ? await group3d(cache) : 'none';
            return group === 'none' ? null : await store.askWorker(win, 'client-store-3d', 'client-stored-3d', ANSWER_MS, { group3d: group });
        } catch (e) {
            return null;
        }
    }

    return {
        boot,
        store3d,
        // The outcome of this page's attempt, for tests: skipped, unsupported, complete, waiting, failed or other.
        get done() { return state.done; },
        // The kiosk stamp this page wrote ({ label, at }), or null; for tests.
        get stamped() { return state.stamped; },
        MARKER_KEY
    };
})(window);

// After asset-cache.js's own DOM-ready boot (registered before this one), so `ready` is its start.
if (!window.__bwAssetCacheManual) {
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => window.bwClientPrecache.boot(), { once: true });
    } else {
        window.bwClientPrecache.boot();
    }
}
