// The asset-cache page modules end to end (asset-cache.js and friends): wipe rules on page load, the manifest round,
// the fill through a fake worker, eviction, the quota-refused fallback, the 3D opt-out and the old-worker case.
// Run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { loadPage, fakeCaches, json, manifest, sha, USER_A, USER_B, KIOSK, ORIGIN } from './fixtures/page-sandbox.mjs';

const W1 = '11111111-1111-1111-1111-111111111111';
const W2 = '22222222-2222-2222-2222-222222222222';
const keys = (caches, name) => [...(caches.stores.get(name) || new Map()).keys()].map(k => k.split('/__bw/')[1]).sort();
const names = caches => [...caches.stores.keys()].sort();
const plain = value => JSON.parse(JSON.stringify(value));
// A route answering one wall's manifest that lists `shas` as core.
const listing = (wallId, ...shas) => ({ [`/api/walls/${wallId}/manifest`]: () => json(manifest(wallId, shas.map(s => [s, 'core']))) });

function standardCaches() {
    return fakeCaches({
        [`bw-a1-${USER_A}-img`]: [`${ORIGIN}/__bw/${sha('a')}`],
        [`bw-a1-${USER_B}-img`]: [`${ORIGIN}/__bw/${sha('b')}`],
        'bw-a1-anon-img': [`${ORIGIN}/__bw/${sha('c')}`],
        'bw-sw-state': ['/__bw/partition'],
        'blocwerk-shell-v4': ['/offline.html'],
        'blocwerk-offline': ['x'],
    });
}

test('rule 1: a page under another identity wipes every other partition, never the worker state, shell or offline queue', async () => {
    const caches = standardCaches();
    const ledger = loadPage().win.bwAssetLedger.memory();
    await ledger.setMeta({ activePartition: USER_B });

    const drafts = [];
    const page = loadPage({ label: USER_A, caches, ledger, swReply: 'none', summary: { wallsWith3d: 0, walls: [W1] }, routes: listing(W1, sha('a')), deployDrafts: { clearAll: () => drafts.push('cleared') } });
    await page.boot();

    assert.deepEqual(names(caches), ['blocwerk-offline', 'blocwerk-shell-v4', `bw-a1-${USER_A}-img`, 'bw-sw-state'].sort());
    assert.equal((await ledger.getMeta()).activePartition, USER_A);
    assert.deepEqual(keys(caches, `bw-a1-${USER_A}-img`), [sha('a')]);
    assert.deepEqual(plain(page.events), ['bw:identity-changed']);
    assert.deepEqual(drafts, ['cleared']);
});

test('rule 1: a missing meta and `none` wipe everything; `offline` decides nothing', async () => {
    for (const label of [null, 'none']) {
        const caches = standardCaches();
        const page = loadPage({ label, caches });
        await page.boot();
        assert.ok(!names(caches).some(n => n.startsWith('bw-a1-')), `label ${label}`);
        assert.equal(page.assetFetches().length, 0);
    }
    const caches = standardCaches();
    const page = loadPage({ label: 'offline', caches });
    await page.boot();
    assert.equal(names(caches).filter(n => n.startsWith('bw-a1-')).length, 3);
});

test('rule 1: a first run fires no identity change, and a missing bwDeployDrafts is harmless', async () => {
    const page = loadPage({ label: USER_A, swReply: 'none' });
    await page.boot();
    assert.deepEqual(plain(page.events), []);
    const next = loadPage({ label: USER_B, swReply: 'none', ledger: await page.ledger() });
    await next.boot();
    assert.deepEqual(plain(next.events), ['bw:identity-changed']);
});

test('a kiosk member\'s release (same partition, another acting user) fires the identity change and wipes nothing', async () => {
    const caches = fakeCaches({ [`bw-a1-${KIOSK}-img`]: [`${ORIGIN}/__bw/${sha('k')}`] });
    const routes = listing(W1, sha('k'));
    const summary = { wallsWith3d: 0, walls: [W1] };
    const member = loadPage({ label: KIOSK, user: 'member-id', caches, routes, summary, swReply: 'none' });
    await member.boot();
    const drafts = [];
    const released = loadPage({ label: KIOSK, user: '', caches, routes, summary, swReply: 'none', ledger: await member.ledger(), deployDrafts: { clearAll: () => drafts.push('cleared') } });
    await released.boot();

    assert.deepEqual(plain(released.events), ['bw:identity-changed']);
    assert.deepEqual(drafts, ['cleared']);
    assert.deepEqual(keys(caches, `bw-a1-${KIOSK}-img`), [sha('k')]);
    const again = loadPage({ label: KIOSK, user: '', caches, routes, summary, swReply: 'none', ledger: await member.ledger() });
    await again.boot();
    assert.deepEqual(plain(again.events), []);
    const signedIn = loadPage({ label: KIOSK, user: 'member-id', caches, routes, summary, swReply: 'none', ledger: await member.ledger() });
    await signedIn.boot();
    const offline = loadPage({ label: 'offline', user: '', ledger: await member.ledger(), swReply: 'none' });
    await offline.boot();
    const back = loadPage({ label: KIOSK, user: 'member-id', caches, routes, summary, swReply: 'none', ledger: await member.ledger() });
    await back.boot();
    assert.deepEqual(plain(offline.events.concat(back.events)), [], 'offline.html decides nothing');
});

