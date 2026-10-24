// The offline client's store reader (Phase 3 PR 3.10, client-store.js) over the real shared cache modules and none of
// Web's globals: the partition comes from the ledger, never the shell's `offline` meta; a lock, a pending upgrade,
// refused storage and a kiosk read nothing; `anon` reads only its share link's wall and a tokenless /v/ wipes it
// (rule 6); marks only under `u-`; documents only under the active label, re-checked after the read, the predecessor
// never for comments; the start drops a lock past 7 days and sweeps other partitions (their mine rows too); Clear
// offline data works without bwAssetCache; the refusal matrix of §3; the change listeners. Run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { loadClient, bodyCaches } from './fixtures/client-sandbox.mjs';
import { USER_A, USER_B, KIOSK, ORIGIN } from './fixtures/page-sandbox.mjs';
import { W1, W2 } from './fixtures/asset-pages.mjs';

const DAY = 24 * 60 * 60 * 1000;
const plain = value => JSON.parse(JSON.stringify(value));
// The sandbox's arrays are of another realm: compare them as plain JSON.
const same = (actual, expected, message) => assert.deepEqual(plain(actual), plain(expected), message ?? "");
const TOKEN = 'tok3n';
// A 64-digit sha from a short hex pattern.
const sha = pattern => pattern.repeat(64).slice(0, 64);
const doc = (kind, wallId, extra = {}) => JSON.stringify(Object.assign({ v: 1, wallId, kind }, extra));
const tick = id => ({ boulderId: id, attempts: 2, sent: false, flashed: true, lastAt: null });

// A device whose ledger holds `label`'s walls W1 and W2 (each with a wall and a comments document cached in -3d),
// their mine rows, and the same for `others` (other partitions a sweep should take).
async function device(label, more = {}) {
    const caches = more.caches || bodyCaches();
    const client = loadClient(Object.assign({ caches }, more));
    const ledger = await client.ledger();
    for (const p of [label].concat(more.others || [])) {
        for (const [wallId, c] of [[W1, '1'], [W2, '2']]) {
            const shas = { wall: sha(c + 'a'), comments: sha(c + 'c') };
            await ledger.updateWall(p, wallId, () => ({ partition: p, wallId, data: { wall: { sha: shas.wall, bytes: 1, cursor: '1.2.3' }, comments: { sha: shas.comments, bytes: 1, cursor: '1.2.3' } } }));
            await caches.putText(`bw-a1-${p}-3d`, `${ORIGIN}/__bw/${shas.wall}`, doc('wall', wallId));
            await caches.putText(`bw-a1-${p}-3d`, `${ORIGIN}/__bw/${shas.comments}`, doc('comments', wallId));
            await ledger.putMine({ partition: p, wallId, cursor: '1.2.3', ticks: [tick(W1)], favourites: [], fetchedAt: 1 });
        }
    }
    await ledger.setMeta(Object.assign({ activePartition: label }, more.meta || {}));
    return { client, ledger, caches };
}

const cacheNames = caches => [...caches.stores.keys()].sort();

test('the partition is the ledger\'s active label, never the shell\'s offline meta', async () => {
    const { client } = await device(USER_A);
    const snap = plain(await client.store.snapshot());

    assert.equal(snap.state, 'ready');
    assert.equal(snap.partition, USER_A);
    same(snap.walls.map(w => w.wallId).sort(), [W1, W2]);
    same(Object.keys(snap.walls[0].data).sort(), ['comments', 'wall']);
    assert.equal(snap.mine.length, 2);
    assert.equal(snap.mine[0].ticks[0].lastAt, new Date(0).toISOString(), 'a missing time reads as the epoch, never null');
    assert.equal(snap.mine[0].ticks[0].flashed, true);
});

test('a lock reads nothing, whatever the active label holds; docs() too', async () => {
    const { client } = await device('anon', { path: `/v/walls/${W1}/shared/${TOKEN}`, meta: { locked: { label: USER_A, since: Date.now() } }, others: [USER_A] });

    same(await client.store.snapshot(), { state: 'locked', partition: null, shareWall: null, walls: [], mine: [] });
    assert.equal(await client.store.docs('anon', W1), null);
    assert.equal(await client.store.docs(USER_A, W1), null);
});

test('a lock the degraded ledger only knows from the mirror still reads nothing', async () => {
    const storage = new Map([['blocwerk-asset-identity', JSON.stringify({ activePartition: 'anon', locked: { label: USER_A, since: Date.now() } })]]);
    const client = loadClient({ degraded: true, storage });
    assert.equal((await client.store.snapshot()).state, 'locked');
});

test('a pending upgrade is updating, refused storage unavailable, a kiosk kiosk: none reads a wall', async () => {
    const pending = loadClient({ degraded: true, pending: true });
    assert.equal((await pending.store.snapshot()).state, 'updating');
    const refused = loadClient({ degraded: true, storage: new Map([['blocwerk-asset-identity', JSON.stringify({ activePartition: USER_A })]]) });
    assert.equal((await refused.store.snapshot()).state, 'unavailable');
    assert.equal(await refused.store.docs(USER_A, W1), null);

    const { client } = await device(KIOSK);
    same(await client.store.snapshot(), { state: 'kiosk', partition: KIOSK, shareWall: null, walls: [], mine: [] });
    assert.equal(await client.store.docs(KIOSK, W1), null, 'no kiosk documents before PR 3.15');
});

