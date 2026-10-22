/*
 * bwAssetData — keeps each cached wall's data documents current in the ledger (Phase 3 plan §1, PR 3.3): the `wall`,
 * `holds`, `boulders`, `gallery` and `comments` documents the offline client reads, beside the manifest's assets.
 *
 * GET /api/walls/{w}/docs[?token=] with If-None-Match on the stored ETag, after every manifest round that left the
 * wall current (asset-cache.js), and on bwAssetData.changed(wallId, manifest): the circuit saw a feed event of a
 * document's input kinds (AssetCacheFeedRelay.cs), or with `manifest` a wall.manifest / wall.resync, which refetches the
 * manifest first (bwAssetCache.manifestChanged). Those come in bursts while someone sets, so a burst refreshes the
 * wall once, DEBOUNCE_MS after its first call, and the call returns at once (the circuit never waits on a fill).
 *   200      → the row's `data` becomes { kind → { sha, bytes, cursor } } and `dataEtag` the ETag. The fill downloads
 *              each document like core (asset-plan.js; the worker caches it in `-3d`). A replaced sha (a rebuilt
 *              document) goes from the device, unless another wall lists it:
 *                `comments` at once, not after the orphan grace: a deleted comment, an erased author;
 *                every other kind once its successor is cached, so the device never holds neither: until then the
 *                cached predecessor stays in `dataPrev` { kind → sha } (listed, so no orphan), and settle() (run
 *                by each eviction pass) lets it go.
 *   304      → unchanged.
 *   403/404  → the wall is dropped (wipe rule 4), as for the manifest.
 *   anything else, 401 and 410 included, or no network → the stored documents stand. Wiping on 410 and on a forced or
 *              gone sign-out, for the manifest and this index alike, is PR 3.5's.
 * Only a wall that already has a row in the active partition is refreshed (the manifest round made it): a kiosk keeps
 * its bound wall only, and a share link the wall it opened. This writes only its fields (data, dataEtag, dataPrev), through
 * the ledger's one merge.
 * Nothing here throws into the page.
 */
