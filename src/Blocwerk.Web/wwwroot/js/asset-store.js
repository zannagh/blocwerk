/*
 * bwAssetStore — what asset-cache.js asks of the service worker and the Cache API (design 2026-10-21 §6.7, §6.8).
 *
 *   probeWorker: does the controlling worker cache `/a/` (PR 2.6b, blocwerk-shell-v4 or later)? It asks `sw-info`;
 *                a worker from before 2.6a ignores the message, so no reply within SW_INFO_TIMEOUT_MS is normal and
 *                just means "no background fill".
 *   record:      the worker's `asset-cached` / `asset-refused` for the page's partition: the ledger row, the outcome
 *                for verify, and the fill task waiting on it.
 *   verify:      did the worker store what the fill just fetched? 'stored' on its `asset-cached` message or a cache
 *                match, 'refused' on its `asset-refused` (the put threw: quota, a full device), else 'unknown'. A
 *                timeout is never a refusal: a slow lookup or a large put may still be writing.
 *   reconcile:   the ledger follows the caches. The worker caches without the page and the browser (or
 *                Clear-Site-Data) can empty a cache behind our back, so on start rows without an entry go and entries
 *                without a row are added, sized from their Content-Length.
 *   loadSummary: /api/me/cache-summary → { wallsWith3d, walls }, or null when the server cannot say.
 *   askWorker:   one message and its reply, or null (client-precache.js asks `client-precache` through it).
 * Nothing here throws into the page.
 */
