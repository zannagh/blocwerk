/*
 * bwAssetLedger — the page's own record of what the on-device wall-asset cache holds (design 2026-10-21 §6.8).
 *
 * IndexedDB `blocwerk-assets` v1, three stores:
 *   walls  [partition, wallId] → { partition, wallId, etag, cursor, entries, pending, member,
 *                                  lastOpened, fetchedAt, keepOffline }
 *          `entries` is the manifest's asset list, compacted to what the fill and eviction read:
 *          [{ sha, kind, tier, bytes, widths?, splats?, at? }] (`at`: a gallery image's upload time).
 *   assets [partition, key]    → { partition, key, sha, width, cache, bytes, cachedAt, verifiedAt }
 *          `key` is the service worker's cache key path without the origin: `{sha}` or `{sha}/{width}`.
 *   meta   'meta'              → { id: 'meta', activePartition, userTag, schema, proto }
 *
 * The budgets count bytes from here, never from navigator.storage.estimate(): on iOS estimate() reported 41 GB in use
 * during a 400 MB write. The ledger is a record, not the truth: asset-cache.js reconciles it against cache.keys() on
 * start, so a cache the browser (or Clear-Site-Data) emptied behind our back only costs a refill.
 *
 * Storage refused: if IndexedDB cannot open or a write throws (QuotaExceededError, a private window, a full device),
 * the ledger moves to an in-memory copy for the rest of the page's life. Nothing here ever rejects into the caller.
 */
window.bwAssetLedger = (function () {
    'use strict';

    const DB_NAME = 'blocwerk-assets';
    const DB_VERSION = 1;
    const SCHEMA = 1;
    const META_KEY = 'meta';
    const KEYS = { walls: ['partition', 'wallId'], assets: ['partition', 'key'], meta: 'id' };

    function keyOf(store, row) {
        const path = KEYS[store];
        return Array.isArray(path) ? path.map(p => row[p]).join('\u0000') : row[path];
    }

    function idKey(store, key) {
        return Array.isArray(key) ? key.join('\u0000') : key;
    }

    // The fallback, and what the node tests run on: the same five calls over Maps.
    function memoryBackend() {
        const stores = { walls: new Map(), assets: new Map(), meta: new Map() };
        return {
            kind: 'memory',
            get: async (store, key) => clone(stores[store].get(idKey(store, key))),
            put: async (store, row) => { stores[store].set(keyOf(store, row), clone(row)); },
            delete: async (store, key) => { stores[store].delete(idKey(store, key)); },
            all: async store => [...stores[store].values()].map(clone),
            clear: async store => { stores[store].clear(); }
        };
    }

    function clone(value) {
        return value === undefined ? undefined : JSON.parse(JSON.stringify(value));
    }

    function request(req) {
        return new Promise((resolve, reject) => {
            req.onsuccess = () => resolve(req.result);
            req.onerror = () => reject(req.error);
        });
    }

    function idbBackend(db) {
        function run(store, mode, work) {
            return new Promise((resolve, reject) => {
                const tx = db.transaction(store, mode);
                let result;
                request(work(tx.objectStore(store))).then(r => { result = r; }, reject);
                tx.oncomplete = () => resolve(result);
                tx.onerror = () => reject(tx.error);
                tx.onabort = () => reject(tx.error);
            });
        }
        return {
            kind: 'idb',
            get: (store, key) => run(store, 'readonly', s => s.get(key)),
            put: (store, row) => run(store, 'readwrite', s => s.put(row)).then(() => undefined),
            delete: (store, key) => run(store, 'readwrite', s => s.delete(key)).then(() => undefined),
            all: store => run(store, 'readonly', s => s.getAll()),
            clear: store => run(store, 'readwrite', s => s.clear()).then(() => undefined)
        };
    }

    function openIdb() {
        return new Promise((resolve, reject) => {
            const req = window.indexedDB.open(DB_NAME, DB_VERSION);
            req.onupgradeneeded = () => {
                const db = req.result;
                for (const [name, keyPath] of Object.entries(KEYS)) {
                    if (!db.objectStoreNames.contains(name)) {
                        db.createObjectStore(name, { keyPath });
                    }
                }
            };
            req.onsuccess = () => resolve(req.result);
            req.onerror = () => reject(req.error);
            req.onblocked = () => reject(new Error('blocwerk-assets upgrade blocked'));
        });
    }

    // Wraps a backend so that any failure switches to memory for good: a refused write never breaks the page.
    function resilient(primary) {
        let backend = primary;
        const fallback = () => {
            if (backend.kind !== 'memory') {
                backend = memoryBackend();
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
            get: call('get'), put: call('put'), delete: call('delete'), all: call('all'), clear: call('clear')
        };
    }

    function create(backend) {
        const b = resilient(backend);
        const byPartition = (rows, partition) => rows.filter(r => r.partition === partition);
        return {
            get kind() { return b.kind; },
            getMeta: async () => (await b.get('meta', META_KEY)) || { id: META_KEY, activePartition: null, schema: SCHEMA },
            setMeta: async patch => {
                const meta = Object.assign((await b.get('meta', META_KEY)) || { id: META_KEY, schema: SCHEMA }, patch);
                await b.put('meta', meta);
                return meta;
            },
            wallsOf: async partition => byPartition(await b.all('walls'), partition),
            getWall: (partition, wallId) => b.get('walls', [partition, wallId]),
            putWall: row => b.put('walls', row),
            deleteWall: (partition, wallId) => b.delete('walls', [partition, wallId]),
            assetsOf: async partition => byPartition(await b.all('assets'), partition),
            getAsset: (partition, key) => b.get('assets', [partition, key]),
            putAsset: row => b.put('assets', row),
            deleteAsset: (partition, key) => b.delete('assets', [partition, key]),
            // Every wall and asset row outside `keep` (null keeps nothing). Meta stays: it records the active label.
            deleteAllExcept: async keep => {
                for (const store of ['walls', 'assets']) {
                    if (keep === null) {
                        await b.clear(store);
                        continue;
                    }
                    for (const row of await b.all(store)) {
                        if (row.partition !== keep) {
                            await b.delete(store, KEYS[store].map(p => row[p]));
                        }
                    }
                }
            }
        };
    }

    let opened = null;

    return {
        SCHEMA,
        // The page's ledger: IndexedDB when it opens, else memory.
        open: function () {
            if (!opened) {
                opened = (async () => {
                    try {
                        if (!window.indexedDB) {
                            return create(memoryBackend());
                        }
                        return create(idbBackend(await openIdb()));
                    } catch (e) {
                        return create(memoryBackend());
                    }
                })();
            }
            return opened;
        },
        memory: () => create(memoryBackend()),
        create
    };
})();
