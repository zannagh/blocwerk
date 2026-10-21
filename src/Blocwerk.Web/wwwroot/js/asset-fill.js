/*
 * bwAssetFill — the background fill of the wall-asset cache (design 2026-10-21 §6.8).
 *
 * The page fetches each missing asset with `priority: 'low'`; the service worker (sw-asset-cache.js) stores it in the
 * partition's cache and posts `asset-cached`. This file never writes the Cache API itself.
 *
 * ORDER (planFill): tier first, then wall. Every wall's `core` comes before any wall's 3D, and within a tier the open
 * wall goes first, then the walls by when they were last opened. `core` is always planned, for every wall, whatever
 * the budget. The 3D tier (textures, masks, one splat level) is planned only while 3D is kept and the 3D budget has
 * room. `lazy` is never background-filled: it joins the cache on use, through the worker.
 *
 * RUN (createRunner): 1 request at a time on cellular or Save-Data, else 2; paused while the page is hidden; one tab
 * at a time (navigator.locks 'bw-fill'). The partition is captured per request: a switch mid-flight records nothing.
 *
 * STORAGE REFUSED. A fetched asset that the worker did not store (no `asset-cached`, no cache match) means the
 * browser refused it. The runner asks the owner to free one wall and retries once; a second refusal stops the fill
 * for the page's life, which leaves every asset streaming from the network. The page never sees an error.
 */
