// The 3D side of the asset cache (asset-3d.js, PR 2.8) end to end in the page sandbox: the mobile-data notice, "Save 3D
// offline", no background 3D on a phone, the saved wall surviving the opt-out, and view3d documents counted as 3D.
// Run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { loadPage, json, manifest, sha, USER_A, ORIGIN } from './fixtures/page-sandbox.mjs';
import { W1, W2, keys, plain } from './fixtures/asset-pages.mjs';

const PHONE = 'Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Mobile Safari/537.36';
const DESKTOP = 'Mozilla/5.0 (X11; Linux x86_64) Chrome/130.0 Safari/537.36';
const THREE_D = [sha('3'), sha('5'), sha('6'), sha('7')];

// W1 has 3D: a texture, its mask and two splat levels; W2 has 3D too (one texture).
function page3d(more = {}) {
    const routes = Object.assign({
        [`/api/walls/${W1}/manifest`]: () => json(manifest(W1, [
            [sha('1'), 'core'],
            [sha('3'), 'view3d', { kind: 'texture', bytes: 2_000_000 }],
            [sha('5'), 'view3d', { kind: 'mask', bytes: 100_000 }],
            [sha('6'), 'view3d', { kind: 'splat', splats: 40_000, bytes: 5_000_000 }],
            [sha('7'), 'view3d', { kind: 'splat', splats: 400_000, bytes: 40_000_000 }],
        ]), 200, { etag: 'W/"m1"' }),
        [`/api/walls/${W2}/manifest`]: () => json(manifest(W2, [[sha('4'), 'view3d', { kind: 'texture' }]]), 200, { etag: 'W/"m2"' }),
    }, more.routes || {});
    return loadPage(Object.assign({ label: USER_A, ua: PHONE, summary: { wallsWith3d: 2, walls: [W1, W2] }, path: `/walls/${W1}/3d` }, more, { routes }));
}

const fetched3d = page => page.assetFetches().map(f => f.path.split('/a/')[1]).filter(s => THREE_D.includes(s) || s === sha('4'));

test('a phone fills no 3D in the background, and asks before a 3D view whose data is not on the device, with its size', async () => {
    const page = page3d();
    await page.boot();

    assert.deepEqual(fetched3d(page), [], 'no background 3D on a phone');
    assert.deepEqual(plain(await page.win.bwAsset3d.gate(W1)), { ask: true, bytes: 2_000_000 + 100_000 + 5_000_000 });
    assert.deepEqual(plain(await page.win.bwAsset3d.gate(W2)), { ask: true, bytes: 1000 });

    // A desktop on a metered connection has no 3D cached either, and still never asks.
    const desktop = page3d({ ua: DESKTOP, connection: { type: 'cellular' } });
    await desktop.boot();
    assert.deepEqual(fetched3d(desktop), []);
    assert.deepEqual(plain(await desktop.win.bwAsset3d.gate(W1)), { ask: false, bytes: null });
});

test('no notice once the 3D is on the device, after Continue on this page, after "Don\'t ask again", or on a kiosk', async () => {
    const page = page3d();
    await page.boot();
    for (const s of [sha('3'), sha('5'), sha('6')]) {
        await page.win.fetch(`/api/walls/${W1}/a/${s}`);
    }
    await page.settle();
    assert.equal((await page.win.bwAsset3d.gate(W1)).ask, false, 'textures, mask and one splat level are cached');

    await page.win.bwAsset3d.accept(W2, false);
    assert.equal((await page.win.bwAsset3d.gate(W2)).ask, false, 'continued on this page');
    assert.equal(page.storage.size, 0, 'nothing remembered without "Don\'t ask again"');

    const other = page3d();
    await other.boot();
    await other.win.bwAsset3d.accept(W2, true);
    assert.equal(other.storage.get(`blocwerk-cache-notice:data-3d:${USER_A}`), '1');
    assert.equal((await other.win.bwAsset3d.gate(W1)).ask, false, 'remembered for this user, every wall');

    const kiosk = page3d({ kioskKey: 'k1' });
    await kiosk.boot();
    assert.equal((await kiosk.win.bwAsset3d.gate(W1)).ask, false);
});

