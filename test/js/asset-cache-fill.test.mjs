// The asset-cache page modules end to end: the manifest round, the fill through a fake worker, eviction, the refused
// and slow-write fallbacks, the 3D opt-out, the old-worker case, resuming a stopped fill and the storage notice.
// Run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { loadPage, fakeCaches, json, manifest, sha, USER_A, USER_B, KIOSK, ORIGIN } from './fixtures/page-sandbox.mjs';
import { W1, W2, keys, names, plain, listing, standardCaches, memberPage } from './fixtures/asset-pages.mjs';

test('the fill: core of every member wall, then 3D; lazy waits for use; the ledger records what the worker stored', async () => {
    const page = memberPage();
    await page.boot();

    const fetched = page.assetFetches().map(f => f.path.split('/a/')[1]);
    assert.deepEqual(fetched, [`${sha('4')}/1280`, `${sha('4')}/640`, sha('1'), sha('3')]);
    assert.ok(page.assetFetches().every(f => f.init.priority === 'low'));
    const ledger = await page.ledger();
    assert.deepEqual(plain((await ledger.assetsOf(USER_A)).map(a => a.key).sort()), [sha('1'), sha('3'), `${sha('4')}/1280`, `${sha('4')}/640`].sort());
    assert.equal((await ledger.getWall(USER_A, W2.toLowerCase())).etag, 'W/"m2"');
});

test('the 3D opt-out: no view3d is fetched, and turning it off later evicts what is cached', async () => {
    const off = memberPage({ prefs3d: false });
    await off.boot();
    assert.ok(!off.assetFetches().some(f => f.path.endsWith(sha('3'))));

    const on = memberPage();
    await on.boot();
    assert.ok(on.assetFetches().some(f => f.path.endsWith(sha('3'))));
    await on.cache.setKeep3d(false);
    await on.settle();
    assert.ok(!keys(on.caches, `bw-a1-${USER_A}-img`).includes(sha('3')));
    assert.equal(on.win.bwPrefs.getCache3d(), false);
});

test('a refused write (quota, full device) frees one wall, retries once, then streams with no error', async () => {
    const caches = fakeCaches({ [`bw-a1-${USER_A}-img`]: [`${ORIGIN}/__bw/${sha('2')}`] });
    const page = memberPage({ caches, refuse: true, connection: { saveData: true } });
    await page.boot();

    const tries = page.assetFetches().map(f => f.path.split('/a/')[1]);
    assert.deepEqual(plain(tries), [`${sha('4')}/1280`, `${sha('4')}/1280`]);
    assert.deepEqual(keys(caches, `bw-a1-${USER_A}-img`), [], 'the oldest wall\'s lazy asset was freed for the retry');
    assert.equal(page.cache.state().runner.streaming, true);
    await page.cache.manifestChanged(W1);
    await page.settle();
    assert.equal(page.assetFetches().length, 2, 'a streaming page starts no further fill');
});

test('an old worker that ignores sw-info gets no background fill, and nothing breaks', async () => {
    for (const swReply of ['none', 'v3', null]) {
        const page = memberPage({ swReply });
        await page.boot();
        assert.equal(page.assetFetches().length, 0, `worker ${swReply}`);
        assert.ok(page.fetches.some(f => f.path.endsWith('/manifest')));
    }
});

test('pending > 0 asks for the manifest again later, and fills what was stamped meanwhile', async () => {
    let calls = 0;
    const page = memberPage({
        summary: { wallsWith3d: 0, walls: [W1] }, path: '/',
        routes: {
            [`/api/walls/${W1}/manifest`]: () => {
                calls++;
                return calls === 1
                    ? json(manifest(W1, [[sha('1'), 'core']], { pending: 1 }), 200, { etag: 'W/"p1"' })
                    : json(manifest(W1, [[sha('1'), 'core'], [sha('5'), 'core']]), 200, { etag: 'W/"p2"' });
            },
        },
    });
    await page.boot();
    assert.equal(calls, 1);
    assert.ok(page.timers.some(t => t.ms === 60000 && !t.done));

    await page.fireLong();
    assert.equal(calls, 2);
    assert.ok(page.assetFetches().some(f => f.path.endsWith(sha('5'))));
    assert.ok(!page.timers.some(t => t.ms === 60000 && !t.done));
});