window.bwAssetStore = (function () {
    'use strict';

    const PROTO = 1;
    const SW_INFO_TIMEOUT_MS = 1500;
    // Above the worker's own partition lookup timeout (2.5 s), so a slow server still answers in time.
    const REFRESH_TIMEOUT_MS = 3000;
    const VERIFY_WAIT_MS = 3000;
    const MIN_ROUTING_SHELL = 4;
    const SUFFIXES = ['img', '3d'];
    const SEEN_MAX = 512;

    const cacheName = (partition, suffix) => `bw-a1-${partition}-${suffix}`;
    const cacheUrl = (win, key) => `${win.location.origin}/__bw/${key}`;

    // The worker's reply of `replyType` to a `type` message (with `fields`), or null (no worker, an old one that ignores
    // it, a timeout).
    function askWorker(win, type = 'sw-info', replyType = 'sw-info', timeoutMs = SW_INFO_TIMEOUT_MS, fields = {}) {
        const sw = win.navigator.serviceWorker;
        const controller = sw && sw.controller;
        if (!controller || typeof win.MessageChannel !== 'function') {
            return Promise.resolve(null);
        }
        return new Promise(resolve => {
            const channel = new win.MessageChannel();
            const timer = win.setTimeout(() => resolve(null), timeoutMs);
            channel.port1.onmessage = event => {
                win.clearTimeout(timer);
                const data = event.data || {};
                resolve(data.proto === PROTO && data.type === replyType ? data : null);
            };
            try {
                controller.postMessage({ ...fields, proto: PROTO, type }, [channel.port2]);
            } catch (e) {
                win.clearTimeout(timer);
                resolve(null);
            }
        });
    }

    function probeWorker(win) {
        return askWorker(win).then(data => {
            const version = data && /^blocwerk-shell-v(\d+)$/.exec(data.shell || '');
            return !!version && Number(version[1]) >= MIN_ROUTING_SHELL;
        });
    }

    /**
     * Asks the worker to look its label up again (`sw-refresh-partition`). Resolves with the label it serves once the
     * lookup answered (its `partition-refreshed` reply), or null: no worker, a timeout, or a worker from before that
     * reply, which still refreshes but does not answer.
     */
    function refreshWorkerPartition(win) {
        return askWorker(win, 'sw-refresh-partition', 'partition-refreshed', REFRESH_TIMEOUT_MS)
            .then(data => (data && typeof data.partition === 'string' ? data.partition : null));
    }

    /** The label the worker serves right now (`sw-info`'s `partition`), or null when it does not say. */
    function workerPartition(win) {
        return askWorker(win).then(data => (data && typeof data.partition === 'string' ? data.partition : null));
    }

    async function inCache(win, partition, key) {
        for (const suffix of SUFFIXES) {
            try {
                if (!(await win.caches.has(cacheName(partition, suffix)))) {
                    continue;
                }
                const cache = await win.caches.open(cacheName(partition, suffix));
                if (await cache.match(cacheUrl(win, key))) {
                    return true;
                }
            } catch (e) {
                return false;
            }
        }
        return false;
    }

    /**
     * One worker message, for a page caching under state.active (asset-cache.js checks that): a stored asset gets its
     * ledger row, the outcome goes into `seen` (bounded) for verify, and a fill task waiting on the key hears it.
     */
    function record(win, state, data, now) {
        const known = data && (data.type === 'asset-cached' || data.type === 'asset-refused');
        if (!known || data.proto !== PROTO || typeof data.sha !== 'string') {
            return;
        }
        const key = win.bwAssetPlan.keyOf(data.sha, data.width);
        const outcome = data.type === 'asset-cached' ? 'stored' : 'refused';
        state.seen.set(key, outcome);
        if (state.seen.size > SEEN_MAX) {
            state.seen.delete(state.seen.keys().next().value);
        }
        if (outcome === 'stored') {
            state.env.ledger.putAsset({ partition: state.active, key, sha: data.sha, width: data.width || null, cache: null, bytes: data.bytes || 0, cachedAt: now, verifiedAt: now });
        }
        const waiter = state.waiters.get(key);
        if (waiter) {
            state.waiters.delete(key);
            waiter(outcome);
        }
    }

    // `seen` maps the keys announced so far to 'stored' or 'refused' (the message can beat the fill's own read of the
    // body); `waiters` maps a key to the resolver the message handler calls.
    function verify(win, seen, waiters, partition, task) {
        if (seen.has(task.key)) {
            return Promise.resolve(seen.get(task.key));
        }
        return new Promise(resolve => {
            const timer = win.setTimeout(() => {
                waiters.delete(task.key);
                inCache(win, partition, task.key).then(hit => resolve(hit ? 'stored' : 'unknown'));
            }, VERIFY_WAIT_MS);
            waiters.set(task.key, outcome => {
                win.clearTimeout(timer);
                resolve(outcome);
            });
        });
    }

    async function reconcile(win, ledger, partition, now) {
        const rows = new Map((await ledger.assetsOf(partition)).map(a => [a.key, a]));
        const present = new Set();
        for (const suffix of SUFFIXES) {
            let cache;
            let requests;
            try {
                // has() first: open() would create the cache.
                if (!(await win.caches.has(cacheName(partition, suffix)))) {
                    continue;
                }
                cache = await win.caches.open(cacheName(partition, suffix));
                requests = await cache.keys();
            } catch (e) {
                return;
            }
            for (const request of requests) {
                const key = String(request.url || request).split('/__bw/')[1];
                if (!key) {
                    continue;
                }
                present.add(key);
                if (!rows.has(key)) {
                    const hit = await cache.match(request);
                    const bytes = Number(hit && hit.headers.get('content-length')) || 0;
                    const [sha, width] = key.split('/');
                    await ledger.putAsset({ partition, key, sha, width: width ? Number(width) : null, cache: suffix, bytes, cachedAt: now, verifiedAt: now });
                }
            }
        }
        for (const key of rows.keys()) {
            if (!present.has(key)) {
                await ledger.deleteAsset(partition, key);
            }
        }
    }

    async function loadSummary(win) {
        try {
            const response = await win.fetch('/api/me/cache-summary', { credentials: 'same-origin', cache: 'no-store' });
            if (!response.ok) {
                return null;
            }
            const body = await response.json();
            return { wallsWith3d: body.wallsWith3d || 0, walls: (body.walls || []).map(w => String(w).toLowerCase()) };
        } catch (e) {
            return null;
        }
    }

    return { PROTO, SW_INFO_TIMEOUT_MS, VERIFY_WAIT_MS, askWorker, probeWorker, workerPartition, refreshWorkerPartition, record, verify, reconcile, loadSummary };
})();