test('no label, or a label with no wall, is empty', async () => {
    const none = loadClient();
    assert.equal((await none.store.snapshot()).state, 'empty');
    const { client, ledger } = await device(USER_A);
    await ledger.setMeta({ activePartition: USER_B });
    assert.equal((await client.store.snapshot()).state, 'empty', 'another label\'s rows are not this one\'s');
});

test('anon reads only the wall its share link names, never marks; a tokenless /v/ wipes anon (rule 6)', async () => {
    const shared = await device('anon', { path: `/v/walls/${W2}/shared/${TOKEN}` });
    const snap = plain(await shared.client.store.snapshot());
    assert.equal(snap.state, 'ready');
    assert.equal(snap.shareWall, W2);
    same(snap.walls.map(w => w.wallId), [W2]);
    same(snap.mine, []);
    assert.equal(await shared.client.store.docs('anon', W1), null, 'another wall of anon is not this link\'s');
    assert.ok(await shared.client.store.docs('anon', W2));

    const lock = { label: USER_A, since: Date.now() };
    const tokenless = await device('anon', { path: `/v/walls/${W2}`, others: [USER_A], meta: { locked: lock } });
    await tokenless.client.store.start();
    same(await tokenless.ledger.wallsOf('anon'), []);
    same(await tokenless.ledger.mineOf('anon'), []);
    assert.ok(!cacheNames(tokenless.caches).includes('bw-a1-anon-3d'));
    assert.equal((await tokenless.ledger.wallsOf(USER_A)).length, 2, 'the locked partition is kept');
});

test('signed in on a share route: every own wall, the share wall named', async () => {
    const { client } = await device(USER_A, { path: `/v/walls/${W1}/boulders/${W2}/shared/${TOKEN}` });
    const snap = plain(await client.store.snapshot());
    assert.equal(snap.shareWall, W1);
    assert.equal(snap.walls.length, 2);
});

test('docs() reads the active label\'s -3d cache, a predecessor for a kind not yet cached, never for comments', async () => {
    const { client, ledger, caches } = await device(USER_A);
    const fresh = sha('f');
    await ledger.updateWall(USER_A, W1, row => Object.assign(row, {
        data: Object.assign(row.data, { holds: { sha: fresh, bytes: 1 }, comments: { sha: sha('9'), bytes: 1 } }),
        dataPrev: { holds: sha('e'), comments: sha('1c') }
    }));
    await caches.putText(`bw-a1-${USER_A}-3d`, `${ORIGIN}/__bw/${sha('e')}`, doc('holds', W1, { old: true }));

    const read = plain(await client.store.docs(USER_A, W1));
    assert.equal(read.partition, USER_A);
    assert.equal(read.docs.wall.sha, sha('1a'));
    assert.equal(read.docs.holds.sha, sha('e'), 'the cached predecessor stands in');
    assert.equal(read.docs.comments, undefined, 'a replaced comments document never stands in');
    assert.equal(await client.store.docs(USER_B, W1), null, 'only the active label');

    await caches.putText(`bw-a1-${USER_A}-3d`, `${ORIGIN}/__bw/${fresh}`, doc('holds', W1));
    assert.equal((await client.store.docs(USER_A, W1)).docs.holds.sha, fresh);
});

test('docs() discards a read during which the label changed or a lock appeared', async () => {
    for (const change of [{ activePartition: USER_B }, { activePartition: 'anon', locked: { label: USER_A, since: Date.now() } }]) {
        const { client, ledger, caches } = await device(USER_A);
        const match = caches.match;
        caches.match = async (...args) => { await ledger.setMeta(change); return match(...args); };
        assert.equal(await client.store.docs(USER_A, W1), null, JSON.stringify(change));
    }
});

test('the start drops a lock past 7 days with its partition, and sweeps every other partition and its marks', async () => {
    const old = { label: USER_A, since: Date.now() - 8 * DAY };
    const { client, ledger, caches } = await device(USER_B, { others: [USER_A, KIOSK], meta: { locked: old } });
    await client.store.start();

    assert.equal((await ledger.getMeta()).locked, null);
    for (const label of [USER_A, KIOSK]) {
        same(await ledger.wallsOf(label), [], label);
        same(await ledger.mineOf(label), [], `${label}: a degraded Clear's mine rows go with the sweep`);
        assert.ok(!cacheNames(caches).includes(`bw-a1-${label}-3d`), label);
    }
    assert.equal((await ledger.mineOf(USER_B)).length, 2);

    const kept = await device(USER_B, { others: [USER_A], meta: { locked: { label: USER_A, since: Date.now() - DAY } } });
    await kept.client.store.start();
    assert.equal((await kept.ledger.wallsOf(USER_A)).length, 2, 'a live lock\'s partition survives the sweep');
});

