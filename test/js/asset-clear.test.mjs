// Settings → "Clear offline data" (asset-clear.js, PR 2.9) against the real page modules: the partition-only wipe, the
// bounded stop, the immediate rebuild (wall data is always cached), the other tabs, and the save-3D unpin's eviction.
// Run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { sha, USER_A, fakeCaches } from './fixtures/page-sandbox.mjs';
import { W1, keys, names, plain, standardCaches, memberPage, w1Routes } from './fixtures/asset-pages.mjs';

// Records every cache deletion, with how many asset fetches had happened by then.
function spyDeletes(caches, page) {
    const deleted = [];
    const original = caches.delete;
    caches.delete = async name => { deleted.push({ name, fetches: page().assetFetches().length }); return original(name); };
    return deleted;
}

const reportOf = page => page.win.bwAssetReport.report().then(plain);
const settled = async page => { await page.settle(); await page.cache.filled(); await page.settle(); };

test('clear wipes only this partition, keeps the queue, the worker state, the shell and the label, then rebuilds core at once', async () => {
    const caches = standardCaches();
    let page = null;
    const deleted = spyDeletes(caches, () => page);
    page = memberPage({ caches, routes: w1Routes() });
    await page.boot();
    deleted.length = 0;
    const before = page.assetFetches().length;

    assert.equal(await page.win.bwAssetReport.clear(), true);
    assert.deepEqual(deleted.map(d => d.name).sort(), [`bw-a1-${USER_A}-3d`, `bw-a1-${USER_A}-img`]);
    assert.ok(['blocwerk-offline', 'blocwerk-shell-v4', 'bw-sw-state'].every(n => names(caches).includes(n)));
    const ledger = await page.ledger();
    assert.equal((await ledger.getMeta()).activePartition, USER_A, 'no wipe or identity change on the next load');

    await settled(page);
    const state = page.cache.state();
    assert.equal(state.stopped, false, 'caching is back on for this page');
    const refetched = page.assetFetches().slice(before).map(f => f.path.split('/a/')[1]);
    assert.ok(refetched.includes(`${sha('4')}/1280`) && refetched.includes(sha('1')), 'core of both walls downloads again');
    const report = await reportOf(page);
    assert.ok(report.walls.every(w => w.ready), 'both walls are offline ready again');
    assert.equal(report.paused, false);
});

test('clear waits for the request in flight, so the wipe comes after it', async () => {
    const caches = fakeCaches();
    let page = null;
    const deleted = spyDeletes(caches, () => page);
    let release;
    let started;
    const inFlight = new Promise(resolve => { started = resolve; });
    let held = new Promise(resolve => { release = resolve; });
    const gate = () => { started(); return held; };
    // One request at a time (Save-Data); the first asset fetch waits until the clear is under way.
    page = memberPage({ caches, routes: w1Routes(), connection: { saveData: true }, gate });
    await page.cache.boot();
    await inFlight;
    const clearing = page.win.bwAssetReport.clear();
    await page.settle();
    assert.equal(deleted.length, 0, 'no wipe while the request is in flight');
    held = Promise.resolve();
    release();
    await clearing;

    assert.equal(deleted[0].fetches, 1, 'the fill stopped after the request in flight, before the wipe');
    await settled(page);
    assert.ok(page.assetFetches().length > 1, 'then the rebuild fills again');
});

test('a stuck download does not hold the clear: it proceeds after the stop wait', async () => {
    const caches = fakeCaches();
    let page = null;
    const deleted = spyDeletes(caches, () => page);
    let started;
    const inFlight = new Promise(resolve => { started = resolve; });
    page = memberPage({ caches, routes: w1Routes(), connection: { saveData: true }, gate: () => { started(); return new Promise(() => {}); } });
    await page.cache.boot();
    await inFlight;
    let done = false;
    const clearing = page.win.bwAssetReport.clear().then(r => { done = true; return r; });
    await page.settle();
    assert.equal(done, false);

    const stopWait = page.timers.filter(t => !t.done && t.ms === page.win.bwAssetClear.STOP_WAIT_MS);
    assert.equal(stopWait.length, 1);
    stopWait.forEach(t => { t.done = true; t.fn(); });
    assert.equal(await clearing, true);
    assert.ok(deleted.some(d => d.name === `bw-a1-${USER_A}-img`));
});

