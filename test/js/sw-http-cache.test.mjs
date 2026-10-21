// The worker and the browser's HTTP cache (sw-asset-cache.js): run by `node --test test/js/`. The HTTP cache is one per
// browser, not one per identity, so a miss must never be answered from it; and the /a/ routes now send
// `private, no-cache` (never immutable), which the worker must still store.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { loadWorker, dispatchFetch, response, ORIGIN } from './fixtures/sw-sandbox.mjs';

const SHA = 'ef'.repeat(32);
const ALICE = 'u-' + 'a'.repeat(20);
const PATH = `/api/walls/w1/a/${SHA}/640`;

function worker(cacheControl) {
    return loadWorker({
        routes: {
            '/api/me/cache-partition': () => response(200, JSON.stringify({ partition: ALICE }), { 'content-type': 'application/json' }),
            [PATH]: () => response(200, 'jpeg', {
                'content-type': 'image/jpeg', 'content-length': '4', etag: `"${SHA}-640"`, 'cache-control': cacheControl
            })
        }
    });
}

test('a miss goes to the network past the HTTP cache (no-store), never to a copy another identity left there', async () => {
    const w = worker('private, no-cache');
    await dispatchFetch(w, PATH).settle();
    const leg = w.fetches.find(f => f.path === PATH);
    assert.ok(leg, 'the miss reached the network');
    assert.equal(leg.init && leg.init.cache, 'no-store');
});

test('a private, no-cache 200 with the sha in its ETag is stored and announced', async () => {
    const w = worker('private, no-cache');
    const served = await dispatchFetch(w, PATH).settle();
    assert.equal(served.status, 200);
    assert.deepEqual([...w.stored.get(`bw-a1-${ALICE}-img`).keys()], [`${ORIGIN}/__bw/${SHA}/640`]);
    assert.equal(w.posted.length, 1);
});

test('a no-store 200 is still served and never stored', async () => {
    const w = worker('no-store');
    assert.equal((await dispatchFetch(w, PATH).settle()).status, 200);
    assert.equal(w.stored.has(`bw-a1-${ALICE}-img`) && w.stored.get(`bw-a1-${ALICE}-img`).size > 0, false);
});
