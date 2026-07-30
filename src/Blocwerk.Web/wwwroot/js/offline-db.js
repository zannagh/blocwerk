/*
 * IndexedDB storage for the offline action queue.
 *
 * localStorage would be simpler but is synchronous, size-capped and has no transactions, so a
 * flush interrupted mid-write can leave a half-updated queue. IndexedDB gives us an atomic
 * read-modify-write per entry and room for a queue that built up over a long offline session.
 *
 * Exposes `window.blocwerkOfflineDb`. Consumed by offline-queue.js only.
 *
 * Entry shape (see offline-queue.js for the semantics):
 *   { id, clientRequestId, kind, payload, dedupeKey, createdAt, attempts, nextAttemptAt, lastError }
 */
(function () {
    const DB_NAME = 'blocwerk-offline';
    const DB_VERSION = 1;
    const STORE = 'actions';

    let dbPromise = null;

    function open() {
        if (dbPromise) {
            return dbPromise;
        }

        dbPromise = new Promise((resolve, reject) => {
            let request;
            try {
                request = indexedDB.open(DB_NAME, DB_VERSION);
            } catch (err) {
                reject(err);
                return;
            }

            request.onupgradeneeded = () => {
                const db = request.result;
                if (!db.objectStoreNames.contains(STORE)) {
                    const store = db.createObjectStore(STORE, { keyPath: 'id', autoIncrement: true });
                    store.createIndex('dedupeKey', 'dedupeKey', { unique: false });
                }
            };

            request.onsuccess = () => resolve(request.result);
            request.onerror = () => reject(request.error);
            request.onblocked = () => reject(new Error('IndexedDB upgrade blocked'));
        });

        // A rejected promise must not be cached forever; a later call should retry the open.
        dbPromise.catch(() => { dbPromise = null; });
        return dbPromise;
    }

    function run(mode, work) {
        return open().then(db => new Promise((resolve, reject) => {
            const tx = db.transaction(STORE, mode);
            const store = tx.objectStore(STORE);
            let result;
            try {
                result = work(store);
            } catch (err) {
                reject(err);
                return;
            }

            tx.oncomplete = () => resolve(result && result.__box ? result.value : result);
            tx.onerror = () => reject(tx.error);
            tx.onabort = () => reject(tx.error);
        }));
    }

    // Wraps an IDBRequest so its result is readable once the transaction completes.
    function capture(request) {
        const box = { __box: true, value: undefined };
        request.onsuccess = () => { box.value = request.result; };
        return box;
    }

    window.blocwerkOfflineDb = {
        available: function () {
            return typeof indexedDB !== 'undefined' && indexedDB !== null;
        },

        add: function (entry) {
            return run('readwrite', store => capture(store.add(entry)));
        },

        put: function (entry) {
            return run('readwrite', store => capture(store.put(entry)));
        },

        remove: function (id) {
            return run('readwrite', store => capture(store.delete(id)));
        },

        // Ordered by autoincrement id, which is insertion order, so flushes stay FIFO.
        all: function () {
            return run('readonly', store => capture(store.getAll()));
        },

        count: function () {
            return run('readonly', store => capture(store.count()));
        },

        clear: function () {
            return run('readwrite', store => capture(store.clear()));
        }
    };
})();
