// Settings → "Offline storage" and the offline-ready mark (asset-report.js, PR 2.9) against the real page modules:
// the report's sizes come from the ledger, the readiness rule, the per-wall pin and the 3D opt-out's eviction
// ("Clear offline data": asset-clear.test.mjs). Run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { json, manifest, sha, USER_A } from './fixtures/page-sandbox.mjs';
import { W1, W2, keys, plain, memberPage, w1Routes, mark } from './fixtures/asset-pages.mjs';

const reportOf = page => page.win.bwAssetReport.report().then(plain);
const wallOf = (report, wallId) => report.walls.find(w => w.wallId === wallId);

test('the report counts the ledger per wall and tier, never estimate(), and both walls are offline ready', async () => {
    const page = memberPage({ routes: w1Routes() });
    await page.boot();
    const report = await reportOf(page);

    assert.equal(report.caching, true);
    assert.equal(report.kiosk, false);
    // 6 assets announced at 5000 bytes each: W2's 1280 + 640, W1's core, gallery 640 + original, 3D. estimate() says 41 GB.
    assert.equal(report.total, 30000);
    const { core, gallery, view3d, other, total, keepOffline, ready } = wallOf(report, W1);
    assert.deepEqual({ core, gallery, view3d, other, total, keepOffline, ready }, { core: 5000, gallery: 10000, view3d: 5000, other: 0, total: 20000, keepOffline: false, ready: true });
    assert.equal(wallOf(report, W2).core, 10000);
    assert.equal(wallOf(report, W2).ready, true);
    assert.equal(report.walls[0].wallId, W2, 'the open wall was opened last, so it comes first');
    assert.equal(report.keep3d, true);
    assert.equal(report.background3d, true);
    assert.equal(report.iosTab, false);
});

test('readiness: nothing pending, at least one core asset, and every core key this device plans', () => {
    const page = memberPage();
    const { isReady } = page.win.bwAssetReport;
    const photo = { sha: sha('4'), tier: 'core', widths: [640, 1280, 1920] };
    const wall = { wallId: W2, fetchedAt: 1, pending: 0, entries: [photo, { sha: sha('9'), tier: 'lazy' }] };
    const both = new Set([`${sha('4')}/1280`, `${sha('4')}/640`]);

    assert.equal(isReady(wall, both, 1280), true);
    assert.equal(isReady(wall, new Set([`${sha('4')}/640`]), 1280), false, 'the fit rendition is missing');
    assert.equal(isReady(wall, both, 1920), false, 'a wider screen needs the 1920 rendition');
    assert.equal(isReady(Object.assign({}, wall, { pending: 1 }), both, 1280), false, 'a hash still pending');
    assert.equal(isReady(Object.assign({}, wall, { fetchedAt: undefined }), both, 1280), false, 'no manifest yet');
    assert.equal(isReady(Object.assign({}, wall, { entries: [{ sha: sha('9'), tier: 'lazy' }] }), both, 1280), false, 'no core at all');
    assert.equal(isReady({ fetchedAt: 1, pending: 0, entries: [{ sha: sha('1'), tier: 'core' }] }, new Set([sha('1')]), 1280), true, 'a non-photo core entry is its original');
});

test('summarize: an asset two walls list counts in both walls and once in the total', () => {
    const page = memberPage();
    const shared = { sha: sha('7'), tier: 'core' };
    const walls = [
        { wallId: 'a', fetchedAt: 1, pending: 0, lastOpened: 1, entries: [shared] },
        { wallId: 'b', fetchedAt: 1, pending: 0, lastOpened: 2, entries: [Object.assign({}, shared, { tier: 'lazy' })] },
    ];
    const out = plain(page.win.bwAssetReport.summarize(walls, [{ key: sha('7'), sha: sha('7'), bytes: 300 }, { key: sha('8'), sha: sha('8'), bytes: 50 }], 1280));
    assert.equal(out.total, 350, 'an orphan still takes space');
    assert.deepEqual(out.walls.map(w => [w.wallId, w.core, w.other]), [['b', 0, 300], ['a', 300, 0]]);
});

test('the offline-ready mark: set on the ready wall, left off the others, and removed by a clear', async () => {
    const ready = mark(W2);
    const stranger = mark('33333333-3333-3333-3333-333333333333');
    const page = memberPage({ routes: w1Routes(), marks: [ready, stranger] });
    page.win.bwAssetReport.observe();
    await page.boot();
    await page.settle();

    assert.equal(ready.ready, true);
    assert.equal(stranger.ready, false);
    await page.win.bwAssetReport.clear();
    assert.equal(ready.ready, false);
});

test('a wall whose core is not cached yet carries no mark', async () => {
    const pending = mark(W2);
    const page = memberPage({ marks: [pending], swReply: 'none' });
    page.win.bwAssetReport.observe();
    await page.boot();
    await page.settle();
    assert.equal(page.assetFetches().length, 0, 'an old worker: no fill');
    assert.equal(pending.ready, false);
    assert.equal(wallOf(await reportOf(page), W2).ready, false);
});

test('a browser that refused storage reports the page as paused (streaming)', async () => {
    const page = memberPage({ refuse: true, connection: { saveData: true } });
    await page.boot();
    assert.equal((await reportOf(page)).paused, true);
});

