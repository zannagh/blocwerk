/*
 * bwAssetStore — what asset-cache.js asks of the service worker and the Cache API (design 2026-10-21 §6.7, §6.8).
 *
 *   probeWorker: does the controlling worker cache `/a/` (PR 2.6b, blocwerk-shell-v4 or later)? It asks `sw-info`;
 *                a worker from before 2.6a ignores the message, so no reply within SW_INFO_TIMEOUT_MS is normal and
 *                just means "no background fill".
 *   verify:      did the worker store what the fill just fetched? 'stored' on its `asset-cached` message or a cache
 *                match, 'refused' on its `asset-refused` (the put threw: quota, a full device), else 'unknown'. A
 *                timeout is never a refusal: a slow lookup or a large put may still be writing.
 *   reconcile:   the ledger follows the caches. The worker caches without the page and the browser (or
 *                Clear-Site-Data) can empty a cache behind our back, so on start rows without an entry go and entries
 *                without a row are added, sized from their Content-Length.
 *   loadSummary: /api/me/cache-summary → { wallsWith3d, walls }, or null when the server cannot say.
 * Nothing here throws into the page.
 */
window.bwAssetStore = (function () {
    'use strict';

    const PROTO = 1;
    const SW_INFO_TIMEOUT_MS = 1500;
    const VERIFY_WAIT_MS = 3000;
    const MIN_ROUTING_SHELL = 4;
    const SUFFIXES = ['img', '3d'];

    const cacheName = (partition, suffix) => `bw-a1-${partition}-${suffix}`;
    const cacheUrl = (win, key) => `${win.location.origin}/__bw/${key}`;

    function probeWorker(win) {
        const sw = win.navigator.serviceWorker;
        const controller = sw && sw.controller;
        if (!controller || typeof win.MessageChannel !== 'function') {
            return Promise.resolve(false);
        }
        return new Promise(resolve => {
            const channel = new win.MessageChannel();
            const timer = win.setTimeout(() => resolve(false), SW_INFO_TIMEOUT_MS);
            channel.port1.onmessage = event => {
                win.clearTimeout(timer);
                const data = event.data || {};
                const version = /^blocwerk-shell-v(\d+)$/.exec(data.shell || '');
                resolve(data.proto === PROTO && data.type === 'sw-info' && !!version && Number(version[1]) >= MIN_ROUTING_SHELL);
            };
            try {
                controller.postMessage({ proto: PROTO, type: 'sw-info' }, [channel.port2]);
            } catch (e) {
                win.clearTimeout(timer);
                resolve(false);
            }
        });
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

    return { PROTO, SW_INFO_TIMEOUT_MS, VERIFY_WAIT_MS, probeWorker, verify, reconcile, loadSummary };
})();
