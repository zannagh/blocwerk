// The storage probe's pure helpers (storage-probe-core.js): run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const core = require('../../src/Blocwerk.Web/wwwroot/js/storage-probe-core.js');

const state = steps => ({ steps: Object.fromEntries(Object.entries(steps).map(([k, status]) => [k, { status }])) });

test('the pattern a chunk is filled with is the pattern expectedByte describes, at any offset', () => {
    for (const offset of [0, 4, 4 * 1024 * 1024, 1536 * 1024 * 1024 - 12]) {
        const u8 = core.fillChunk(new Uint8Array(13), offset);
        for (let i = 0; i < u8.length; i++) {
            assert.equal(u8[i], core.expectedByte(offset + i), `offset ${offset} + ${i}`);
        }
    }
});

test('firstMismatch passes good data and finds a corrupted sample, reporting its absolute offset', () => {
    const base = 8 * 1024 * 1024;
    const u8 = core.fillChunk(new Uint8Array(4096), base);
    assert.equal(core.firstMismatch(u8, base, 64), -1);
    assert.notEqual(core.firstMismatch(u8, base + 4, 64), -1, 'a shifted block must not pass');
    u8[u8.length - 1] ^= 0xff;
    assert.equal(core.firstMismatch(u8, base, 64), base + u8.length - 1);
});

test('patternStream yields exactly size bytes, in chunks, that verify', async () => {
    const size = 10 * 1024 + 3;
    const reader = core.patternStream(size, 4096).getReader();
    let offset = 0;
    for (;;) {
        const { done, value } = await reader.read();
        if (done) {
            break;
        }
        assert.ok(value.length <= 4096);
        assert.equal(core.firstMismatch(value, offset, 16), -1);
        offset += value.length;
    }
    assert.equal(offset, size);
});

test('a fresh ladder starts with the Cache API at 25 MB, then OPFS at 25 MB', () => {
    const first = core.nextUnit({ steps: {} });
    assert.deepEqual([first.family, first.sizeMb], ['cache', 25]);
    assert.deepEqual(first.plan, { write: 'run', 'read-stream': 'run', 'read-buffer': 'run' });
    const second = core.nextUnit(state({ 'cache-write:25': 'ok', 'cache-read-stream:25': 'ok', 'cache-read-buffer:25': 'ok' }));
    assert.deepEqual([second.family, second.sizeMb], ['opfs', 25]);
});

test('a crash in the whole-buffer read stops only that step at that size and above', () => {
    const s = state({ 'cache-write:600': 'ok', 'cache-read-stream:600': 'ok', 'cache-read-buffer:600': 'crashed' });
    assert.deepEqual(core.unitPlan(s, 'cache', 600), { write: null, 'read-stream': null, 'read-buffer': null });
    assert.deepEqual(core.unitPlan(s, 'cache', 800), { write: 'run', 'read-stream': 'run', 'read-buffer': 'blocked' });
    assert.deepEqual(core.unitPlan(s, 'opfs', 800), { write: 'run', 'read-stream': 'run', 'read-buffer': 'run' });
});

test('a reopened unit whose read is still owed writes its data again', () => {
    const s = state({ 'opfs-write:200': 'ok', 'opfs-read-stream:200': 'ok' });
    assert.deepEqual(core.unitPlan(s, 'opfs', 200), { write: 'run', 'read-stream': null, 'read-buffer': 'run' });
});

test('a failed or unsupported write blocks its reads and every larger size, so the plan always ends', () => {
    const s = state({ 'opfs-write:25': 'unsupported' });
    assert.deepEqual(core.unitPlan(s, 'opfs', 25), { write: null, 'read-stream': 'blocked', 'read-buffer': 'blocked' });
    assert.deepEqual(core.unitPlan(s, 'opfs', 1536), { write: 'blocked', 'read-stream': 'blocked', 'read-buffer': 'blocked' });

    // Drive a whole ladder where every step answers at once: it must finish, in a bounded number of units.
    const run = { steps: {} };
    let units = 0;
    for (let unit = core.nextUnit(run); unit; unit = core.nextUnit(run)) {
        units += 1;
        assert.ok(units < 100, 'the plan loops');
        for (const step of core.STEPS) {
            if (unit.plan[step]) {
                const dies = unit.family === 'cache' && step === 'write' && unit.sizeMb >= 800;
                run.steps[core.unitKey(unit.family, unit.sizeMb, step)] = { status: unit.plan[step] === 'blocked' ? 'blocked' : dies ? 'fail' : 'ok' };
            }
        }
    }
    assert.equal(units, core.SIZES_MB.length * core.FAMILIES.length);
    assert.equal(run.steps['cache-read-buffer:1536'].status, 'blocked');
    assert.equal(run.steps['opfs-read-buffer:1536'].status, 'ok');
});

test('markCrashes turns a left-over "started" into a crash and forgets an "interrupted" step', () => {
    const s = state({ 'cache-write:400': 'started', 'opfs-read-stream:50': 'interrupted', 'cache-write:25': 'ok' });
    assert.deepEqual(core.markCrashes(s, '2026-10-21T12:00:00Z'), ['cache-write:400']);
    assert.equal(s.steps['cache-write:400'].status, 'crashed');
    assert.equal(s.steps['opfs-read-stream:50'], undefined);
    assert.equal(s.steps['cache-write:25'].status, 'ok');
});

test('run ids name the context and the local time, and fit the server token pattern', () => {
    const date = new Date(2026, 9, 21, 14, 32);
    assert.match(core.makeRunId(true, date, 0.5), /^A-1021-1432-[0-9a-z]{4}$/);
    assert.match(core.makeRunId(false, date, 0), /^T-1021-1432-0000$/);
});

test('fitReport keeps a report under the cap, trimming long strings first', () => {
    const small = { run: 'r', data: { a: 1 } };
    assert.equal(core.fitReport(small, 7600), JSON.stringify(small));
    const long = core.fitReport({ run: 'r', data: { ua: 'x'.repeat(9000), n: 2 } }, 7600);
    assert.ok(long.length <= 7600);
    assert.equal(JSON.parse(long).data.n, 2);
    const many = core.fitReport({ run: 'r', data: Object.fromEntries(Array.from({ length: 200 }, (_, i) => ['k' + i, 'y'.repeat(200)])) }, 7600);
    assert.ok(many.length <= 7600);
    assert.equal(JSON.parse(many).data.trimmed, true);
});

test('ageDays and formatBytes', () => {
    const now = Date.parse('2026-10-29T12:00:00Z');
    assert.equal(core.ageDays('2026-10-21T12:00:00Z', now), 8);
    assert.equal(core.ageDays(null, now), null);
    assert.equal(core.formatBytes(1536 * core.MB), '1.50 GB');
    assert.equal(core.formatBytes(25 * core.MB), '25.0 MB');
    assert.equal(core.formatBytes(undefined), 'unknown');
});