test('the reconcile: ledger rows whose entry the browser emptied go; entries the worker stored alone are added', async () => {
    const caches = fakeCaches();
    const first = memberPage({ caches, prefs3d: false });
    await first.boot();
    const ledger = await first.ledger();
    caches.stores.get(`bw-a1-${USER_A}-img`).delete(`${ORIGIN}/__bw/${sha('1')}`);
    (await caches.open(`bw-a1-${USER_A}-3d`)).put(`${ORIGIN}/__bw/${sha('3')}`, new Response('x', { headers: { 'content-length': '42' } }));

    const page = memberPage({ caches, ledger, swReply: 'none' });
    await page.boot();
    const rows = await ledger.assetsOf(USER_A);
    assert.ok(!rows.some(r => r.key === sha('1')));
    assert.equal(rows.find(r => r.key === sha('3')).bytes, 42);
    assert.equal(rows.find(r => r.key === sha('3')).cache, '3d');
});

test('retention: a manifest copy older than 30 days is fetched whole, without If-None-Match', async () => {
    const first = memberPage({ swReply: 'none' });
    await first.boot();
    const ledger = await first.ledger();
    const row = await ledger.getWall(USER_A, W1);
    row.fetchedAt = Date.now() - 31 * 24 * 3600 * 1000;
    await ledger.updateWall(USER_A, W1, () => row);

    const page = memberPage({ swReply: 'none', ledger });
    await page.boot();
    const calls = page.fetches.filter(f => f.path === `/api/walls/${W1}/manifest`);
    assert.equal(calls[0].init.headers['If-None-Match'], undefined);
    const fresh = page.fetches.filter(f => f.path === `/api/walls/${W2}/manifest`);
    assert.equal(fresh[0].init.headers['If-None-Match'], 'W/"m2"');
});

test('a share link fills its wall with the token; a kiosk fills only the wall the summary lists', async () => {
    const shared = loadPage({
        label: 'anon', path: `/walls/${W1}/shared/tok`,
        routes: { [`/api/walls/${W1}/manifest`]: (init, url) => (url.search === '?token=tok' ? json(manifest(W1, [[sha('1'), 'core']])) : new Response('', { status: 404 })) },
    });
    await shared.boot();
    assert.deepEqual(shared.assetFetches().map(f => f.search), ['?token=tok']);

    const tablet = memberPage({ label: KIOSK, path: '/', summary: { wallsWith3d: 1, walls: [W1] } });
    await tablet.boot();
    assert.ok(!tablet.fetches.some(f => f.path === `/api/walls/${W2}/manifest`));
});

test('the storage notice: shown to a member of 3 walls with 3D, dismissed per user', async () => {
    const page = memberPage({ swReply: 'none', summary: { wallsWith3d: 3, walls: [W1] } });
    page.cache.boot();
    const notice = await page.win.bwAssetNotice.current();
    assert.equal(notice.kind, 'high-storage');
    page.win.bwAssetNotice.dismiss('high-storage');
    assert.equal(await page.win.bwAssetNotice.current(), null);
    assert.ok([...page.storage.keys()].some(k => k.endsWith(USER_A)));

    const other = memberPage({ label: USER_B, swReply: 'none', summary: { wallsWith3d: 3, walls: [W1] } });
    other.storage.set(`blocwerk-cache-notice:high-storage:${USER_A}`, '1');
    other.cache.boot();
    assert.equal((await other.win.bwAssetNotice.current()).kind, 'high-storage');
});

test('a fill stopped by a hidden page resumes when it is visible again', async () => {
    const page = memberPage();
    page.win.document.visibilityState = 'hidden';
    await page.boot();
    assert.equal(page.assetFetches().length, 0);
    page.win.document.visibilityState = 'visible';
    page.docEvent('visibilitychange', {});
    await page.settle();
    await page.cache.filled();
    await page.settle();
    assert.equal(page.assetFetches().length, 4);
});