window.bwAssetFill = (function () {
    'use strict';

    const WIDTHS = [640, 1280, 1920, 2560];
    // image-res.js: at fit the rendition is capped at 1920 (FIT_CAP), which is what a page shows first.
    const FIT_CAP = 1920;
    const THUMB = 640;
    // iOS fails a single Cache API entry at about 600 MB (the storage probe); 400 MB stored fine.
    const MAX_ENTRY_BYTES = 400 * 1024 * 1024;

    /** The rendition a wall photo is shown at on this device at fit. */
    function fitWidth(cssWidth, dpr) {
        const needed = (cssWidth || 0) * (dpr || 1);
        const rung = WIDTHS.find(w => w >= needed) || WIDTHS[WIDTHS.length - 1];
        return Math.min(rung, FIT_CAP);
    }

    function keyOf(sha, width) {
        return width ? `${sha}/${width}` : sha;
    }

    function urlOf(wallId, sha, width, token) {
        const path = `/api/walls/${wallId}/a/${sha}` + (width ? `/${width}` : '');
        return token ? `${path}?token=${encodeURIComponent(token)}` : path;
    }

    function wallOrder(walls, openWallId) {
        return walls.slice().sort((a, b) => {
            if (a.wallId === openWallId || b.wallId === openWallId) {
                return a.wallId === openWallId ? -1 : 1;
            }
            return (b.lastOpened || 0) - (a.lastOpened || 0) || String(a.wallId).localeCompare(String(b.wallId));
        });
    }

    function coreTasks(wall, entry, width) {
        if (!Array.isArray(entry.widths) || entry.widths.length === 0) {
            return [{ sha: entry.sha, width: null }];
        }
        return [...new Set([width, THUMB])].filter(w => entry.widths.includes(w)).map(w => ({ sha: entry.sha, width: w }));
    }

    // Textures and masks whole; of the splat levels only the smallest, unless the wall already has one cached.
    function view3dEntries(wall, cachedShas) {
        const entries = (wall.entries || []).filter(e => e.tier === 'view3d' && e.sha);
        const splats = entries.filter(e => e.kind === 'splat');
        const others = entries.filter(e => e.kind !== 'splat');
        if (splats.length === 0 || splats.some(e => cachedShas.has(e.sha))) {
            return others;
        }
        const smallest = splats.slice().sort((a, b) => (a.splats || Infinity) - (b.splats || Infinity))[0];
        return others.concat([smallest]);
    }

    /**
     * The ordered download list. `state`: { walls, cachedKeys: Set, openWallId, keep3d, budget3d, used3d, width,
     * tokens: Map wallId → token }. Returns [{ key, url, sha, width, tier, wallId, bytes }].
     */
    function planFill(state) {
        const walls = wallOrder(state.walls, state.openWallId);
        const planned = new Set(state.cachedKeys);
        const cachedShas = new Set([...state.cachedKeys].map(k => String(k).split('/')[0]));
        const tasks = [];
        const push = (wall, entry, width, tier) => {
            const key = keyOf(entry.sha, width);
            if (planned.has(key)) {
                return false;
            }
            planned.add(key);
            const token = state.tokens ? state.tokens.get(wall.wallId) : null;
            tasks.push({ key, url: urlOf(wall.wallId, entry.sha, width, token), sha: entry.sha, width, tier, wallId: wall.wallId, bytes: entry.bytes || 0 });
            return true;
        };
        for (const wall of walls) {
            for (const entry of (wall.entries || []).filter(e => e.tier === 'core' && e.sha)) {
                coreTasks(wall, entry, state.width).forEach(t => push(wall, entry, t.width, 'core'));
            }
        }
        if (state.keep3d) {
            let used = state.used3d || 0;
            for (const wall of walls) {
                for (const entry of view3dEntries(wall, cachedShas)) {
                    const bytes = entry.bytes || 0;
                    if (bytes > MAX_ENTRY_BYTES || used + bytes > state.budget3d) {
                        continue;
                    }
                    if (push(wall, entry, null, 'view3d')) {
                        used += bytes;
                    }
                }
            }
        }
        return tasks;
    }

    function waitVisible(doc) {
        return new Promise(resolve => {
            const onChange = () => {
                if (doc.visibilityState !== 'hidden') {
                    doc.removeEventListener('visibilitychange', onChange);
                    resolve();
                }
            };
            doc.addEventListener('visibilitychange', onChange);
        });
    }

    // One filling tab: another tab holding 'bw-fill' means this one skips its run.
    function lock(nav, fn) {
        const locks = nav.locks;
        if (!locks || typeof locks.request !== 'function') {
            return fn();
        }
        return locks.request('bw-fill', { ifAvailable: true }, held => (held ? fn() : null));
    }

    function concurrencyOf(nav) {
        const c = nav && nav.connection;
        if (!c) {
            return 2;
        }
        const slow = c.saveData || c.type === 'cellular' || /(^|-)2g$|^3g$/.test(c.effectiveType || '');
        return slow ? 1 : 2;
    }

    /**
     * env: { fetch(url, init), verify(task) → Promise<bool>, partition() → label, hidden() → bool,
     *        waitVisible() → Promise, concurrency() → n, freeOneWall() → Promise<bool>, lock(fn) → Promise }
     * Returns { run(tasks) → Promise<'done'|'offline'|'streaming'|'locked'>, streaming }.
     */
    function createRunner(env) {
        let streaming = false;
        let retried = false;

        async function one(task) {
            const label = env.partition();
            const response = await env.fetch(task.url, { priority: 'low', credentials: 'same-origin' });
            // A 404/403 (the asset or the access went) is the manifest's business: its next fetch drops it.
            if (!response.ok) {
                return 'next';
            }
            await response.arrayBuffer();
            if (env.partition() !== label) {
                return 'next';
            }
            return (await env.verify(task)) ? 'next' : 'refused';
        }

        async function drain(queue) {
            while (queue.length > 0 && !streaming) {
                if (env.hidden()) {
                    await env.waitVisible();
                }
                const task = queue.shift();
                let outcome = await one(task);
                if (outcome === 'refused') {
                    if (!retried && await env.freeOneWall()) {
                        retried = true;
                        outcome = await one(task);
                    }
                    if (outcome === 'refused') {
                        streaming = true;
                    }
                }
            }
        }

        async function run(tasks) {
            if (streaming) {
                return 'streaming';
            }
            const queue = tasks.slice();
            const workers = Math.max(1, env.concurrency());
            const result = await env.lock(async () => {
                try {
                    await Promise.all(Array.from({ length: workers }, () => drain(queue)));
                } catch (e) {
                    return 'offline';
                }
                return streaming ? 'streaming' : 'done';
            });
            return result || 'locked';
        }

        return { run, get streaming() { return streaming; } };
    }

    return { WIDTHS, THUMB, MAX_ENTRY_BYTES, fitWidth, keyOf, urlOf, planFill, concurrencyOf, waitVisible, lock, createRunner };
})();
