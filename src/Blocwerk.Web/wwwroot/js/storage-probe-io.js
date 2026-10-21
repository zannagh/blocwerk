/*
 * The storage probe's plumbing (window.bwProbe): the run id and context, the progress beacon to
 * POST /api/probe/storage, the on-screen log, and the OPFS / Cache API / IndexedDB primitives the tests
 * share. The tests themselves are in storage-probe-checks.js and storage-probe-ladder.js.
 */
(function () {
    'use strict';

    const core = window.bwStorageProbeCore;
    const ENDPOINT = '/api/probe/storage';
    const standalone = !!((window.matchMedia && window.matchMedia('(display-mode: standalone)').matches)
        || window.navigator.standalone === true);

    const probe = {
        core: core,
        standalone: standalone,
        ctx: standalone ? 'standalone' : 'tab',
        runId: core.makeRunId(standalone, new Date()),
        seq: 0,
        worker: null,
    };

    function describe(e) {
        return (e && (e.name ? e.name + ': ' : '') + (e.message || String(e))) || 'unknown error';
    }

    /** One line in the on-screen log; kind is ok | fail | info | warn. */
    function log(text, kind) {
        const list = document.getElementById('probe-log');
        if (!list) {
            return;
        }
        const item = document.createElement('li');
        item.className = 'probe-line probe-' + (kind || 'info');
        const time = new Date().toLocaleTimeString();
        item.textContent = time + '  ' + text;
        list.prepend(item);
    }

    /**
     * Sends one report and waits (up to 5 s) for the server to take it, so a "started" line is in the log
     * BEFORE the step that may kill the page begins. keepalive lets a report sent while the page is going
     * away still arrive. Resolves to the HTTP status, or 0 when nothing came back.
     */
    async function post(step, result, data) {
        probe.seq += 1;
        const body = core.fitReport({ run: probe.runId, ctx: probe.ctx, seq: probe.seq, step: step, result: result, data: data || {} });
        const controller = typeof AbortController === 'function' ? new AbortController() : null;
        const timer = controller ? setTimeout(() => controller.abort(), 5000) : null;
        try {
            const response = await fetch(ENDPOINT, {
                method: 'POST',
                credentials: 'same-origin',
                keepalive: true,
                headers: { 'Content-Type': 'application/json' },
                body: body,
                signal: controller ? controller.signal : undefined,
            });
            if (response.status !== 204) {
                log('The server did not take report ' + step + ' (HTTP ' + response.status + ').', 'warn');
            }
            return response.status;
        } catch (e) {
            log('Report ' + step + ' did not reach the server: ' + describe(e), 'warn');
            return 0;
        } finally {
            if (timer) {
                clearTimeout(timer);
            }
        }
    }

    async function estimate() {
        try {
            if (!navigator.storage || !navigator.storage.estimate) {
                return { unsupported: true };
            }
            const e = await navigator.storage.estimate();
            return { quota: e.quota, usage: e.usage, details: e.usageDetails || null };
        } catch (e) {
            return { error: describe(e) };
        }
    }

    function hasOpfs() {
        return !!(navigator.storage && typeof navigator.storage.getDirectory === 'function');
    }

    function hasCreateWritable() {
        return typeof FileSystemFileHandle !== 'undefined'
            && typeof FileSystemFileHandle.prototype.createWritable === 'function';
    }

    /** The OPFS worker, started once; null when workers cannot run it. Every call is { id, op, ... }. */
    function worker() {
        if (probe.worker) {
            return probe.worker;
        }
        const page = document.getElementById('storage-probe');
        let w;
        try {
            w = new Worker(new URL(page.dataset.worker, document.baseURI).href);
        } catch (e) {
            return null;
        }
        const pending = new Map();
        let next = 0;
        w.onmessage = event => {
            const resolve = pending.get(event.data.id);
            pending.delete(event.data.id);
            if (resolve) {
                resolve(event.data);
            }
        };
        w.onerror = event => {
            for (const resolve of pending.values()) {
                resolve({ ok: false, error: 'worker error: ' + (event.message || 'unknown') });
            }
            pending.clear();
        };
        const call = msg => new Promise(resolve => {
            next += 1;
            pending.set(next, resolve);
            w.postMessage(Object.assign({ id: next }, msg));
        });
        // Absolute: importScripts resolves a relative URL against the WORKER's, which sits under /js/.
        probe.worker = { call: call, ready: call({ op: 'init', coreUrl: new URL(page.dataset.core, document.baseURI).href }) };
        return probe.worker;
    }

    /** Writes `size` pattern bytes (or `text`) to an OPFS file: createWritable here, else the worker. */
    async function opfsWrite(name, size, text) {
        if (!hasOpfs()) {
            return { ok: false, unsupported: true, error: 'no navigator.storage.getDirectory' };
        }
        let writableError = null;
        if (hasCreateWritable()) {
            try {
                return await writeWithWritable(name, size, text);
            } catch (e) {
                // Seen in WebKit: createWritable exists but the write fails; the worker's handle may still work.
                writableError = describe(e);
                await opfsRemove(name);
            }
        }
        const reply = await writeInWorker(name, size, text);
        if (writableError) {
            reply.writableError = writableError;
        }
        return reply;
    }

    async function writeWithWritable(name, size, text) {
        const started = performance.now();
        const dir = await navigator.storage.getDirectory();
        const file = await dir.getFileHandle(name, { create: true });
        const writable = await file.createWritable();
        if (text !== undefined) {
            await writable.write(text);
            await writable.close();
        } else {
            await core.patternStream(size).pipeTo(writable);
        }
        return { ok: true, method: 'createWritable', bytes: size, ms: Math.round(performance.now() - started) };
    }

    async function writeInWorker(name, size, text) {
        const w = worker();
        if (!w) {
            return { ok: false, unsupported: true, error: 'no createWritable and no worker' };
        }
        const init = await w.ready;
        if (!init.ok || !init.syncAccessHandle) {
            return { ok: false, unsupported: true, error: 'no createWritable and no createSyncAccessHandle' };
        }
        const reply = text !== undefined
            ? await w.call({ op: 'writeText', name: name, text: text })
            : await w.call({ op: 'write', name: name, size: size });
        reply.method = 'syncAccessHandle';
        if (!reply.ok && /unsupported/.test(reply.error || '')) {
            reply.unsupported = true;
        }
        return reply;
    }

    /** The OPFS file, or null when it (or OPFS) is not there. */
    async function opfsFile(name) {
        if (!hasOpfs()) {
            return null;
        }
        try {
            const dir = await navigator.storage.getDirectory();
            return await (await dir.getFileHandle(name)).getFile();
        } catch (e) {
            return null;
        }
    }

    async function opfsRemove(name) {
        try {
            const dir = await navigator.storage.getDirectory();
            await dir.removeEntry(name);
        } catch (e) {
            // Already gone, or OPFS is missing: nothing to clean up either way.
        }
    }

    /** The probe's IndexedDB store, opened (and created) on demand. */
    function idb() {
        return new Promise((resolve, reject) => {
            if (typeof indexedDB === 'undefined') {
                reject(new Error('unsupported: no indexedDB'));
                return;
            }
            const request = indexedDB.open('bw-storage-probe', 1);
            request.onupgradeneeded = () => request.result.createObjectStore('markers');
            request.onsuccess = () => resolve(request.result);
            request.onerror = () => reject(request.error);
        });
    }

    function idbDo(mode, work) {
        return idb().then(db => new Promise((resolve, reject) => {
            const tx = db.transaction('markers', mode);
            const request = work(tx.objectStore('markers'));
            tx.oncomplete = () => {
                db.close();
                resolve(request.result);
            };
            tx.onerror = () => {
                db.close();
                reject(tx.error);
            };
        }));
    }

    Object.assign(probe, {
        describe: describe,
        log: log,
        post: post,
        estimate: estimate,
        hasOpfs: hasOpfs,
        hasCreateWritable: hasCreateWritable,
        worker: null,
        startWorker: worker,
        opfsWrite: opfsWrite,
        opfsFile: opfsFile,
        opfsRemove: opfsRemove,
        idbPut: (key, value) => idbDo('readwrite', store => store.put(value, key)),
        idbGet: key => idbDo('readonly', store => store.get(key)),
    });
    window.bwProbe = probe;
})();
