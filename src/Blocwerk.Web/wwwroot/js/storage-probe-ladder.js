/*
 * The storage probe's size ladder (window.bwProbe.ladder): for 25 MB up to 1.5 GB, and for the Cache API and
 * OPFS in turn, write that much client-generated data, read it back as a stream, read it back whole into one
 * ArrayBuffer (what a splat parser does), then delete it.
 *
 * Every step is bracketed by two reports ("start", then "ok"/"fail"), and its state is kept in localStorage
 * BEFORE it begins, so when the page is killed mid-step the next visit finds it still "started", reports it as
 * the crash, and never tries that step again at that size or larger (storage-probe-core.js plans this).
 * Leaving the page mid-step is not a crash: pagehide marks it "interrupted" and it is retried.
 */
(function () {
    'use strict';

    const probe = window.bwProbe;
    const core = probe.core;
    const STATE_KEY = 'bw-storage-probe-ladder-v1';
    const CACHE = 'bw-storage-probe-ladder';
    const OPFS_PREFIX = 'bw-storage-probe-ladder-';
    let running = null;

    function load() {
        try {
            return JSON.parse(localStorage.getItem(STATE_KEY)) || { steps: {} };
        } catch (e) {
            return { steps: {} };
        }
    }

    function save(state) {
        localStorage.setItem(STATE_KEY, JSON.stringify(state));
    }

    function url(sizeMb) {
        return '/__storage-probe/ladder/' + sizeMb;
    }

    async function cleanup() {
        if (typeof caches !== 'undefined') {
            await caches.delete(CACHE);
        }
        if (probe.hasOpfs()) {
            try {
                const dir = await navigator.storage.getDirectory();
                for await (const name of dir.keys()) {
                    if (name.startsWith(OPFS_PREFIX)) {
                        await probe.opfsRemove(name);
                    }
                }
            } catch (e) {
                // keys() is missing on some engines; the per-size names are removed after each unit anyway.
            }
        }
    }

    /** Reads a whole stream, checking the pattern at sampled offsets of every chunk; never keeps a chunk. */
    async function drain(stream) {
        const reader = stream.getReader();
        let offset = 0;
        for (;;) {
            const { done, value } = await reader.read();
            if (done) {
                return offset;
            }
            const bad = core.firstMismatch(value, offset, 4);
            if (bad >= 0) {
                throw new Error('pattern mismatch at byte ' + bad);
            }
            offset += value.length;
        }
    }

    function checkBuffer(buffer, size) {
        if (buffer.byteLength !== size) {
            throw new Error('read ' + buffer.byteLength + ' bytes, expected ' + size);
        }
        const bad = core.firstMismatch(new Uint8Array(buffer), 0, 256);
        if (bad >= 0) {
            throw new Error('pattern mismatch at byte ' + bad);
        }
    }

    async function cacheStep(step, sizeMb, size) {
        const cache = await caches.open(CACHE);
        if (step === 'write') {
            await cache.put(url(sizeMb), new Response(core.patternStream(size), { headers: { 'Content-Type': 'application/octet-stream' } }));
            return {};
        }
        const hit = await cache.match(url(sizeMb));
        if (!hit) {
            throw new Error('the cache entry is gone');
        }
        if (step === 'read-stream') {
            const bytes = await drain(hit.body);
            if (bytes !== size) {
                throw new Error('streamed ' + bytes + ' bytes, expected ' + size);
            }
            return {};
        }
        let buffer = await hit.arrayBuffer();
        checkBuffer(buffer, size);
        buffer = null;
        return {};
    }

    async function opfsStep(step, sizeMb, size) {
        const name = OPFS_PREFIX + sizeMb;
        if (step === 'write') {
            const result = await probe.opfsWrite(name, size);
            if (!result.ok) {
                const error = new Error(result.error + (result.writableError ? ' (createWritable: ' + result.writableError + ')' : ''));
                error.unsupported = !!result.unsupported;
                throw error;
            }
            return { method: result.method };
        }
        const file = await probe.opfsFile(name);
        if (!file) {
            throw new Error('the OPFS file is gone');
        }
        if (step === 'read-stream') {
            const bytes = await drain(file.stream());
            if (bytes !== size) {
                throw new Error('streamed ' + bytes + ' bytes, expected ' + size);
            }
            return {};
        }
        let buffer = await file.arrayBuffer();
        checkBuffer(buffer, size);
        buffer = null;
        return {};
    }

    async function runStep(state, family, sizeMb, step, alreadyKnown) {
        const key = core.unitKey(family, sizeMb, step);
        const size = sizeMb * core.MB;
        const tag = 'ladder.' + family + '.' + step + '.' + sizeMb;
        const unsupported = family === 'cache' ? typeof caches === 'undefined' : !probe.hasOpfs();
        if (unsupported) {
            state.steps[key] = { status: 'unsupported' };
            save(state);
            await probe.post(tag, 'unsupported', {});
            probe.log(family + ' ' + step + ' ' + sizeMb + ' MB: unsupported on this device.', 'warn');
            return false;
        }
        const previous = alreadyKnown ? state.steps[key] : null;
        state.steps[key] = { status: 'started', at: new Date().toISOString() };
        running = key;
        save(state);
        await probe.post(tag, 'start', { sizeMb: sizeMb, rewrite: !!previous });
        probe.log(family + ' ' + step + ' ' + sizeMb + ' MB: started...', 'info');
        const started = performance.now();
        try {
            const extra = family === 'cache' ? await cacheStep(step, sizeMb, size) : await opfsStep(step, sizeMb, size);
            const ms = Math.round(performance.now() - started);
            state.steps[key] = previous || { status: 'ok', ms: ms };
            running = null;
            save(state);
            const est = step === 'write' ? await probe.estimate() : {};
            await probe.post(tag, 'ok', Object.assign({ sizeMb: sizeMb, ms: ms, mbPerS: Math.round(sizeMb / (ms / 1000 || 1)), usage: est.usage, quota: est.quota }, extra));
            probe.log(family + ' ' + step + ' ' + sizeMb + ' MB: OK in ' + (ms / 1000).toFixed(1) + ' s.', 'ok');
            return true;
        } catch (e) {
            const isUnsupported = !!e.unsupported;
            // Not `previous`: a rewrite that fails now must end the unit, or its owed reads would loop on it.
            state.steps[key] = { status: isUnsupported ? 'unsupported' : 'fail', error: probe.describe(e) };
            running = null;
            save(state);
            await probe.post(tag, isUnsupported ? 'unsupported' : 'fail', { sizeMb: sizeMb, error: probe.describe(e) });
            probe.log(family + ' ' + step + ' ' + sizeMb + ' MB: ' + (isUnsupported ? 'unsupported' : 'FAILED') + ' - ' + probe.describe(e), 'fail');
            return false;
        }
    }

    async function runUnit(state, unit) {
        const { family, sizeMb, plan } = unit;
        for (const step of core.STEPS) {
            if (plan[step] === 'blocked') {
                state.steps[core.unitKey(family, sizeMb, step)] = { status: 'blocked' };
                save(state);
                await probe.post('ladder.' + family + '.' + step + '.' + sizeMb, 'blocked', { sizeMb: sizeMb });
            }
        }
        if (plan.write === 'run') {
            const known = state.steps[core.unitKey(family, sizeMb, 'write')];
            const wrote = await runStep(state, family, sizeMb, 'write', known && known.status === 'ok');
            if (!wrote) {
                await cleanup();
                return;
            }
        }
        for (const step of ['read-stream', 'read-buffer']) {
            if (plan[step] === 'run') {
                await runStep(state, family, sizeMb, step, false);
            }
        }
        await cleanup();
    }

    /** Runs every owed unit in order; safe to call again after a crash, it skips what is proven. */
    async function run() {
        const state = load();
        await probe.post('ladder', 'begin', { estimate: await probe.estimate(), state: summary(state) });
        await cleanup();
        // Bounded: each unit ends with every step final, so this is ~60 units; the cap only guards a planning bug.
        let guard = 0;
        for (let unit = core.nextUnit(state); unit && guard < 100; unit = core.nextUnit(state)) {
            guard += 1;
            await runUnit(state, unit);
        }
        await probe.post('ladder', 'done', { state: summary(state) });
        const rows = summary(state);
        for (const name of Object.keys(rows).sort()) {
            probe.log(name + ': largest OK ' + rows[name].okMb + ' MB' + (rows[name].stop ? ', stopped by ' + rows[name].stop + ' MB' : '') + '.', 'ok');
        }
        probe.log('Size ladder finished.', 'ok');
    }

    /** Per family and step: the largest size that came back ok, and where it stopped (fail/crashed/unsupported). */
    function summary(state) {
        const out = {};
        for (const [key, entry] of Object.entries(state.steps)) {
            const [name, size] = key.split(':');
            const row = out[name] || (out[name] = { okMb: 0, stop: null });
            if (entry.status === 'ok') {
                row.okMb = Math.max(row.okMb, Number(size));
            } else if (['fail', 'crashed', 'unsupported'].includes(entry.status)) {
                row.stop = entry.status + '@' + size;
            }
        }
        return out;
    }

    /** On load: any step an earlier page left "started" died with that page. Reports and returns them. */
    async function detectCrashes() {
        const state = load();
        const crashed = core.markCrashes(state, new Date().toISOString());
        save(state);
        for (const key of crashed) {
            await probe.post('ladder.crash', 'crashed', { step: key, startedAt: state.steps[key].at });
            probe.log('The page died during ' + key.replace(':', ' at ') + ' MB on the last visit. Recorded as the limit.', 'fail');
        }
        return crashed;
    }

    function reset() {
        localStorage.removeItem(STATE_KEY);
        return cleanup();
    }

    // Leaving mid-step is not a crash; a crash never fires pagehide.
    window.addEventListener('pagehide', () => {
        if (!running) {
            return;
        }
        const state = load();
        if (state.steps[running] && state.steps[running].status === 'started') {
            state.steps[running].status = 'interrupted';
            save(state);
        }
    });

    probe.ladder = { run: run, detectCrashes: detectCrashes, cleanup: cleanup, reset: reset, summary: () => summary(load()) };
})();