test('Save 3D offline on a phone caches the wall\'s 3D (one splat level), asks for persistent storage and pins the wall', async () => {
    const page = page3d();
    await page.boot();
    const before = page.persisted.length;
    assert.deepEqual(plain(await page.win.bwAsset3d.status(W1)), { available: true, saved: false, cached: false });

    assert.deepEqual(plain(await page.win.bwAsset3d.save(W1)), { saved: true });

    assert.deepEqual(fetched3d(page), [sha('3'), sha('5'), sha('6')], 'textures, mask, the smallest level; not W2');
    assert.equal(page.persisted.length, before + 1);
    const row = await (await page.ledger()).getWall(USER_A, W1);
    assert.equal(row.keepOffline, true);
    assert.equal(row.save3d, true);
    assert.deepEqual(plain(await page.win.bwAsset3d.status(W1)), { available: true, saved: true, cached: true });
    assert.equal((await page.win.bwAsset3d.gate(W1)).ask, false);
});

test('a saved wall keeps its 3D, its view3d document included, through the opt-out; an unsaved wall loses it', async () => {
    const page = page3d({ ua: DESKTOP });
    await page.boot();
    await page.win.bwAsset3d.save(W1);
    const ledger = await page.ledger();
    const cache = await page.caches.open(`bw-a1-${USER_A}-3d`);
    for (const [wallId, doc] of [[W1, sha('8')], [W2, sha('9')]]) {
        await page.win.bwAsset3d.noteDoc(wallId, doc, 100);
        await cache.put(`${ORIGIN}/__bw/${doc}`, new Response('{}'));
        await ledger.putAsset({ partition: USER_A, key: doc, sha: doc, width: null, cache: null, bytes: 100, cachedAt: 0, verifiedAt: 0 });
    }
    assert.deepEqual(plain((await ledger.getWall(USER_A, W1)).docs), [{ sha: sha('8'), kind: 'view3d', tier: 'view3d', bytes: 100 }]);

    // The next page load runs eviction on start: a listed document is no orphan, however old.
    const next = page3d({ ua: DESKTOP, ledger, caches: page.caches });
    await next.boot();
    assert.ok(keys(page.caches, `bw-a1-${USER_A}-3d`).includes(sha('9')));

    await next.cache.setKeep3d(false);
    await next.settle();
    const left = (await ledger.assetsOf(USER_A)).map(a => a.key);
    for (const kept of [sha('3'), sha('5'), sha('6'), sha('8')]) {
        assert.ok(left.includes(kept), `saved wall keeps ${kept.slice(0, 4)}`);
    }
    assert.ok(!left.includes(sha('4')) && !left.includes(sha('9')), 'the unsaved wall\'s 3D goes');
});

test('a browser that refuses the storage leaves Save 3D offline unsaved, silently, and the page streams', async () => {
    const page = page3d({ refuse: true });
    await page.boot();

    assert.deepEqual(plain(await page.win.bwAsset3d.save(W1)), { saved: false });
    assert.equal(page.cache.state().runner.streaming, true);
    assert.equal((await page.win.bwAsset3d.status(W1)).available, false, 'no save offered on a streaming page');
});

test('no Save 3D offline for an anonymous share viewer, a kiosk or a page without a caching worker', async () => {
    for (const more of [{ label: 'anon' }, { kioskKey: 'k1' }, { swReply: 'none' }]) {
        const page = page3d(more);
        await page.boot();
        assert.equal((await page.win.bwAsset3d.status(W1)).available, false, JSON.stringify(more));
        assert.deepEqual(plain(await page.win.bwAsset3d.save(W1)), { saved: false });
    }
});