test('keep offline pins the wall in the ledger and asks for persistent storage; an unknown wall is refused', async () => {
    const page = memberPage({ ua: 'Mozilla/5.0 (X11; Linux x86_64; rv:131.0) Gecko/20100101 Firefox/131.0' });
    await page.boot();
    assert.equal(page.persisted.length, 0, 'Firefox prompts, so the start never asks');

    assert.equal(await page.win.bwAssetReport.setKeepOffline(W1.toUpperCase(), true), true);
    assert.equal((await (await page.ledger()).getWall(USER_A, W1)).keepOffline, true);
    assert.equal(page.persisted.length, 1);
    assert.equal(wallOf(await reportOf(page), W1).keepOffline, true);

    await page.win.bwAssetReport.setKeepOffline(W1, false);
    assert.equal(wallOf(await reportOf(page), W1).keepOffline, false);
    assert.equal(page.persisted.length, 1, 'unpinning asks nothing');
    assert.equal(await page.win.bwAssetReport.setKeepOffline('33333333-3333-3333-3333-333333333333', true), false);
});

test('unpinning undoes 2.8\'s Save 3D offline; a manifest round after the pin keeps it', async () => {
    const page = memberPage();
    await page.boot();
    const ledger = await page.ledger();
    await ledger.updateWall(USER_A, W1, row => Object.assign(row, { keepOffline: true, save3d: true }));
    assert.equal(wallOf(await reportOf(page), W1).saved3d, true);

    await page.win.bwAssetReport.setKeepOffline(W1, false);
    let row = await ledger.getWall(USER_A, W1);
    assert.deepEqual([row.keepOffline, row.save3d], [false, false]);

    await page.win.bwAssetReport.setKeepOffline(W1, true);
    await page.cache.manifestChanged(W1);
    await page.settle();
    row = await ledger.getWall(USER_A, W1);
    assert.deepEqual([row.keepOffline, !!row.save3d], [true, false], 'the pin survives the manifest round; 3D is not revived');
    assert.equal(wallOf(await reportOf(page), W1).saved3d, false);
});

test('a pinned wall keeps its assets over budget while an unpinned wall loses them', async () => {
    const big = 200 * 1024 * 1024;
    const routes = {
        [`/api/walls/${W2}/manifest`]: () => json(manifest(W2, [[sha('4'), 'core'], [sha('6'), 'lazy']])),
    };
    // A constrained quota: a 100 MB image budget. No wall is open, so only the pin protects one.
    const page = memberPage({ routes, path: '/', quota: 400 * 1024 * 1024 });
    await page.boot();
    const ledger = await page.ledger();
    for (const s of [sha('2'), sha('6')]) {
        await ledger.putAsset({ partition: USER_A, key: s, sha: s, width: null, cache: null, bytes: big, cachedAt: 1, verifiedAt: 1 });
    }
    assert.equal(await page.win.bwAssetReport.setKeepOffline(W1, true), true);
    await page.cache.setKeep3d(false); // runs an eviction pass
    await page.settle();
    assert.ok(await ledger.getAsset(USER_A, sha('2')), 'the pinned wall keeps its lazy asset');
    assert.equal(await ledger.getAsset(USER_A, sha('6')), undefined, 'the unpinned wall lost its');
});

test('the 3D toggle off evicts the 3D tier and the report says so; on again refills it on a desktop', async () => {
    const page = memberPage({ routes: w1Routes() });
    await page.boot();
    assert.equal(wallOf(await reportOf(page), W1).view3d, 5000);

    await page.cache.setKeep3d(false);
    await page.settle();
    let report = await reportOf(page);
    assert.equal(report.keep3d, false);
    assert.equal(report.background3d, false);
    assert.equal(wallOf(report, W1).view3d, 0);
    assert.ok(!keys(page.caches, `bw-a1-${USER_A}-img`).includes(sha('3')));
    assert.equal(wallOf(report, W1).ready, true, 'core and the mark are untouched');

    await page.cache.setKeep3d(true);
    await page.settle();
    await page.cache.filled();
    report = await reportOf(page);
    assert.equal(wallOf(report, W1).view3d, 5000);
});

test('an iPhone Safari tab: the install nudge, mobile 3D, and no background 3D', async () => {
    const page = memberPage({ ua: 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_7 like Mac OS X) AppleWebKit/605.1.15 Version/18.0 Mobile/15E148 Safari/604.1' });
    page.win.bwInstall = { instructions: () => 'In Safari: tap Share, then "Add to Home Screen".' };
    await page.boot();
    const report = await reportOf(page);
    assert.equal(report.iosTab, true);
    assert.equal(report.mobile, true);
    assert.equal(report.background3d, false);
    assert.match(report.installHint, /Add to Home Screen/);

    const installed = memberPage({ ua: 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_7 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148', standalone: true });
    await installed.boot();
    assert.equal((await reportOf(installed)).iosTab, false);
    assert.equal((await reportOf(installed)).installHint, null);
});

test('no real label (none, unknown) reports no caching and no walls, and the writes refuse', async () => {
    for (const label of ['none', 'unknown', null]) {
        const page = memberPage({ label });
        await page.boot();
        const report = await reportOf(page);
        assert.equal(report.caching, false, String(label));
        assert.deepEqual(report.walls, []);
        assert.equal(await page.win.bwAssetReport.clear(), false);
        assert.equal(await page.win.bwAssetReport.setKeepOffline(W1, true), false);
    }
});
