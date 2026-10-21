// The worker's answer to `sw-refresh-partition` (sw-asset-cache.js, service-worker.js): run by `node --test test/js/`.
// The page waits for `partition-refreshed` so a page whose worker served another identity can fill at once.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { loadWorker, response } from './fixtures/sw-sandbox.mjs';

const ALICE = 'u-' + 'a'.repeat(20);
const BOB = 'u-' + 'b'.repeat(20);
const PARTITION = '/api/me/cache-partition';

function refresh(worker) {
    const sent = [];
    let pending;
    worker.listeners.message({
        data: { proto: 1, type: 'sw-refresh-partition' }, ports: [{ postMessage: m => sent.push(m) }],
        source: { id: 'tab-1' }, waitUntil: p => { pending = p; }
    });
    return pending.then(() => sent);
}

test('a refresh is answered with partition-refreshed and the label the worker now serves', async () => {
    let label = ALICE;
    const worker = loadWorker({ routes: { [PARTITION]: () => response(200, JSON.stringify({ partition: label }), { 'content-type': 'application/json' }) } });
    assert.deepEqual((await refresh(worker)).map(m => ({ ...m })), [{ proto: 1, type: 'partition-refreshed', partition: ALICE }]);
    label = BOB;
    assert.deepEqual((await refresh(worker)).map(m => ({ ...m })), [{ proto: 1, type: 'partition-refreshed', partition: BOB }]);
});

test('an unreachable server still gets an answer: the label the worker keeps serving', async () => {
    const worker = loadWorker({ routes: { [PARTITION]: () => { throw new TypeError('Failed to fetch'); } } });
    const [reply] = await refresh(worker);
    assert.equal(reply.type, 'partition-refreshed');
    assert.notEqual(reply.partition, BOB);
});
