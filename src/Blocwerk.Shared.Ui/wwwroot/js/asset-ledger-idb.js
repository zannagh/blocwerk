/*
 * bwAssetLedgerIdb — the IndexedDB half of bwAssetLedger (asset-ledger.js, which owns the schema and passes it in): the
 * store backend, and the open that runs the in-place upgrade. Split out of asset-ledger.js (Phase 3 PR 3.5) so neither
 * file outgrows the size rule; asset-ledger.js reads this one lazily, so the load order of the two does not matter.
 *
 * open(idb, spec) resolves with the database, or rejects: as PENDING when the upgrade is blocked by another tab (or the
 * open hangs past OPEN_WAIT_MS, queued behind one), with the browser's error otherwise. spec: { name, version, keys,
 * schema, metaKey }.
 */
window.bwAssetLedgerIdb = (function () {
    'use strict';

    // An open still unsettled after this (queued behind another tab's blocked upgrade) is a pending upgrade too.
    const OPEN_WAIT_MS = 5000;
    const PENDING = 'BlocwerkUpgradePending';

    function request(req) {
        return new Promise((resolve, reject) => {
            req.onsuccess = () => resolve(req.result);
            req.onerror = () => reject(req.error);
        });
    }

    function backend(db) {
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
            clear: store => run(store, 'readwrite', s => s.clear()).then(() => undefined),
            // Read and write in ONE transaction, so no other write lands between them.
            update: (store, key, change) => new Promise((resolve, reject) => {
                const tx = db.transaction(store, 'readwrite');
                const target = tx.objectStore(store);
                let next;
                const read = target.get(key);
                read.onsuccess = () => {
                    next = change(read.result);
                    if (next) {
                        target.put(next);
                    }
                };
                tx.oncomplete = () => resolve(next);
                tx.onerror = () => reject(tx.error);
                tx.onabort = () => reject(tx.error);
            })
        };
    }

    // Only adds: the stores this version lacks, and the schema stamp on a v1 meta row. No row is rewritten or dropped.
    function upgrade(spec, db, tx, oldVersion) {
        for (const [name, keyPath] of Object.entries(spec.keys)) {
            if (!db.objectStoreNames.contains(name)) {
                db.createObjectStore(name, { keyPath });
            }
        }
        if (oldVersion > 0) {
            const meta = tx.objectStore('meta');
            const read = meta.get(spec.metaKey);
            read.onsuccess = () => {
                if (read.result) {
                    meta.put(Object.assign(read.result, { schema: spec.schema }));
                }
            };
        }
    }

    function pendingError() {
        const error = new Error('blocwerk-assets upgrade pending');
        error.name = PENDING;
        return error;
    }

    /**
     * Opens v2. `blocked` (an older tab holds the database open) and an open that hangs past OPEN_WAIT_MS reject as
     * PENDING; the request itself runs on, so the upgrade completes once the other tab closes, and that late
     * connection is closed at once (this page already went to memory).
     */
    function open(idb, spec) {
        return new Promise((resolve, reject) => {
            let settled = false;
            const give = (fn, value) => {
                if (!settled) {
                    settled = true;
                    window.clearTimeout(timer);
                    fn(value);
                }
            };
            const timer = window.setTimeout(() => give(reject, pendingError()), OPEN_WAIT_MS);
            const req = idb.open(spec.name, spec.version);
            req.onupgradeneeded = event => upgrade(spec, req.result, req.transaction, event.oldVersion || 0);
            req.onsuccess = () => {
                const db = req.result;
                db.onversionchange = () => db.close();
                if (settled) {
                    db.close();
                    return;
                }
                give(resolve, db);
            };
            req.onerror = () => give(reject, req.error);
            req.onblocked = () => give(reject, pendingError());
        });
    }

    return { PENDING, OPEN_WAIT_MS, backend, open };
})();
