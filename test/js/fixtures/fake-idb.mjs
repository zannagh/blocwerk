// A small in-memory IndexedDB for the ledger tests: versioned opens with `upgradeneeded`, `versionchange` sent to the
// other connections, `blocked` while one of them stays open (the open then waits and upgrades once it closes), a
// VersionError for an older version, one open at a time per database (a later open queues behind a blocked one), and
// object stores with (composite) key paths whose requests settle on later event-loop turns, as in a browser. Only what
// asset-ledger.js calls.
const turn = () => new Promise(resolve => setImmediate(resolve));

function domError(name) {
    const error = new Error(name);
    error.name = name;
    return error;
}

function keyOf(keyPath, row) {
    return JSON.stringify(Array.isArray(keyPath) ? keyPath.map(p => row[p]) : row[keyPath]);
}

function makeRequest() {
    return { result: undefined, error: null, onsuccess: null, onerror: null, onblocked: null, onupgradeneeded: null, transaction: null };
}

// One transaction: every request runs on its own later turn; complete fires once a turn passes with none left.
function makeTransaction(data, db, mode) {
    let open = 0;
    const tx = { mode, oncomplete: null, onerror: null, onabort: null, error: null };
    const settle = () => setImmediate(() => {
        if (open === 0 && !tx.done) {
            tx.done = true;
            setImmediate(() => tx.oncomplete && tx.oncomplete());
        }
    });
    const op = work => {
        const req = makeRequest();
        open++;
        setImmediate(() => {
            try {
                req.result = work();
                req.onsuccess && req.onsuccess({ target: req });
            } catch (e) {
                req.error = e;
                req.onerror && req.onerror({ target: req });
            }
            open--;
            settle();
        });
        return req;
    };
    tx.objectStore = name => {
        const store = data.stores.get(name);
        if (!store) {
            throw domError('NotFoundError');
        }
        if (mode === 'readonly') {
            const read = { get: k => op(() => structuredClone(store.rows.get(JSON.stringify(k)))), getAll: () => op(() => [...store.rows.values()].map(r => structuredClone(r))) };
            return read;
        }
        return {
            get: k => op(() => structuredClone(store.rows.get(JSON.stringify(k)))),
            getAll: () => op(() => [...store.rows.values()].map(r => structuredClone(r))),
            put: row => op(() => { store.rows.set(keyOf(store.keyPath, row), structuredClone(row)); }),
            delete: k => op(() => { store.rows.delete(JSON.stringify(k)); }),
            clear: () => op(() => { store.rows.clear(); }),
        };
    };
    settle();
    return tx;
}

function makeConnection(data, version) {
    const db = { version, closed: false, onversionchange: null };
    db.objectStoreNames = { contains: name => data.stores.has(name) };
    db.createObjectStore = (name, options) => {
        data.stores.set(name, { keyPath: options.keyPath, rows: new Map() });
        return {};
    };
    db.transaction = (names, mode = 'readonly') => {
        if (db.closed) {
            throw domError('InvalidStateError');
        }
        return makeTransaction(data, db, mode);
    };
    db.close = () => {
        db.closed = true;
        data.connections.delete(db);
        data.wake();
    };
    return db;
}

export function fakeIndexedDB() {
    const dbs = new Map();
    const dataOf = name => {
        if (!dbs.has(name)) {
            const data = { version: 0, stores: new Map(), connections: new Set(), queue: Promise.resolve(), waiters: [] };
            data.wake = () => data.waiters.splice(0).forEach(fn => fn());
            dbs.set(name, data);
        }
        return dbs.get(name);
    };
    const allClosed = (data, except) => new Promise(resolve => {
        const check = () => ([...data.connections].every(c => c === except) ? resolve() : data.waiters.push(check));
        check();
    });

    async function process(data, req, version) {
        await turn();
        const target = version ?? Math.max(1, data.version);
        if (target < data.version) {
            req.error = domError('VersionError');
            req.onerror && req.onerror({ target: req });
            return;
        }
        if (target > data.version) {
            const oldVersion = data.version;
            for (const other of [...data.connections]) {
                other.onversionchange && other.onversionchange({ oldVersion, newVersion: target });
            }
            await turn();
            if (data.connections.size > 0) {
                req.onblocked && req.onblocked({ oldVersion, newVersion: target });
                await allClosed(data);
            }
            data.version = target;
            const db = makeConnection(data, target);
            data.connections.add(db);
            req.result = db;
            req.transaction = makeTransaction(data, db, 'versionchange');
            const done = new Promise(resolve => { req.transaction.oncomplete = resolve; });
            req.onupgradeneeded && req.onupgradeneeded({ target: req, oldVersion, newVersion: target });
            await done;
            req.transaction = null;
            req.onsuccess && req.onsuccess({ target: req });
            return;
        }
        const db = makeConnection(data, target);
        data.connections.add(db);
        req.result = db;
        req.onsuccess && req.onsuccess({ target: req });
    }

    return {
        dbs,
        open(name, version) {
            const data = dataOf(name);
            const req = makeRequest();
            data.queue = data.queue.then(() => process(data, req, version));
            return req;
        },
        // What a store holds, read outside any connection (for asserts).
        rows: (name, store) => [...(dataOf(name).stores.get(store)?.rows.values() || [])].map(r => structuredClone(r)),
        version: name => dataOf(name).version,
        storeNames: name => [...dataOf(name).stores.keys()].sort(),
        connections: name => dataOf(name).connections.size,
        // Every open connection goes, as when its tabs close (a Release 1/2 tab never closes on its own).
        closeAll: name => [...dataOf(name).connections].forEach(c => c.close()),
    };
}
