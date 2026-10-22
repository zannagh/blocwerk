/*
 * bwAssetData — keeps each cached wall's data documents current in the ledger (Phase 3 plan §1, PR 3.3): the `wall`,
 * `holds`, `boulders`, `gallery` and `comments` documents the offline client reads, beside the manifest's assets.
 *
 * GET /api/walls/{w}/docs[?token=] with If-None-Match on the stored ETag, after every manifest round that left the
 * wall current (asset-cache.js), and on bwAssetData.changed(wallId, manifest): the circuit saw a feed event of a
 * document's input kinds (AssetCacheFeedRelay.cs), or with `manifest` a wall.manifest / wall.resync, which refetches the
 * manifest first (bwAssetCache.manifestChanged). Those come in bursts while someone sets, so a wall refreshes at most
 * once per DEBOUNCE_MS, at its end, and the call returns at once (the circuit never waits on a fill).
 *   200      → the row's `data` becomes { kind → { sha, bytes, cursor } } and `dataEtag` the ETag. The fill downloads
 *              each document like core (asset-plan.js; the worker caches it in `-3d`). A sha that left `data` (a
 *              rebuilt document: a comment deleted, an author erased) is deleted from the device at once, not after
 *              the orphan grace, unless another wall lists it.
 *   304      → unchanged.
 *   403/404  → the wall is dropped (wipe rule 4), as for the manifest.
 *   anything else, or no network → the stored documents stand (401/410 are the session's: asset-session.js).
 * Only a wall that already has a row in the active partition is refreshed (the manifest round made it): a kiosk keeps
 * its bound wall only, and a share link the wall it opened. This writes only DATA_FIELDS, through the ledger's one merge.
 * Nothing here throws into the page.
 */
window.bwAssetData = (function (win) {
    'use strict';

    const DEBOUNCE_MS = 10 * 1000;
    const KIND = /^[a-z][a-z0-9]{0,31}$/;
    const SHA = /^[0-9a-f]{64}$/;
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

    const shasOf = data => new Set(Object.values(data || {}).map(doc => doc.sha));

    // Writes `data` and `dataEtag` onto the row as stored NOW; a row that went meanwhile (dropWall) stays gone.
    function write(env, partition, wallId, data, etag) {
        return env.ledger.updateWall(partition, wallId, current => (current ? Object.assign(current, { data, dataEtag: etag }) : undefined));
    }

    // The documents the new index replaced, unless another wall of the partition still lists them.
    async function dropReplaced(env, partition, before, after) {
        const gone = [...shasOf(before)].filter(sha => !shasOf(after).has(sha));
        if (gone.length === 0) {
            return;
        }
        const listed = new Set();
        (await env.ledger.wallsOf(partition)).forEach(w => win.bwAssetLedger.listed(w).forEach(e => listed.add(e.sha)));
        const keys = (await env.ledger.assetsOf(partition)).filter(a => gone.includes(a.sha) && !listed.has(a.sha)).map(a => a.key);
        await win.bwAssetWipe.deleteKeys(env, partition, keys);
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
        await write(env, partition, wallId, data, response.headers.get('etag'));
        await dropReplaced(env, partition, row.data, data);
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

    return { DEBOUNCE_MS, indexUrl, compact, refresh, changed };
})(window);
