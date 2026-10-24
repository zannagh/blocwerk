// The offline client's store (Phase 3 PR 3.10) between tabs and across starts: its own Clear is not heard as another
// tab's; a refusal it meets reaches the Server tabs over BroadcastChannel without it ever listening; a Clear on a
// degraded ledger (refused storage, a pending upgrade) leaves the label for the next durable start, the client's or a
// Server page's, to drop. Run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { loadClient } from './fixtures/client-sandbox.mjs';
import { loadPage, USER_A } from './fixtures/page-sandbox.mjs';
import { W1, memberPage } from './fixtures/asset-pages.mjs';
import { cacheNames, device, same, sha } from './fixtures/client-device.mjs';

const tick = () => new Promise(resolve => setImmediate(resolve));
const mirror = storage => JSON.parse(storage.get('blocwerk-asset-identity') || '{}');

test('the client\'s own Clear never calls its own listener back; another tab\'s post does', async () => {
    const bus = new Set();
    const { client } = await device(USER_A, { bus });
    let calls = 0;
    client.store.listen(() => { calls++; });

    assert.equal(await client.store.clear(), true);
    await tick();
    assert.equal(calls, 0, 'clearing / cleared are this tab\'s own');

    new client.win.BroadcastChannel('bw-asset-clear').postMessage({ type: 'locked', partition: USER_A });
    await tick();
    assert.equal(calls, 1);
});

test('a refusal the client meets stops the Server tabs of the partition, though the client never listened', async () => {
    const bus = new Set();
    const storage = new Map();
    const server = memberPage({ bus, storage, swReply: 'none' });
    await server.boot();
    server.win.bwAssetClear.listen();
    assert.equal(server.cache.state().active, USER_A);

    const client = loadClient({ bus, storage, caches: server.caches, ledger: await server.ledger() });
    assert.equal(await client.store.refused(USER_A, W1, 401, 'forced'), 'wiped');
    await server.settle();

    assert.equal(server.cache.state().active, 'none', 'the Server tab heard `locked` and stopped');
    assert.equal(server.cache.state().stopped, true);
});

test('a refused Clear\'s rows: a degraded Clear marks its label, and the next durable client start drops it', async () => {
    for (const pending of [false, true]) {
        const durable = await device(USER_A);
        const storage = durable.client.storage;
        storage.set('blocwerk-asset-identity', JSON.stringify({ activePartition: USER_A }));

        const degraded = loadClient({ degraded: true, pending, storage, caches: durable.caches });
        assert.equal((await degraded.store.snapshot()).state, pending ? 'updating' : 'unavailable');
        assert.equal(await degraded.store.clear(), true);
        assert.ok(!cacheNames(durable.caches).includes(`bw-a1-${USER_A}-3d`), 'the caches go at once');
        assert.equal(mirror(storage).cleared, USER_A);
        assert.equal((await durable.ledger.wallsOf(USER_A)).length, 2, 'the durable rows are out of a degraded page\'s reach');

        const next = loadClient({ storage, caches: durable.caches, ledger: durable.ledger });
        assert.equal((await next.store.snapshot()).state, 'empty', `pending ${pending}: no "2 walls saved" with no documents`);
        same(await durable.ledger.wallsOf(USER_A), []);
        same(await durable.ledger.mineOf(USER_A), []);
        assert.equal(mirror(storage).cleared ?? null, null, 'the mark goes with the rows');
    }
});

test('a degraded Clear\'s label is dropped by the next durable Server page too, and kept by a degraded one', async () => {
    const durable = await device(USER_A);
    const storage = durable.client.storage;
    storage.set('blocwerk-asset-identity', JSON.stringify({ activePartition: USER_A }));
    const degraded = loadClient({ degraded: true, storage, caches: durable.caches });
    await degraded.store.clear();

    const stillDegraded = loadPage({ label: USER_A, degraded: true, storage, caches: durable.caches, swReply: 'none' });
    await stillDegraded.boot();
    assert.equal(mirror(storage).cleared, USER_A, 'a degraded start passes the mark on');

    const server = loadPage({ label: USER_A, storage, caches: durable.caches, ledger: durable.ledger, swReply: 'none' });
    await server.boot();
    const rows = await durable.ledger.wallsOf(USER_A);
    assert.ok(rows.every(row => !row.data || row.data.wall.sha !== sha('1a')), 'the cleared rows are gone');
    same(await durable.ledger.mineOf(USER_A), []);
    assert.equal(mirror(storage).cleared ?? null, null);
});

test('a lock a degraded Clear dropped from the mirror is dropped by the next durable client start', async () => {
    const lock = { label: USER_A, since: Date.now() };
    const durable = await device('anon', { others: [USER_A], meta: { locked: lock }, path: '/v/' });
    const storage = durable.client.storage;
    storage.set('blocwerk-asset-identity', JSON.stringify({ activePartition: 'anon', locked: lock }));
    const degraded = loadClient({ degraded: true, storage, caches: durable.caches });
    assert.equal((await degraded.store.snapshot()).state, 'locked');
    await degraded.store.clear();
    assert.equal(mirror(storage).locked, null);

    const next = loadClient({ storage, caches: durable.caches, ledger: durable.ledger });
    assert.notEqual((await next.store.snapshot()).state, 'locked');
    same(await durable.ledger.wallsOf(USER_A), []);
    assert.equal((await durable.ledger.getMeta()).locked, null);
});
