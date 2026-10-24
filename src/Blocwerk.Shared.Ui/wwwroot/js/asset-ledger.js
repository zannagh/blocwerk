/*
 * bwAssetLedger — the page's own record of what the on-device wall-asset cache holds (design 2026-10-21 §6.8).
 *
 * IndexedDB `blocwerk-assets` v2, four stores:
 *   walls  [partition, wallId] → { partition, wallId, etag, cursor, entries, pending, member,
 *                                  lastOpened, fetchedAt, keepOffline, save3d, docs, data, dataEtag, dataPrev, share }
 *          `entries` is the manifest's asset list, compacted to what the fill and eviction read:
 *          [{ sha, kind, tier, bytes, widths?, splats?, at? }] (`at`: a gallery image's upload time).
 *          `docs` the view3d documents (asset-3d.js); `data` the data documents of the docs index (asset-data.js),
 *          { kind → { sha, bytes, cursor } }. listed() is every sha a row holds on to. `member`: the wall was in the
 *          viewer's own list when its manifest was read (its documents are the members' class). `share`: on an `anon`
 *          row, the SHA-256 of the share link's token (shareHash), never the token itself.
 *   assets [partition, key]    → { partition, key, sha, width, cache, bytes, cachedAt, verifiedAt }
 *          `key` is the service worker's cache key path without the origin: `{sha}` or `{sha}/{width}`.
 *   meta   'meta'              → { id: 'meta', activePartition, userTag, schema, proto, locked }
 *          `locked` { label, since }: a `u-` partition kept hidden after its session expired (asset-lock.js, PR 3.5).
 *   mine   [partition, wallId] → { partition, wallId, cursor, ticks, favourites, fetchedAt } (v2; `u-` only, PR 3.4)
 *
 * UPGRADE v1 → v2 (Phase 3 PR 3.3) is in place: it only adds the `mine` store and stamps meta.schema, so every v1 row
 * stays as it was (a v1 wall row simply has no `data` yet). Every connection closes on `versionchange`, so an open tab
 * of this version never holds a later upgrade back; it carries on, on memory. A Release 1/2 tab has no such handler and
 * blocks the upgrade while it stays open: then this page runs on memory with `upgradePending()` true (not "storage
 * refused"), and the open, left running, finishes the upgrade once that tab goes; the next page start opens v2. A
 * Release 1/2 page meeting a v2 database gets a VersionError and runs on memory, which it already did on any refusal.
 * The IndexedDB backend and that open live in asset-ledger-idb.js (bwAssetLedgerIdb), read lazily from here.
 *
 * The budgets count bytes from here, never from navigator.storage.estimate(): on iOS estimate() reported 41 GB in use
 * during a 400 MB write. The ledger is a record, not the truth: asset-cache.js reconciles it against cache.keys() on
 * start, so a cache the browser (or Clear-Site-Data) emptied behind our back only costs a refill.
 *
 * Storage refused: if IndexedDB cannot open or a write throws (QuotaExceededError, a private window, a full device),
 * the ledger moves to an in-memory copy for the rest of the page's life. Nothing here ever rejects into the caller.
 * Such a ledger is `degraded` (a pending upgrade too): it knows nothing of what the device holds, so asset-cache.js
 * runs only the privacy wipes on it, never a reconcile, eviction or fill (they would delete real caches from an empty
 * record). `memory()` is the plain one, not degraded (the node tests run the whole cache on it).
 */
