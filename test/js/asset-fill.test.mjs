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

test('a network failure ends the run as offline, and no worker fetches after the lock is released', async () => {
    let inLock = false;
    const outside = [];
    const r = runner({
        concurrency: () => 2,
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
