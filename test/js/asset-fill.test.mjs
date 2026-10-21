// The background fill's runner (asset-fill.js): how a run ends, releases its lock and treats refusals. Run by
// `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { loadPage } from './fixtures/page-sandbox.mjs';

const { bwAssetFill: Fill } = loadPage().win;
const ok = () => ({ ok: true, body: null, arrayBuffer: async () => new ArrayBuffer(1) });
const tasks = (...urls) => urls.map(u => ({ key: u, url: u }));

function exclusiveLock() {
    const lock = { held: false };
    lock.run = async fn => {
        if (lock.held) {
            return null;
        }
        lock.held = true;
        try {
            return await fn();
        } finally {
            lock.held = false;
        }
    };
    return lock;
}

function runner(env) {
    return Fill.createRunner(Object.assign({
        fetch: async () => ok(), verify: async () => 'stored', partition: () => 'p', hidden: () => false,
        concurrency: () => 1, freeOneWall: async () => false, lock: fn => fn(),
    }, env));
}

test('a hidden page ends its run and releases the lock, so another tab can fill', async () => {
    const lock = exclusiveLock();
    let fetched = 0;
    const hidden = runner({ hidden: () => true, lock: lock.run, fetch: async () => { fetched++; return ok(); } });
    assert.equal(await hidden.run(tasks('/a')), 'hidden');
    assert.equal(fetched, 0);
    assert.equal(lock.held, false);
    assert.equal(await runner({ lock: lock.run }).run(tasks('/b')), 'done');
});

test('a run that cannot get the lock answers locked', async () => {
    const lock = exclusiveLock();
    lock.held = true;
    assert.equal(await runner({ lock: lock.run }).run(tasks('/a')), 'locked');
});

test('a network failure while offline ends the run as offline, and no worker fetches after the lock is released', async () => {
    let inLock = false;
    const outside = [];
    const r = runner({
        concurrency: () => 2, online: () => false,
        lock: async fn => { inLock = true; try { return await fn(); } finally { inLock = false; } },
        fetch: async url => {
            if (!inLock) {
                outside.push(url);
            }
            if (url === '/1') {
                throw new TypeError('Failed to fetch');
            }
            await new Promise(resolve => setTimeout(resolve, 5));
            return ok();
        },
    });
    assert.equal(await r.run(tasks('/0', '/1', '/2', '/3', '/4')), 'offline');
    await new Promise(resolve => setTimeout(resolve, 30));
    assert.deepEqual(outside, []);
    assert.equal(r.streaming, false);
});

test('a write the worker never confirmed (a timeout) is not a refusal: nothing is freed, the fill only pauses', async () => {
    let freed = 0;
    let fetched = 0;
    const r = runner({ verify: async () => 'unknown', freeOneWall: async () => { freed++; return true; }, fetch: async () => { fetched++; return ok(); } });
    assert.equal(await r.run(tasks('/a', '/b', '/c', '/d', '/e')), 'unsure');
    assert.equal(fetched, Fill.UNSURE_LIMIT);
    assert.equal(freed, 0);
    assert.equal(r.streaming, false);
});

test('a confirmed refusal frees one wall and retries once, then streams for good', async () => {
    let freed = 0;
    let fetched = 0;
    const r = runner({ verify: async () => 'refused', freeOneWall: async () => { freed++; return true; }, fetch: async () => { fetched++; return ok(); } });
    assert.equal(await r.run(tasks('/a', '/b')), 'streaming');
    assert.deepEqual([freed, fetched], [1, 2]);
    assert.equal(await r.run(tasks('/c')), 'streaming');
    assert.equal(fetched, 2);
});

test('a body is drained chunk by chunk and never buffered whole', async () => {
    let chunks = 0;
    const body = new ReadableStream({
        pull(controller) {
            chunks++;
            if (chunks > 3) {
                controller.close();
            } else {
                controller.enqueue(new Uint8Array(1024));
            }
        },
    });
    const response = { ok: true, body, arrayBuffer: async () => assert.fail('buffered the whole body') };
    assert.equal(await runner({ fetch: async () => response }).run(tasks('/a')), 'done');
    assert.equal(chunks, 4);
});

test('a fetch that fails while online is that asset\'s failure: the run goes on, and after 3 the asset is skipped for good', async () => {
    const fetched = [];
    const r = runner({ online: () => true, fetch: async url => { fetched.push(url); if (url === '/bad') { throw new TypeError('reset'); } return ok(); } });
    for (let i = 0; i < 4; i++) {
        assert.equal(await r.run(tasks('/bad', '/good')), 'done');
    }
    assert.equal(fetched.filter(u => u === '/bad').length, Fill.FAILURE_LIMIT);
    assert.equal(fetched.filter(u => u === '/good').length, 4);
});

test('an earlier announcement of a key is forgotten before its fetch', async () => {
    const seen = new Map([['/a', 'stored']]);
    let verified;
    const r = runner({ forget: task => seen.delete(task.key), verify: async task => { verified = seen.get(task.key) || 'unknown'; return verified; } });
    await r.run(tasks('/a'));
    assert.equal(verified, 'unknown');
});

test('unsure retries back off 30 s, 1, 2, 4 min, and the page stops filling after 5; locked waits 30 s; done resets', () => {
    const waits = [];
    const win = { setTimeout: (fn, ms) => { waits.push(ms); return fn; }, document: { addEventListener() {} }, addEventListener() {} };
    const holder = {};
    for (let i = 0; i < 5; i++) {
        holder.retry = null;
        Fill.resumeAfter(win, i % 2 ? 'mismatch' : 'unsure', () => {}, holder);
    }
    assert.deepEqual(waits, [30000, 60000, 120000, 240000]);
    assert.equal(holder.stopped, true);
    assert.equal(Fill.backoffMs(10), 15 * 60 * 1000);
    const other = { unsureRuns: 3 };
    Fill.resumeAfter(win, 'done', () => {}, other);
    assert.equal(other.unsureRuns, 0);
    Fill.resumeAfter(win, 'locked', () => {}, other);
    assert.equal(waits.at(-1), 30000);
});