test('a fill stopped by a network failure resumes on `online`, and fetches what it missed', async () => {
    let down = true;
    const page = memberPage({ onLine: false, routes: { [`/api/walls/${W2}/a/${sha('4')}/1280`]: () => { if (down) { throw new TypeError('Failed to fetch'); } return new Response('x'); } } });
    await page.boot();
    const before = page.assetFetches().length;
    down = false;
    page.win.navigator.onLine = true;
    page.winEvent('online');
    await page.settle();
    await page.cache.filled();
    await page.settle();
    assert.ok(page.assetFetches().length > before);
    assert.ok(page.assetFetches().filter(f => f.path.endsWith(`${sha('4')}/1280`)).length >= 2);
});

test('a slow write (the worker confirms nothing in time) neither frees a wall nor stops the fill for good', async () => {
    const caches = fakeCaches({ [`bw-a1-${USER_A}-img`]: [`${ORIGIN}/__bw/${sha('2')}`] });
    const page = memberPage({ caches, silent: true, connection: { saveData: true } });
    await page.boot();
    assert.equal(page.cache.state().runner.streaming, false);
    assert.deepEqual(keys(caches, `bw-a1-${USER_A}-img`), [sha('2')], 'no wall was freed for a timeout');
    assert.ok(page.timers.some(t => t.ms === 30000 && !t.done), 'the fill tries again later');
});

test('manifests are fetched in parallel, at most 3 at a time', async () => {
    const walls = Array.from({ length: 7 }, (_, i) => `0000000${i}-0000-0000-0000-000000000000`);
    let inFlight = 0;
    let peak = 0;
    const routes = Object.fromEntries(walls.map(w => [`/api/walls/${w}/manifest`, async () => {
        inFlight++;
        peak = Math.max(peak, inFlight);
        await new Promise(resolve => setTimeout(resolve, 5));
        inFlight--;
        return json(manifest(w, []));
    }]));
    const page = loadPage({ label: USER_A, path: '/', swReply: 'none', summary: { wallsWith3d: 0, walls }, routes });
    await page.boot();
    assert.equal(page.fetches.filter(f => f.path.endsWith('/manifest')).length, 7);
    assert.equal(peak, 3);
});

test('a worker serving another partition than the page\'s gets no fill, and the fill tries again later', async () => {
    const page = memberPage({ swPartition: USER_B });
    await page.boot();
    assert.equal(page.assetFetches().length, 0);
    assert.ok(page.posted.some(m => m.type === 'sw-refresh-partition' && m.proto === 1), 'the worker is asked to look again');
    assert.ok(page.timers.some(t => t.ms === 30000 && !t.done));
    const same = memberPage({ swPartition: USER_A });
    await same.boot();
    assert.equal(same.assetFetches().length, 4);
});

test('a worker whose refresh answers with the page\'s label fills at once, without waiting out the backoff', async () => {
    const page = memberPage({ swPartition: USER_B, swRefreshed: USER_A });
    await page.boot();
    assert.ok(page.posted.some(m => m.type === 'sw-refresh-partition'));
    assert.equal(page.assetFetches().length, 4);
    assert.ok(!page.timers.some(t => t.ms === 30000 && !t.done), 'no backed-off retry is pending');
    const still = memberPage({ swPartition: USER_B, swRefreshed: USER_B });
    await still.boot();
    assert.equal(still.assetFetches().length, 0);
    assert.ok(still.timers.some(t => t.ms === 30000 && !t.done));
});

test('a phone gets no 3D background fill, and the hook for 2.8 says so', async () => {
    const phone = memberPage({ ua: 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_7 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148' });
    await phone.boot();
    assert.ok(!phone.assetFetches().some(f => f.path.endsWith(sha('3'))));
    assert.ok(phone.assetFetches().some(f => f.path.endsWith(sha('1'))));
    assert.equal(phone.cache.fills3dInBackground(), false);
    assert.equal(memberPage().cache.fills3dInBackground(), true);
});

test('a manifest that stays pending backs off its refetch instead of asking every minute', async () => {
    const page = memberPage({ summary: { wallsWith3d: 0, walls: [W1] }, path: '/', routes: { [`/api/walls/${W1}/manifest`]: () => json(manifest(W1, [[sha('1'), 'core']], { pending: 2 })) } });
    await page.boot();
    const waits = [];
    for (let i = 0; i < 4; i++) {
        waits.push(page.timers.filter(t => !t.done && t.ms >= 60000).map(t => t.ms)[0]);
        await page.fireLong();
    }
    assert.deepEqual(waits, [60000, 120000, 300000, 900000]);
});
