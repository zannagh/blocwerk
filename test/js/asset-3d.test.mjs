// The 3D side of the asset cache (asset-3d.js, PR 2.8) end to end in the page sandbox: the mobile-data notice, "Save 3D
// offline", no background 3D on a phone, the saved wall surviving the opt-out, and view3d documents counted as 3D.
// Run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { loadPage, json, manifest, sha, USER_A, ORIGIN } from './fixtures/page-sandbox.mjs';
import { W1, W2, keys, plain } from './fixtures/asset-pages.mjs';

const PHONE = 'Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Mobile Safari/537.36';
const DESKTOP = 'Mozilla/5.0 (X11; Linux x86_64) Chrome/130.0 Safari/537.36';
const IPHONE = 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_7 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1';
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
    assert.deepEqual(plain(await page.win.bwAsset3d.status(W1)), { available: true, saved: false, cached: false, install: null });

    assert.deepEqual(plain(await page.win.bwAsset3d.save(W1)), { saved: true });

    assert.deepEqual(fetched3d(page), [sha('3'), sha('5'), sha('6')], 'textures, mask, the smallest level; not W2');
    assert.equal(page.persisted.length, before + 1);
    const row = await (await page.ledger()).getWall(USER_A, W1);
    assert.equal(row.keepOffline, true);
    assert.equal(row.save3d, true);
    assert.deepEqual(plain(await page.win.bwAsset3d.status(W1)), { available: true, saved: true, cached: true, install: null });
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

test('an Android tablet in desktop mode (Linux UA, touch, a coarse pointer) counts as mobile for the notice', async () => {
    const tablet = page3d({ ua: DESKTOP, maxTouchPoints: 5, coarse: true, connection: { type: 'cellular' } });
    await tablet.boot();
    assert.equal((await tablet.win.bwAsset3d.gate(W1)).ask, true);

    const desktop = page3d({ ua: DESKTOP, maxTouchPoints: 5, coarse: false, connection: { type: 'cellular' } });
    await desktop.boot();
    assert.equal((await desktop.win.bwAsset3d.gate(W1)).ask, false, 'a touch laptop with a fine pointer is a desktop');
});

test('an iOS Safari tab gets the install nudge instead of the save; the installed app keeps the save', async () => {
    const tab = page3d({ ua: IPHONE });
    await tab.boot();
    const nudge = plain(await tab.win.bwAsset3d.status(W1));
    assert.equal(nudge.available, false);
    assert.match(nudge.install, /^Install the app to keep 3D offline\./);
    assert.deepEqual(plain(await tab.win.bwAsset3d.save(W1)), { saved: false });
    assert.equal((await (await tab.ledger()).getWall(USER_A, W1)).save3d, undefined);

    const app = page3d({ ua: IPHONE, standalone: true });
    await app.boot();
    assert.equal((await app.win.bwAsset3d.status(W1)).available, true);
    assert.deepEqual(plain(await app.win.bwAsset3d.save(W1)), { saved: true });
});

test('Remove undoes the save: the wall is no longer kept, and the opt-out and eviction apply again', async () => {
    const page = page3d({ ua: DESKTOP });
    await page.boot();
    await page.win.bwAsset3d.save(W1);
    page.win.bwPrefs.setCache3d(false);

    assert.deepEqual(plain(await page.win.bwAsset3d.unsave(W1)), { saved: false });
    await page.settle();

    const row = await (await page.ledger()).getWall(USER_A, W1);
    assert.equal(row.keepOffline, false);
    assert.equal(row.save3d, false);
    const left = (await (await page.ledger()).assetsOf(USER_A)).map(a => a.key);
    assert.ok(!THREE_D.some(s => left.includes(s)), 'the opt-out evicts the unsaved wall\'s 3D');
    assert.equal((await page.win.bwAsset3d.status(W1)).saved, false);
});

test('a document already in the ledger is listed at the ledger\'s size, so the two agree', async () => {
    const page = page3d({ ua: DESKTOP });
    await page.boot();
    const ledger = await page.ledger();
    await ledger.putAsset({ partition: USER_A, key: sha('8'), sha: sha('8'), width: null, cache: null, bytes: 4321, cachedAt: 1, verifiedAt: 1 });

    await page.win.bwAsset3d.noteDoc(W1, sha('8'), 9999);
    await page.win.bwAsset3d.noteDoc(W2, sha('9'), 777);

    assert.equal((await ledger.getWall(USER_A, W1)).docs[0].bytes, 4321);
    assert.equal((await ledger.getWall(USER_A, W2)).docs[0].bytes, 777, 'not cached yet: the page\'s count, the same wire size');
});

test('held names the splat levels the ledger holds in this partition, and nothing when the page does not cache', async () => {
    const page = page3d({ ua: DESKTOP });
    await page.boot();
    const held = await page.win.bwAsset3d.held([sha('6'), sha('7'), sha('9'), null]);
    assert.deepEqual([...held], [sha('6')], 'the desktop fill cached the smallest splat level only');

    const none = page3d({ label: 'none' });
    await none.boot();
    assert.deepEqual([...await none.win.bwAsset3d.held([sha('6')])], []);
});