window.bwAssetData = (function (win) {
    'use strict';

    const DEBOUNCE_MS = 10 * 1000;
    const KIND = /^[a-z][a-z0-9]{0,31}$/;
    const SHA = /^[0-9a-f]{64}$/;
    // Replaced at once: its old copy may hold a deleted comment or an erased author's name.
    const PRIVATE_KINDS = new Set(['comments']);
    const timers = new Map();

    const Cache = () => win.bwAssetCache;

    function indexUrl(wallId, token) {
        const path = `/api/walls/${wallId}/docs`;
        return token ? `${path}?token=${encodeURIComponent(token)}` : path;
    }

    /** The index body's documents as the row's `data`: well-formed kinds and shas only, view3d never (asset-3d.js). */
    function compact(body) {
        const data = {};
        for (const doc of (body && Array.isArray(body.docs) ? body.docs : [])) {
            if (doc && KIND.test(doc.kind || '') && doc.kind !== 'view3d' && SHA.test(doc.sha || '')) {
                data[doc.kind] = { sha: doc.sha, bytes: Number(doc.bytes) || 0, cursor: typeof doc.cursor === 'string' ? doc.cursor : null };
            }
        }
        return data;
    }

    // Every sha a row held on to through its documents: `data` and `dataPrev`.
    const heldOf = row => new Set(Object.values((row && row.data) || {}).map(doc => doc.sha).concat(Object.values((row && row.dataPrev) || {})));

    // Writes `fields` (of data, dataEtag, dataPrev) onto the row as stored NOW; a row that went meanwhile (dropWall) stays gone.
    function write(env, partition, wallId, fields) {
        return env.ledger.updateWall(partition, wallId, current => (current ? Object.assign(current, fields) : undefined));
    }

    /**
     * Per kind, the cached sha to keep serving until `data`'s new one is cached: the replaced sha when it is cached,
     * else the one kept already. None once the new one is cached, and never for a private kind.
     */
    function keptOf(row, data, cached) {
        const kept = {};
        for (const [kind, doc] of Object.entries(data)) {
            const old = row.data && row.data[kind] ? row.data[kind].sha : null;
            const prior = row.dataPrev ? row.dataPrev[kind] : null;
            if (PRIVATE_KINDS.has(kind) || cached.has(doc.sha)) {
                continue;
            }
            const keep = [old, prior].find(sha => sha && sha !== doc.sha && cached.has(sha));
            if (keep) {
                kept[kind] = keep;
            }
        }
        return kept;
    }

    // Deletes `candidates` from the device, except those a wall of the partition still lists.
    async function dropUnlisted(env, partition, candidates) {
        if (candidates.length === 0) {
            return;
        }
        const listed = new Set();
        (await env.ledger.wallsOf(partition)).forEach(w => win.bwAssetLedger.listed(w).forEach(e => listed.add(e.sha)));
        const keys = (await env.ledger.assetsOf(partition)).filter(a => candidates.includes(a.sha) && !listed.has(a.sha)).map(a => a.key);
        await win.bwAssetWipe.deleteKeys(env, partition, keys);
    }

    const cachedShas = async (env, partition) => new Set((await env.ledger.assetsOf(partition)).map(a => a.sha));

    /** Each wall's kept predecessors whose successor is cached now go (asset-cache.js runs this before evicting). */
    async function settle(env, partition) {
        const cached = await cachedShas(env, partition);
        for (const row of await env.ledger.wallsOf(partition)) {
            if (!row.dataPrev || Object.keys(row.dataPrev).length === 0) {
                continue;
            }
            const before = heldOf(row);
            const dataPrev = keptOf(row, row.data || {}, cached);
            if (JSON.stringify(dataPrev) === JSON.stringify(row.dataPrev)) {
                continue;
            }
            await write(env, partition, row.wallId, { dataPrev });
            await dropUnlisted(env, partition, [...before]);
        }
    }

    /**
     * Refreshes one wall's documents. env: asset-cache.js's { fetch, ledger, partition(), caches, origin }. opts:
     * { token, dropWall }. Returns 'updated' | 'unchanged' | 'dropped' | 'offline' | 'switched' | 'skipped'.
     */
    async function refresh(env, partition, wallId, opts) {
        const row = await env.ledger.getWall(partition, wallId);
        if (!row) {
            return 'skipped';
        }
        const headers = row.data && row.dataEtag ? { 'If-None-Match': row.dataEtag } : {};
        let response;
        try {
            response = await env.fetch(indexUrl(wallId, opts.token), { credentials: 'same-origin', cache: 'no-store', headers });
        } catch (e) {
            return 'offline';
        }
        if (env.partition() !== partition) {
            return 'switched';
        }
        if (response.status === 403 || response.status === 404) {
            await opts.dropWall(wallId);
            return 'dropped';
        }
        if (response.status === 304) {
            return 'unchanged';
        }
        let data;
        try {
            data = response.ok ? compact(await response.json()) : null;
        } catch (e) {
            data = null;
        }
        if (!data) {
            return 'offline';
        }
        const dataPrev = keptOf(row, data, await cachedShas(env, partition));
        await write(env, partition, wallId, { data, dataEtag: response.headers.get('etag'), dataPrev });
        await dropUnlisted(env, partition, [...heldOf(row)]);
        return 'updated';
    }

    /**
     * A document input of the wall changed (the circuit's feed): refresh it DEBOUNCE_MS after the first call of a
     * burst; the manifest too when any call of the burst said so.
     */
    function changed(wallId, manifest) {
        const id = String(wallId || '').toLowerCase();
        if (!id) {
            return;
        }
        const pending = timers.get(id);
        if (pending) {
            pending.manifest = pending.manifest || !!manifest;
            return;
        }
        const burst = { manifest: !!manifest };
        timers.set(id, burst);
        win.setTimeout(() => {
            timers.delete(id);
            const cache = Cache();
            const run = cache && (burst.manifest ? cache.manifestChanged : cache.dataChanged);
            if (typeof run === 'function') {
                Promise.resolve(run(id)).catch(() => undefined);
            }
        }, DEBOUNCE_MS);
    }

    return { DEBOUNCE_MS, indexUrl, compact, refresh, settle, changed };
})(window);
