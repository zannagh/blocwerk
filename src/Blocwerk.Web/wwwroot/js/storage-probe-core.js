/*
 * Pure helpers of the on-device storage probe (/storage-probe), with no DOM and no storage:
 *
 *  - the test data: a deterministic byte pattern, generated in chunks, that a read-back can verify at
 *    sampled offsets without keeping a second copy in memory;
 *  - the size ladder's plan: which (family, size) unit runs next, which steps of it are still owed, and
 *    which earlier step a reopened page must call a crash (it said "started" and never finished);
 *  - run ids, byte formatting, and trimming a report to the server's 8 KB cap.
 *
 * Loaded as a plain script (self.bwStorageProbeCore, also inside the OPFS worker) and by node
 * (module.exports) for test/js/storage-probe.test.mjs.
 */
(function (root, factory) {
    'use strict';
    const api = factory();
    if (typeof module === 'object' && module.exports) {
        module.exports = api;
    } else {
        root.bwStorageProbeCore = api;
    }
})(typeof self !== 'undefined' ? self : this, function () {
    'use strict';

    const MB = 1024 * 1024;
    const SIZES_MB = [25, 50, 100, 200, 300, 400, 600, 800, 1024, 1536];
    const FAMILIES = ['cache', 'opfs'];
    const STEPS = ['write', 'read-stream', 'read-buffer'];
    const CHUNK = 4 * MB;
    const FINAL = { ok: true, fail: true, crashed: true, blocked: true, unsupported: true };
    const DEAD = ['fail', 'crashed', 'blocked', 'unsupported'];

    /** The 32-bit word at word index w: a multiplicative hash, so a shifted or stale block never matches. */
    function patternWord(w) {
        return Math.imul(w + 1, 2654435761) >>> 0;
    }

    /** The byte at absolute offset i (little-endian words; every device the probe targets is). */
    function expectedByte(i) {
        return (patternWord(Math.floor(i / 4)) >>> ((i % 4) * 8)) & 255;
    }

    /** Fills u8 (whose first byte sits at absolute offset `offset`, a multiple of 4) with the pattern. */
    function fillChunk(u8, offset) {
        const words = u8.length >>> 2;
        const view = new Uint32Array(u8.buffer, u8.byteOffset, words);
        const base = offset / 4;
        for (let k = 0; k < words; k++) {
            view[k] = Math.imul(base + k + 1, 2654435761);
        }
        for (let i = words * 4; i < u8.length; i++) {
            u8[i] = expectedByte(offset + i);
        }
        return u8;
    }

    /** A pull-based stream of `size` pattern bytes in CHUNK pieces: nothing is held beyond one chunk. */
    function patternStream(size, chunk) {
        const step = chunk || CHUNK;
        let offset = 0;
        return new ReadableStream({
            pull(controller) {
                if (offset >= size) {
                    controller.close();
                    return;
                }
                const length = Math.min(step, size - offset);
                controller.enqueue(fillChunk(new Uint8Array(length), offset));
                offset += length;
            },
        }, { highWaterMark: 1 });
    }

    /**
     * The first sampled offset in u8 (absolute offset `base`) that is not the pattern, or -1. Samples the
     * first and last byte and `count` points between, so a read of a gigabyte costs microseconds to check.
     */
    function firstMismatch(u8, base, count) {
        const n = u8.length;
        if (n === 0) {
            return -1;
        }
        const samples = Math.max(2, count || 64);
        for (let s = 0; s < samples; s++) {
            const i = s === samples - 1 ? n - 1 : Math.floor((n / samples) * s);
            if (u8[i] !== expectedByte(base + i)) {
                return base + i;
            }
        }
        return -1;
    }

    function unitKey(family, sizeMb, step) {
        return family + '-' + step + ':' + sizeMb;
    }

    function status(state, family, sizeMb, step) {
        const entry = state && state.steps && state.steps[unitKey(family, sizeMb, step)];
        return entry ? entry.status : null;
    }

    /** True when a smaller size of this family's step already failed, crashed or was unsupported: larger ones are moot. */
    function blockedBelow(state, family, sizeMb, step) {
        return SIZES_MB.some(s => s < sizeMb && DEAD.includes(status(state, family, s, step)));
    }

    /**
     * What a unit still owes: each step with `run` (do it), `blocked` (a smaller size of it already died,
     * or the write it reads from did) or null (already has a final answer). The write always runs again
     * when a read is owed, because every unit deletes its data when it ends.
     */
    function unitPlan(state, family, sizeMb) {
        const plan = {};
        const writeDead = blockedBelow(state, family, sizeMb, 'write')
            || DEAD.includes(status(state, family, sizeMb, 'write'));
        for (const step of STEPS) {
            const known = status(state, family, sizeMb, step);
            if (known && FINAL[known]) {
                plan[step] = null;
            } else if (writeDead || blockedBelow(state, family, sizeMb, step)) {
                plan[step] = 'blocked';
            } else {
                plan[step] = 'run';
            }
        }
        if (plan['read-stream'] === 'run' || plan['read-buffer'] === 'run') {
            plan.write = 'run';
        }
        return plan;
    }

    /** The next unit with any step to run or to mark blocked, smallest size first, cache before OPFS; or null. */
    function nextUnit(state, families) {
        for (const sizeMb of SIZES_MB) {
            for (const family of families || FAMILIES) {
                const plan = unitPlan(state, family, sizeMb);
                if (STEPS.some(step => plan[step] !== null)) {
                    return { family: family, sizeMb: sizeMb, plan: plan };
                }
            }
        }
        return null;
    }

    /**
     * A step left "started" by an earlier page is a crash: the page died mid-step (an "interrupted" one was
     * left on purpose, by pagehide, and is simply retried). Marks them and returns their keys.
     */
    function markCrashes(state, now) {
        const crashed = [];
        for (const key of Object.keys((state && state.steps) || {})) {
            const entry = state.steps[key];
            if (entry.status === 'started') {
                entry.status = 'crashed';
                entry.detectedAt = now;
                crashed.push(key);
            } else if (entry.status === 'interrupted') {
                delete state.steps[key];
            }
        }
        return crashed;
    }

    /** "T-1021-1432-k3f9": context letter, local month/day, hour/minute, and four random characters. */
    function makeRunId(standalone, date, random) {
        const pad = n => String(n).padStart(2, '0');
        const tail = Math.floor((random === undefined ? Math.random() : random) * 1679616).toString(36).padStart(4, '0');
        return (standalone ? 'A' : 'T') + '-' + pad(date.getMonth() + 1) + pad(date.getDate())
            + '-' + pad(date.getHours()) + pad(date.getMinutes()) + '-' + tail;
    }

    function formatBytes(bytes) {
        if (typeof bytes !== 'number' || !isFinite(bytes)) {
            return 'unknown';
        }
        if (bytes >= 1024 * MB) {
            return (bytes / (1024 * MB)).toFixed(2) + ' GB';
        }
        return (bytes / MB).toFixed(bytes >= 100 * MB ? 0 : 1) + ' MB';
    }

    /**
     * The JSON body for a report, trimmed under maxBytes: long strings in data are cut first, then data is
     * replaced by a note. The server caps at 8 KB and refuses anything larger outright.
     */
    function fitReport(report, maxBytes) {
        const limit = maxBytes || 7600;
        const bytes = text => (typeof TextEncoder === 'function' ? new TextEncoder().encode(text).length : text.length * 3);
        let body = JSON.stringify(report);
        if (bytes(body) <= limit) {
            return body;
        }
        const data = {};
        for (const [k, v] of Object.entries(report.data || {})) {
            data[k] = typeof v === 'string' && v.length > 300 ? v.slice(0, 300) + '...' : v;
        }
        body = JSON.stringify(Object.assign({}, report, { data: data }));
        if (bytes(body) <= limit) {
            return body;
        }
        return JSON.stringify(Object.assign({}, report, { data: { trimmed: true, bytes: bytes(body) } }));
    }

    /** Days (one decimal) between an ISO time and now, or null when the marker is missing or unreadable. */
    function ageDays(iso, now) {
        const at = iso ? Date.parse(iso) : NaN;
        return isNaN(at) ? null : Math.round(((now - at) / 86400000) * 10) / 10;
    }

    return {
        MB: MB,
        SIZES_MB: SIZES_MB,
        FAMILIES: FAMILIES,
        STEPS: STEPS,
        CHUNK: CHUNK,
        patternWord: patternWord,
        expectedByte: expectedByte,
        fillChunk: fillChunk,
        patternStream: patternStream,
        firstMismatch: firstMismatch,
        unitKey: unitKey,
        unitPlan: unitPlan,
        nextUnit: nextUnit,
        markCrashes: markCrashes,
        makeRunId: makeRunId,
        formatBytes: formatBytes,
        fitReport: fitReport,
        ageDays: ageDays,
    };
});
