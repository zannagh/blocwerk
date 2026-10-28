// A page whose ledger is degraded (a pending v2 upgrade or refused storage: the ledger starts empty) against the real
// page modules: it runs the privacy wipes but never the reconcile, eviction or fill, so a saved wall's 3D survives the
// 3D opt-out; the next durable start resumes. The identity mirror still announces a changed user on such a page. And
// the `mine` rows go with dropWall and "Clear offline data". Run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { fakeCaches, json, manifest, sha, USER_A, USER_B, ORIGIN } from './fixtures/page-sandbox.mjs';
import { W1, W2, keys, names, plain, memberPage } from './fixtures/asset-pages.mjs';

const SAVED = [sha('3'), sha('8')]; // W1's texture (in -img) and a splat (in -3d), saved for offline 3D

function savedCaches() {
    return fakeCaches({
        [`bw-a1-${USER_A}-img`]: [`${ORIGIN}/__bw/${sha('1')}`, `${ORIGIN}/__bw/${SAVED[0]}`],
        [`bw-a1-${USER_A}-3d`]: [`${ORIGIN}/__bw/${SAVED[1]}`],
        [`bw-a1-${USER_B}-img`]: [`${ORIGIN}/__bw/${sha('b')}`],
        'bw-a1-anon-img': [`${ORIGIN}/__bw/${sha('c')}`],
    });
}

const w1Saved = () => ({
    [`/api/walls/${W1}/manifest`]: () => json(manifest(W1, [[sha('1'), 'core'], [SAVED[0], 'view3d', { kind: 'texture' }], [SAVED[1], 'view3d', { kind: 'splat', splats: 1000 }]])),
});

test('a degraded ledger wipes other partitions but never reconciles, evicts or fills: a saved wall\'s 3D survives the opt-out', async () => {
    const caches = savedCaches();
    const page = memberPage({ degraded: true, caches, prefs3d: false, routes: w1Saved() });
    await page.boot();
    const ledger = await page.ledger();
    assert.equal(ledger.degraded, true);
    assert.deepEqual(names(caches), [`bw-a1-${USER_A}-3d`, `bw-a1-${USER_A}-img`], 'the privacy wipe of other partitions still runs');
    assert.deepEqual(keys(caches, `bw-a1-${USER_A}-3d`), [SAVED[1]]);
    assert.deepEqual(keys(caches, `bw-a1-${USER_A}-img`), [sha('1'), SAVED[0]].sort());
    assert.equal(page.fetches.filter(f => /manifest|docs|cache-summary/.test(f.path)).length, 0, 'no manifest round');
    assert.equal(page.assetFetches().length, 0, 'no fill');

    await page.cache.setKeep3d(false);
    await page.cache.evict();
    await page.cache.manifestChanged(W1);
    await page.settle();
    assert.deepEqual(keys(caches, `bw-a1-${USER_A}-3d`), [SAVED[1]], 'nothing on this page evicts');
    assert.equal(page.assetFetches().length, 0);

    // The next start has its IndexedDB again (here: the plain ledger, with the saved row): it resumes as usual.
    const durable = memberPage({ caches, prefs3d: false, routes: w1Saved() });
    const book = await durable.ledger();
    await book.setMeta({ activePartition: USER_A });
    await book.updateWall(USER_A, W1, () => ({ partition: USER_A, wallId: W1, entries: [], keepOffline: true, save3d: true, lastOpened: 1 }));
    await durable.boot();
    assert.ok(durable.fetches.some(f => f.path === `/api/walls/${W1}/manifest`));
    assert.deepEqual(keys(caches, `bw-a1-${USER_A}-3d`), [SAVED[1]], 'the saved 3D is still pinned');
    assert.ok((await book.assetsOf(USER_A)).some(a => a.key === SAVED[1]), 'reconciled into the durable ledger');
});

test('the identity mirror: a degraded page still announces another user, and stays quiet for the same one', async () => {
    const storage = new Map();
    const first = memberPage({ storage, user: 'alice-id' });
    await first.boot();
    assert.ok(storage.has('blocwerk-asset-identity'));

    const same = memberPage({ storage, degraded: true, user: 'alice-id' });
    await same.boot();
    assert.ok(!same.events.includes('bw:identity-changed'));

    const cleared = [];
    const other = memberPage({ storage, degraded: true, user: 'bob-id', label: USER_B, deployDrafts: { clearAll: () => cleared.push(1) } });
    await other.boot();
    assert.ok(other.events.includes('bw:identity-changed'), 'a pending page announces the sign-in of another user');
    assert.equal(cleared.length, 1, 'and clears the deploy drafts');

    const signedOut = memberPage({ storage, degraded: true, user: '', label: 'anon' });
    await signedOut.boot();
    assert.ok(signedOut.events.includes('bw:identity-changed'), 'and a sign-out');
});

test('a degraded page with no readable localStorage fails towards announcing', async () => {
    const page = memberPage({ degraded: true, storageBlocked: true, user: 'alice-id' });
    await page.boot();
    assert.ok(page.events.includes('bw:identity-changed'));
    const durable = memberPage({ storageBlocked: true, user: 'alice-id' });
    await durable.boot();
    assert.ok(!durable.events.includes('bw:identity-changed'), 'a durable ledger never needs the mirror');
});

test('dropWall and "Clear offline data" delete the partition\'s mine rows, and only those', async () => {
    const page = memberPage();
    await page.boot();
    const ledger = await page.ledger();
    for (const [partition, wallId] of [[USER_A, W1], [USER_A, W2], [USER_B, W1]]) {
        await ledger.putMine({ partition, wallId, cursor: null, ticks: [], favourites: [], fetchedAt: 1 });
    }
    await page.cache.dropWall(W1);
    assert.deepEqual(plain((await ledger.mineOf(USER_A)).map(m => m.wallId)), [W2]);

    assert.equal(await page.win.bwAssetReport.clear(), true);
    assert.deepEqual(plain((await ledger.mineOf(USER_A)).map(m => m.wallId)), []);
    assert.equal((await ledger.mineOf(USER_B)).length, 1, 'another partition is not this clear\'s');
});