test('Clear offline data inside the client, with none of Web\'s globals: active and locked partitions go, the rest stays', async () => {
    const bus = new Set();
    const lock = { label: USER_A, since: Date.now() };
    const { client, ledger, caches } = await device('anon', { bus, path: `/v/walls/${W1}/shared/${TOKEN}`, others: [USER_A], meta: { locked: lock } });
    for (const name of ['bwAssetCache', 'bwAssetStore', 'bwAssetReport', 'bwDeployDrafts', 'bwAssetManifest']) {
        assert.equal(client.win[name], undefined, `${name} is Web's`);
    }
    await caches.putText('blocwerk-offline', 'x', 'unsent');
    await caches.putText('bw-sw-state', '/__bw/locked', USER_A);
    const heard = [];
    const tab = new client.win.BroadcastChannel('bw-asset-clear');
    tab.onmessage = e => heard.push(e.data.type);

    assert.equal(await client.store.clear(), true);
    await new Promise(resolve => setImmediate(resolve));

    for (const label of ['anon', USER_A]) {
        same(await ledger.wallsOf(label), [], label);
        same(await ledger.mineOf(label), [], label);
    }
    same(cacheNames(caches), ['blocwerk-offline', 'bw-sw-state']);
    assert.equal(caches.stores.get('bw-sw-state').has('/__bw/locked'), false, 'the worker\'s lock marker goes');
    assert.equal((await ledger.getMeta()).locked, null);
    assert.equal((await ledger.getMeta()).activePartition, 'anon', 'the active label stays');
    same(heard, ['clearing', 'cleared']);
    assert.ok(client.posted.some(m => m.type === 'sw-refresh-partition'), 'the worker is told without bwAssetStore');
    assert.equal((await client.store.snapshot()).state, 'empty');
});

test('the refusal matrix (§3): 403/404 drop the wall, a 401 expired locks u-, other 401s and 410 wipe', async () => {
    const run = async (label, status, reason, opts) => {
        const d = await device(label, { path: `/v/walls/${W1}/shared/${TOKEN}` });
        return Object.assign(d, { outcome: await d.client.store.refused(label, W1, status, reason, opts) });
    };
    for (const status of [403, 404]) {
        const d = await run(USER_A, status);
        assert.equal(d.outcome, 'dropped');
        same((await d.ledger.wallsOf(USER_A)).map(w => w.wallId), [W2]);
        same((await d.ledger.mineOf(USER_A)).map(m => m.wallId), [W2]);
    }
    const mine = await run(USER_A, 403, null, { mine: true });
    assert.equal(mine.outcome, 'dropped');
    assert.equal((await mine.ledger.wallsOf(USER_A)).length, 2, 'a mine refusal keeps the wall');
    same((await mine.ledger.mineOf(USER_A)).map(m => m.wallId), [W2]);

    const locked = await run(USER_A, 401, 'expired');
    assert.equal(locked.outcome, 'locked');
    assert.equal((await locked.ledger.getMeta()).locked.label, USER_A);
    assert.equal((await locked.ledger.wallsOf(USER_A)).length, 2, 'locked, not wiped');
    assert.equal((await locked.client.store.snapshot()).state, 'locked');

    for (const [label, status, reason] of [[USER_A, 401, 'forced'], [USER_A, 401, 'gone'], [USER_A, 410, null], ['anon', 401, 'expired'], [KIOSK, 401, null], [KIOSK, 410, null]]) {
        const d = await run(label, status, reason);
        assert.equal(d.outcome, 'wiped', `${label} ${status} ${reason}`);
        same(await d.ledger.wallsOf(label), [], `${label} ${status} ${reason}`);
        assert.equal((await d.ledger.getMeta()).locked ?? null, null);
    }
    assert.equal((await run(USER_A, 503)).outcome, 'kept');
    const other = await device(USER_A);
    assert.equal(await other.client.store.refused(USER_B, W1, 403), 'switched');
    assert.equal((await other.ledger.wallsOf(USER_A)).length, 2, 'another tab\'s label is not this refusal\'s');
});

test('listen(): another tab\'s clear or lock, an identity change and the tab coming back each call back', async () => {
    const bus = new Set();
    const { client } = await device(USER_A, { bus });
    let calls = 0;
    client.store.listen(() => { calls++; });
    new client.win.BroadcastChannel('bw-asset-clear').postMessage({ type: 'locked', partition: USER_A });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(calls, 1);
    client.winEvent('storage', { key: 'blocwerk-asset-identity' });
    client.winEvent('storage', { key: 'something-else' });
    assert.equal(calls, 2);
    client.docEvent('visibilitychange');
    assert.equal(calls, 3);
});

test('without the shared modules nothing is read and nothing throws', async () => {
    const client = loadClient({ modules: [] });
    same(await client.store.snapshot(), { state: 'unavailable', partition: null, shareWall: null, walls: [], mine: [] });
    assert.equal(await client.store.docs(USER_A, W1), null);
    assert.equal(await client.store.clear(), false);
    assert.equal(await client.store.snapshotJson(), JSON.stringify({ state: 'unavailable', partition: null, shareWall: null, walls: [], mine: [] }));
    assert.equal(await client.store.docsJson(USER_A, W1), null);
});
