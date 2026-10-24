// Settings → "Offline storage" and the "Offline mode available" bubble (asset-report.js, PR 2.9) against the real page modules:
// the report's sizes come from the ledger, the readiness rule, the per-wall pin and the 3D opt-out's eviction
// ("Clear offline data": asset-clear.test.mjs). Run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { json, manifest, sha, USER_A, KIOSK } from './fixtures/page-sandbox.mjs';
import { W1, W2, keys, plain, memberPage, w1Routes } from './fixtures/asset-pages.mjs';

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

// The bubbles the page asked for: bwStatus.show(kind, opts) recorded instead of drawn.
function bubbles(page) {
    const shown = [];
    page.win.bwStatus = { show: kind => { shown.push(kind); return true; } };
    return shown;
}

test('"Offline mode available" once the walls are ready, not again on the next load, a rotation, or a clear and its refill', async () => {
    const storage = new Map();
    const page = memberPage({ routes: w1Routes(), storage });
    const shown = bubbles(page);
    page.win.bwAssetReport.observe();
    await page.boot();
    await page.settle();
    assert.deepEqual(shown, ['offline-ready'], 'both walls became ready on this device');

    const again = memberPage({ routes: w1Routes(), storage, ledger: await page.ledger(), caches: page.caches });
    const shownAgain = bubbles(again);
    again.win.bwAssetReport.observe();
    await again.boot();
    await again.settle();
    assert.deepEqual(shownAgain, [], 'the same walls ready on the next load say nothing');

    again.win.innerWidth = 900;
    await again.win.bwAssetReport.readiness.check();
    assert.deepEqual(shownAgain, [], 'a landscape viewport wanting a wider rendition does not make a wall "new"');

    await again.win.bwAssetReport.clear();
    await again.settle();
    await again.cache.filled();
    await again.win.bwAssetReport.readiness.check();
    assert.deepEqual(shownAgain, [], 'cleared and filled again within 10 minutes: quiet');
});

test('a wall that is not ready for a while (a new panel photo pending) stays told', async () => {
    const storage = new Map();
    const page = memberPage({ routes: w1Routes(), storage });
    const shown = bubbles(page);
    page.win.bwAssetReport.observe();
    await page.boot();
    await page.settle();
    const key = `blocwerk-offline-ready:${USER_A}`;
    // Long after the bubble, both walls still remembered though one is pending now.
    storage.set(key, JSON.stringify({ walls: [W1, W2], toldAt: Date.now() - 11 * 60000 }));
    const ledger = await page.ledger();
    await ledger.updateWall(USER_A, W2, w => Object.assign(w, { pending: 1 }));
    await page.win.bwAssetReport.readiness.check();
    await ledger.updateWall(USER_A, W2, w => Object.assign(w, { pending: 0 }));
    await page.win.bwAssetReport.readiness.check();
    assert.deepEqual(shown, ['offline-ready'], 'only the first time');
    assert.deepEqual(JSON.parse(storage.get(key)).walls, [W1, W2].sort());
});

test('a fill that finishes while another bubble is up asks again, at most 10 times, and tells once the pill is free', async () => {
    const storage = new Map();
    const page = memberPage({ routes: w1Routes(), storage });
    const shown = [];
    let busy = true;
    let asked = 0;
    page.win.bwStatus = { show: kind => { asked++; if (busy) { return false; } shown.push(kind); return true; } };
    page.win.bwAssetReport.observe();
    await page.boot();
    await page.settle();
    await page.settle();
    assert.deepEqual(shown, []);
    assert.equal(storage.get(`blocwerk-offline-ready:${USER_A}`), undefined, 'not marked told, no quiet window started');
    // The load's check and the fill's announcements ask too; the retries add at most 10.
    assert.ok(asked > 2 && asked <= 13, `asked again, but not forever (${asked})`);
    const settled = asked;
    await page.settle();
    assert.equal(asked, settled, 'and then stops');

    busy = false;
    await page.win.bwAssetReport.readiness.check();
    assert.deepEqual(shown, ['offline-ready']);
    assert.ok(JSON.parse(storage.get(`blocwerk-offline-ready:${USER_A}`)).toldAt > 0);
});

test('a kiosk page never asks again, whatever its partition', async () => {
    const page = memberPage({ routes: w1Routes(), kioskKey: 'kiosk-1' });
    let asked = 0;
    page.win.bwStatus = { show: () => { asked++; return false; } };
    page.win.bwAssetReport.observe();
    await page.boot();
    await page.settle();
    await page.settle();
    // Only the checks the page itself runs (its load, the fill's announcements), no retry after them.
    assert.ok(asked <= 2, `asked ${asked} times`);
});

test('walls that finish one after the other are told once: another wall ready within 10 minutes of the last bubble says nothing', async () => {
    const key = `blocwerk-offline-ready:${USER_A}`;
    const recent = new Map([[key, JSON.stringify({ walls: [W2], toldAt: Date.now() - 60000 })]]);
    const page = memberPage({ routes: w1Routes(), storage: recent });
    const shown = bubbles(page);
    page.win.bwAssetReport.observe();
    await page.boot();
    await page.settle();
    assert.deepEqual(shown, []);
    assert.deepEqual(JSON.parse(recent.get(key)).walls, [W1, W2].sort(), 'the new wall is remembered all the same');

    const old = new Map([[key, JSON.stringify({ walls: [W2], toldAt: Date.now() - 11 * 60000 })]]);
    const later = memberPage({ routes: w1Routes(), storage: old });
    const told = bubbles(later);
    later.win.bwAssetReport.observe();
    await later.boot();
    await later.settle();
    assert.deepEqual(told, ['offline-ready']);
});

test('a wall whose core is not cached yet is not offline ready, and a kiosk is never told', async () => {
    const pending = memberPage({ swReply: 'none' });
    const shown = bubbles(pending);
    pending.win.bwAssetReport.observe();
    await pending.boot();
    await pending.settle();
    assert.equal(pending.assetFetches().length, 0, 'an old worker: no fill');
    assert.deepEqual(shown, []);
    assert.equal(wallOf(await reportOf(pending), W2).ready, false);

    const tablet = memberPage({ label: KIOSK, routes: w1Routes() });
    const told = bubbles(tablet);
    tablet.win.bwAssetReport.observe();
    await tablet.boot();
    await tablet.settle();
    await tablet.win.bwAssetReport.readiness.check();
    assert.ok((await tablet.win.bwAssetReport.readiness.readySet()).ready.size > 0, 'the kiosk\'s walls are ready all the same');
    assert.deepEqual(told, []);
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
    assert.equal(wallOf(report, W1).ready, true, 'core and its readiness are untouched');

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