window.bwAssetLedger = (function () {
    'use strict';

    const DB_NAME = 'blocwerk-assets';
    const DB_VERSION = 2;
    const SCHEMA = 2;
    const META_KEY = 'meta';
    const KEYS = { walls: ['partition', 'wallId'], assets: ['partition', 'key'], meta: 'id', mine: ['partition', 'wallId'] };
    // The stores a partition wipe empties (meta records the active label and stays).
    const PARTITIONED = ['walls', 'assets', 'mine'];
    const Idb = () => window.bwAssetLedgerIdb;

    function keyOf(store, row) {
        const path = KEYS[store];
        return Array.isArray(path) ? path.map(p => row[p]).join('\u0000') : row[path];
    }

    function idKey(store, key) {
        return Array.isArray(key) ? key.join('\u0000') : key;
    }

    // The fallback, and what the node tests run on: the same six calls over Maps.
    function memoryBackend() {
        const stores = { walls: new Map(), assets: new Map(), meta: new Map(), mine: new Map() };
        return {
            kind: 'memory',
            get: async (store, key) => clone(stores[store].get(idKey(store, key))),
            put: async (store, row) => { stores[store].set(keyOf(store, row), clone(row)); },
            delete: async (store, key) => { stores[store].delete(idKey(store, key)); },
            all: async store => [...stores[store].values()].map(clone),
            clear: async store => { stores[store].clear(); },
            update: async (store, key, change) => {
                const next = change(clone(stores[store].get(idKey(store, key))));
                if (next) {
                    stores[store].set(keyOf(store, next), clone(next));
                }
                return clone(next);
            }
        };
    }

    function clone(value) {
        return value === undefined ? undefined : JSON.parse(JSON.stringify(value));
    }

    // Wraps a backend so that any failure switches to memory for good: a refused write never breaks the page.
    function resilient(primary, fallen) {
        let backend = primary;
        let degraded = !!fallen;
        const fallback = () => {
            if (backend.kind !== 'memory') {
                backend = memoryBackend();
                degraded = true;
            }
        };
        const call = name => async (...args) => {
            try {
                return await backend[name](...args);
            } catch (e) {
                fallback();
                return backend[name](...args);
            }
        };
        return {
            get kind() { return backend.kind; },
            get degraded() { return degraded; },
            get: call('get'), put: call('put'), delete: call('delete'), all: call('all'), clear: call('clear'), update: call('update')
        };
    }

    function create(backend, fallen) {
        const b = resilient(backend, fallen);
        const byPartition = (rows, partition) => rows.filter(r => r.partition === partition);
        return {
            get kind() { return b.kind; },
            get degraded() { return b.degraded; },
            getMeta: async () => (await b.get('meta', META_KEY)) || { id: META_KEY, activePartition: null, schema: SCHEMA },
            setMeta: async patch => {
                const meta = Object.assign((await b.get('meta', META_KEY)) || { id: META_KEY, schema: SCHEMA }, patch);
                await b.put('meta', meta);
                return meta;
            },
            wallsOf: async partition => byPartition(await b.all('walls'), partition),
            getWall: (partition, wallId) => b.get('walls', [partition, wallId]),
            /**
             * THE way to write a wall row: `change(current)` gets the stored row (or undefined) and returns the row to
             * store (or nothing to skip), read and written in one transaction. Each writer changes only its own fields:
             * the manifest round its MANIFEST_FIELDS (asset-manifest.js), "Save 3D offline" keepOffline/save3d/docs
             * (asset-3d.js), so neither can write back a stale copy of the other's.
             */
            updateWall: (partition, wallId, change) => b.update('walls', [partition, wallId], change),
            deleteWall: (partition, wallId) => b.delete('walls', [partition, wallId]),
            assetsOf: async partition => byPartition(await b.all('assets'), partition),
            getAsset: (partition, key) => b.get('assets', [partition, key]),
            putAsset: row => b.put('assets', row),
            deleteAsset: (partition, key) => b.delete('assets', [partition, key]),
            // The viewer's own ticks and favourites per wall (PR 3.4 writes them; `u-` partitions only).
            mineOf: async partition => byPartition(await b.all('mine'), partition),
            putMine: row => b.put('mine', row),
            deleteMine: (partition, wallId) => b.delete('mine', [partition, wallId]),
            // Every wall, asset and mine row outside the keep set: the labels in `keep` (an array, or one label; null or []
            // keeps nothing). The set is the active label plus a locked one (asset-lock.js). Meta stays: it records both.
            deleteAllExcept: async keep => {
                const kept = new Set((Array.isArray(keep) ? keep : [keep]).filter(label => typeof label === 'string'));
                for (const store of PARTITIONED) {
                    if (kept.size === 0) {
                        await b.clear(store);
                        continue;
                    }
                    for (const row of await b.all(store)) {
                        if (!kept.has(row.partition)) {
                            await b.delete(store, KEYS[store].map(p => row[p]));
                        }
                    }
                }
            }
        };
    }

    /**
     * Every sha a wall row holds on to: its manifest entries, its view3d documents (`docs`), its data documents
     * (`data`) and the replaced ones still served until their successor is cached (`dataPrev`, { kind → sha }), the
     * last two as entries of tier `data`. Eviction (classify) and dropWall read this, so none of them is an orphan.
     */
    function listed(wall) {
        const data = Object.entries((wall && wall.data) || {})
            .filter(([, doc]) => doc && typeof doc.sha === 'string')
            .map(([kind, doc]) => ({ sha: doc.sha, kind, tier: 'data', bytes: doc.bytes || 0 }));
        const prev = Object.entries((wall && wall.dataPrev) || {})
            .filter(([, sha]) => typeof sha === 'string')
            .map(([kind, sha]) => ({ sha, kind, tier: 'data', bytes: 0 }));
        return ((wall && wall.entries) || []).concat((wall && wall.docs) || [], data, prev);
    }

    /** The SHA-256 (lower-case hex) of a share token, as an `anon` wall row keeps it; null without WebCrypto. */
    async function shareHash(token) {
        try {
            const subtle = window.crypto && window.crypto.subtle;
            if (!token || !subtle) {
                return null;
            }
            const digest = await subtle.digest('SHA-256', new TextEncoder().encode(String(token)));
            return [...new Uint8Array(digest)].map(b => b.toString(16).padStart(2, '0')).join('');
        } catch (e) {
            return null;
        }
    }

    let opened = null;
    let pending = false;

    return {
        SCHEMA,
        DB_NAME,
        DB_VERSION,
        // The page's ledger: IndexedDB when it opens, else memory.
        open: function () {
            if (!opened) {
                opened = (async () => {
                    try {
                        if (!window.indexedDB) {
                            return create(memoryBackend(), true);
                        }
                        const spec = { name: DB_NAME, version: DB_VERSION, keys: KEYS, schema: SCHEMA, metaKey: META_KEY };
                        return create(Idb().backend(await Idb().open(window.indexedDB, spec)));
                    } catch (e) {
                        pending = !!e && !!Idb() && e.name === Idb().PENDING;
                        return create(memoryBackend(), true);
                    }
                })();
            }
            return opened;
        },
        // True when this page runs on memory because the v2 upgrade waits for another tab: "Updating offline data…",
        // never "storage refused". The next page start tries again.
        upgradePending: () => pending,
        listed,
        shareHash,
        memory: () => create(memoryBackend()),
        create
    };
})();
