// The offline client's kiosk mode (Phase 3 PR 3.15, client-store.js + client-store-rows.js): a tablet's `k-` partition
// shows its one bound wall and never marks, whoever the tablet acts as; another wall (a second row under `k-`, or any
// other partition's data on the device) is never read; a tablet whose wall document is not cached (its answers were
// `no-store`) and a `none` tablet show "Not available offline" (the `kiosk` state); a Server page records the kiosk
// mark the client reads (asset-session.js). Run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { loadClient } from './fixtures/client-sandbox.mjs';
import { loadPage, USER_A, KIOSK, ORIGIN } from './fixtures/page-sandbox.mjs';
import { W1, W2 } from './fixtures/asset-pages.mjs';
import { plain, same, sha, cacheNames, device } from './fixtures/client-device.mjs';

const nothing = (state, partition = null) => ({ state, partition, shareWall: null, walls: [], mine: [] });

test('a kiosk tablet shows its one bound wall in the kiosk class, and no marks even where a row holds some', async () => {
    const { client, ledger } = await device(KIOSK, { walls: [W1] });
    assert.equal((await ledger.mineOf(KIOSK)).length, 1, 'the fixture plants a mark under the kiosk label');

    const snap = plain(await client.store.snapshot());
    assert.equal(snap.state, 'ready');
    assert.equal(snap.partition, KIOSK);
    same(snap.walls.map(w => w.wallId), [W1]);
    same(snap.mine, [], 'act-as or not, a kiosk reads no marks');

    const read = plain(await client.store.docs(KIOSK, W1));
    assert.equal(read.wallId, W1);
    assert.equal(JSON.parse(read.docs.wall.text).wallId, W1);
    assert.equal(await client.store.docs(KIOSK, W2), null);
});

test('another wall is never reached: not a second row under k-, not another partition\'s, not a member\'s marks', async () => {
    const { client, ledger, caches } = await device(KIOSK, { walls: [W1], others: [USER_A, 'anon'] });
    await client.store.start();
    for (const label of [USER_A, 'anon']) {
        same(await ledger.wallsOf(label), [], `${label}: swept on the tablet's start`);
        same(await ledger.mineOf(label), [], `${label}: its marks too`);
        assert.ok(!cacheNames(caches).includes(`bw-a1-${label}-3d`), label);
        assert.equal(await client.store.docs(label, W2), null, label);
    }

    // A second row under the kiosk label is not the bound wall's alone: neither wall is read.
    await ledger.updateWall(KIOSK, W2, () => ({ partition: KIOSK, wallId: W2, member: true, data: { wall: { sha: sha('2a'), bytes: 1 } } }));
    await caches.putText(`bw-a1-${KIOSK}-3d`, `${ORIGIN}/__bw/${sha('2a')}`, '{"wallId":"w2"}');
    same(await client.store.snapshot(), nothing('kiosk', KIOSK));
    assert.equal(await client.store.docs(KIOSK, W1), null);
    assert.equal(await client.store.docs(KIOSK, W2), null);
});

test('a no-store tablet (a row from the index, no cached wall document) is not available offline', async () => {
    const { client, caches } = await device(KIOSK, { walls: [W1] });
    await caches.delete(`bw-a1-${KIOSK}-3d`);

    same(await client.store.snapshot(), nothing('kiosk', KIOSK));
    assert.equal(await client.store.docs(KIOSK, W1), null);
});

test('a kiosk with no row yet, and a `none` tablet by the mirror\'s mark, are not available offline; any other device is empty', async () => {
    const { client } = await device(KIOSK, { walls: [] });
    same(await client.store.snapshot(), nothing('kiosk', KIOSK));

    for (const [kiosk, state] of [[true, 'kiosk'], [false, 'empty'], [undefined, 'empty']]) {
        const storage = new Map([['blocwerk-asset-identity', JSON.stringify({ activePartition: 'none', kiosk })]]);
        const none = loadClient({ storage });
        await (await none.ledger()).setMeta({ activePartition: 'none' });
        same(await none.store.snapshot(), nothing(state), String(kiosk));
    }
});

test('a Server page records the kiosk mark: a tablet (any label, none included) yes, anyone else no, offline.html keeps it', async () => {
    const mark = storage => JSON.parse(storage.get('blocwerk-asset-identity')).kiosk;
    const cases = [
        [{ label: KIOSK, kioskKey: 'key-1' }, true],
        [{ label: 'none', kioskKey: 'key-1' }, true],
        [{ label: KIOSK }, true],
        [{ label: USER_A }, false],
        [{ label: 'anon' }, false],
    ];
    for (const [opts, expected] of cases) {
        const storage = new Map();
        const page = loadPage(Object.assign({ storage, swReply: 'none', summary: { wallsWith3d: 0, walls: [] } }, opts));
        await page.boot();
        assert.equal(mark(storage), expected, JSON.stringify(opts));
    }

    const storage = new Map();
    const tablet = loadPage({ label: 'none', kioskKey: 'key-1', storage, swReply: 'none', summary: { wallsWith3d: 0, walls: [] } });
    await tablet.boot();
    const offline = loadPage({ label: 'offline', storage, swReply: 'none', ledger: await tablet.ledger() });
    await offline.boot();
    assert.equal(mark(storage), true, 'offline.html knows nobody and keeps the mark');
});
