/*
 * bwClientPrecache — the page's half of the offline client's precache (plan 2026-10-22 §1, PR 3.9).
 *
 * The page triggers it, the worker does it (sw-client-cache.js). Once this page's asset-cache start and its first fill
 * are done, and only when:
 *   - the page names a client version (its `bw-client-version` meta) and Save-Data is off (any connection type counts);
 *   - the page caches for a person (`u-…`) or a share viewer (`anon`) with a durable ledger, and the device holds at
 *     least one wall of that partition, so there is something to open offline (a kiosk waits for PR 3.15);
 *   - the worker answers `sw-info` with a `client` field (blocwerk-shell-v5 or later; an older one is left alone);
 *   - the worker's complete version is not already this page's;
 *   - no failed attempt for this version started within RETRY_MS (a failing fill is not retried on every page);
 * it posts `client-precache` and records the outcome in localStorage (MARKER_KEY: `{ version, at, complete }`), the
 * page's completeness marker. The worker's own record decides the hand-off; the marker only spares the repeat.
 * Nothing here throws into the page.
 */
window.bwClientPrecache = (function (win) {
    'use strict';

    const MARKER_KEY = 'bw-client-precache';
    const RETRY_MS = 10 * 60 * 1000;
    // A cold 2–3 MB fill on a slow connection; the worker keeps going after the page stops waiting.
    const ANSWER_MS = 5 * 60 * 1000;
    const VERSION = /^[0-9a-f]{16}$/;
    const LABEL = /^(anon|u-[a-z2-7]{20})$/;

    const state = { done: null };

    function pageVersion() {
        const meta = win.document.querySelector('meta[name="bw-client-version"]');
        const value = meta ? meta.getAttribute('content') : null;
        return VERSION.test(value || '') ? value : null;
    }

    const saveData = () => !!(win.navigator.connection && win.navigator.connection.saveData);

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
        if (info.client === version) {
            writeMarker({ version, at: Date.now(), complete: true });
            return 'complete';
        }
        const marker = readMarker();
        // Only a failed attempt waits: a marker that says complete while the worker says not means it lost its copy.
        if (marker && marker.version === version && marker.complete === false && Date.now() - Number(marker.at) < RETRY_MS) {
            return 'waiting';
        }
        writeMarker({ version, at: Date.now(), complete: false });
        const answer = await store.askWorker(win, 'client-precache', 'client-precached', ANSWER_MS);
        if (!answer || !answer.complete) {
            return 'failed';
        }
        writeMarker({ version: answer.version, at: Date.now(), complete: true });
        return answer.version === version ? 'complete' : 'other';
    }

    function boot() {
        if (!state.done) {
            state.done = run(win.bwAssetCache, win.bwAssetStore).catch(() => 'failed');
        }
        return state.done;
    }

    return {
        boot,
        // The outcome of this page's attempt, for tests: skipped, unsupported, complete, waiting, failed or other.
        get done() { return state.done; },
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