test('rule 6: an anonymous page without a share token wipes anon; with one, anon is kept', async () => {
    const caches = standardCaches();
    const shared = loadPage({ label: 'anon', caches, path: `/walls/${W1}/shared/tok`, swReply: 'none', routes: listing(W1, sha('c')) });
    await shared.boot();
    assert.deepEqual(keys(caches, 'bw-a1-anon-img'), [sha('c')]);

    const drafts = [];
    const tokenless = loadPage({ label: 'anon', caches, path: '/login', swReply: 'none', ledger: await shared.ledger(), deployDrafts: { clearAll: () => drafts.push('cleared') } });
    await tokenless.boot();
    assert.ok(!names(caches).some(n => n.startsWith('bw-a1-')));
    assert.deepEqual(plain(tokenless.events), ['bw:identity-changed']);
    assert.deepEqual(drafts, ['cleared']);
});

test('rule 2: the logout link wipes every partition first, then navigates; kiosk release does not wipe', async () => {
    const caches = standardCaches();
    const drafts = [];
    const page = loadPage({ label: USER_A, caches, swReply: 'none', deployDrafts: { clearAll: () => drafts.push('cleared') } });
    await page.boot();
    let prevented = false;
    const link = { href: `${ORIGIN}/account/logout`, getAttribute: () => '/account/logout', closest: () => link };
    page.docEvent('click', { target: link, preventDefault: () => { prevented = true; }, defaultPrevented: false });
    await page.settle();

    assert.ok(prevented);
    assert.ok(!names(caches).some(n => n.startsWith('bw-a1-')));
    assert.deepEqual(page.assigned, [`${ORIGIN}/account/logout`]);
    assert.deepEqual(plain(page.events), ['bw:identity-changed']);
    assert.deepEqual(drafts, ['cleared']);
    assert.equal((await (await page.ledger()).getMeta()).activePartition, null);

    const kiosk = fakeCaches({ [`bw-a1-${KIOSK}-img`]: [`${ORIGIN}/__bw/${sha('k')}`] });
    const tablet = loadPage({ label: KIOSK, caches: kiosk, swReply: 'none', summary: { wallsWith3d: 0, walls: [W1] }, routes: listing(W1, sha('k')) });
    await tablet.boot();
    let released = false;
    const form = { dataset: {}, getAttribute: () => '/kiosk/release', submit: () => { released = true; } };
    tablet.docEvent('submit', { target: form, preventDefault: () => assert.fail('release must not be held') });
    await tablet.settle();
    assert.deepEqual(keys(kiosk, `bw-a1-${KIOSK}-img`), [sha('k')]);

    const unregister = { dataset: {}, getAttribute: () => '/kiosk/unregister', submit: () => { released = true; } };
    tablet.docEvent('submit', { target: unregister, preventDefault: () => {} });
    await tablet.settle();
    assert.ok(released);
    assert.ok(!names(kiosk).some(n => n.startsWith('bw-a1-')));
});

function memberPage(more = {}) {
    const routes = Object.assign({
        [`/api/walls/${W1}/manifest`]: () => json(manifest(W1, [[sha('1'), 'core'], [sha('2'), 'lazy'], [sha('3'), 'view3d', { kind: 'texture' }]]), 200, { etag: 'W/"m1"' }),
        [`/api/walls/${W2}/manifest`]: () => json(manifest(W2, [[sha('4'), 'core', { widths: [640, 1280, 1920, 2560] }]]), 200, { etag: 'W/"m2"' }),
    }, more.routes || {});
    return loadPage(Object.assign({ label: USER_A, summary: { wallsWith3d: 1, walls: [W1, W2] }, path: `/walls/${W2}` }, more, { routes }));
}

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

test('rule 4: a manifest 404 drops the wall; an asset another wall lists stays', async () => {
    const caches = fakeCaches({ [`bw-a1-${USER_A}-img`]: [`${ORIGIN}/__bw/${sha('1')}`, `${ORIGIN}/__bw/${sha('9')}`] });
    const first = memberPage({ caches, swReply: 'none', routes: { [`/api/walls/${W2}/manifest`]: () => json(manifest(W2, [[sha('1'), 'core'], [sha('9'), 'core']])) } });
    await first.boot();

    const page = memberPage({ caches, swReply: 'none', ledger: await first.ledger(), routes: { [`/api/walls/${W2}/manifest`]: () => new Response('', { status: 404 }) } });
    await page.boot();

    const ledger = await page.ledger();
    assert.equal(await ledger.getWall(USER_A, W2), undefined);
    assert.deepEqual(keys(caches, `bw-a1-${USER_A}-img`), [sha('1')]);
});

test('rule 5: a wall that left the viewer\'s list is dropped on start, and dropWall drops on demand', async () => {
    const first = memberPage({ swReply: 'none' });
    await first.boot();
    const ledger = await first.ledger();
    assert.equal((await ledger.wallsOf(USER_A)).length, 2);

    const page = memberPage({ swReply: 'none', ledger, path: '/', summary: { wallsWith3d: 0, walls: [W1] } });
    await page.boot();
    assert.deepEqual(plain((await ledger.wallsOf(USER_A)).map(w => w.wallId)), [W1]);

    await page.cache.dropWall(W1);
    assert.deepEqual(plain(await ledger.wallsOf(USER_A)), []);
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
    await ledger.putWall(row);

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