test('another tab of the same partition stops on "clearing" and rebuilds on "cleared"; another partition ignores it', async () => {
    const caches = fakeCaches();
    const bus = new Set();
    const tabA = memberPage({ caches, bus, routes: w1Routes() });
    tabA.win.bwAssetReport.observe();
    await tabA.boot();
    const ledger = await tabA.ledger();
    const tabB = memberPage({ caches, bus, ledger, routes: w1Routes(), path: '/' });
    tabB.win.bwAssetReport.observe();
    await tabB.boot();
    const other = memberPage({ caches: fakeCaches(), bus, label: 'u-bbbbbbbbbbbbbbbbbbbb' });
    other.win.bwAssetReport.observe();
    await other.boot();
    const manifestsOf = tab => tab.fetches.filter(f => f.path.endsWith('/manifest')).length;
    const [b0, o0] = [manifestsOf(tabB), manifestsOf(other)];

    await tabA.win.bwAssetReport.clear();
    await settled(tabB);
    await settled(tabA);

    assert.ok(manifestsOf(tabB) >= b0 + 2, 'tab B refetched both walls');
    assert.equal(tabB.cache.state().stopped, false);
    assert.equal(manifestsOf(other), o0, 'another identity\'s tab does nothing');
    assert.equal(other.cache.state().stopped, undefined);
});

test('a tab that hears "clearing" but never "cleared" (the clearing tab closed) rebuilds after the resume wait', async () => {
    const bus = new Set();
    const tab = memberPage({ bus });
    tab.win.bwAssetReport.observe();
    await tab.boot();
    const sender = { name: 'bw-asset-clear', onmessage: null };
    bus.add(sender);
    bus.forEach(c => { if (c !== sender && c.name === 'bw-asset-clear') { c.onmessage({ data: { type: 'clearing', partition: USER_A } }); } });
    await tab.settle();
    assert.equal(tab.cache.state().stopped, true);

    await tab.fireLong();
    assert.equal(tab.cache.state().stopped, false);
});

test('unpinning a wall with saved 3D runs eviction, so the opt-out applies to its 3D again', async () => {
    const page = memberPage({ prefs3d: false });
    await page.boot();
    const ledger = await page.ledger();
    await ledger.updateWall(USER_A, W1, row => Object.assign(row, { keepOffline: true, save3d: true }));
    await page.cache.manifestChanged(W1);
    await settled(page);
    assert.ok(await ledger.getAsset(USER_A, sha('3')), 'saved 3D is filled despite the opt-out');

    await page.win.bwAssetReport.setKeepOffline(W1, false);
    await page.settle();
    assert.equal(await ledger.getAsset(USER_A, sha('3')), undefined);
    assert.ok(!keys(page.caches, `bw-a1-${USER_A}-img`).includes(sha('3')));
});

test('without BroadcastChannel the clear goes over localStorage, and a storage event stops the other tab', async () => {
    const sender = memberPage();
    await sender.boot();
    const writes = [];
    const setItem = sender.win.localStorage.setItem;
    sender.win.localStorage.setItem = (k, v) => { writes.push([k, v]); setItem(k, v); };
    sender.win.bwAssetClear.listen();
    await sender.win.bwAssetReport.clear();
    assert.deepEqual(writes.map(([k, v]) => [k, JSON.parse(v).type]), [['bw-asset-clear', 'clearing'], ['bw-asset-clear', 'cleared']]);
    assert.equal(sender.win.localStorage.getItem('bw-asset-clear'), null, 'the key is removed again');

    const tab = memberPage();
    tab.win.bwAssetReport.observe();
    await tab.boot();
    tab.winEvent('storage', { key: 'bw-asset-clear', newValue: writes[0][1] });
    await tab.settle();
    assert.equal(tab.cache.state().stopped, true);
});

test('the rebuild refetches every wall at once: wall 2\'s core is not held behind wall 1\'s 3D', async () => {
    let holding = false;
    let release = () => undefined;
    const held = new Promise(resolve => { release = resolve; });
    // After the clear, wall 1's 3D texture download hangs until released.
    const gate = path => (holding && path.endsWith(sha('3')) ? held : undefined);
    // No wall open, so the rebuild takes the summary's order: wall 1 first.
    const page = memberPage({ routes: w1Routes(), gate, path: '/' });
    await page.boot();
    holding = true;
    const before = page.assetFetches().length;

    await page.win.bwAssetReport.clear();
    await page.settle();
    const after = page.assetFetches().slice(before).map(f => f.path.split('/a/')[1]);
    assert.ok(after.includes(sha('3')), 'wall 1\'s 3D is in flight');
    assert.ok(after.includes(`${sha('4')}/1280`) && after.includes(`${sha('4')}/640`), 'wall 2\'s core came while it was held');
    release();
    await settled(page);
});
