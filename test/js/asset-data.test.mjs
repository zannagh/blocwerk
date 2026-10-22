// The data documents in the page cache (Phase 3 PR 3.3, asset-data.js) against the real page modules: the docs index
// after each manifest round, the row's `data` field beside the manifest's and the view3d fields, the fill downloading
// each document like core, eviction never taking one as an orphan, dropWall and a 403 removing them, a rebuilt
// document's old sha going at once, the kiosk's bound wall only, the share token, and the 10 s debounce of
// bwAssetData.changed. Run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { json, sha, USER_A, KIOSK } from './fixtures/page-sandbox.mjs';
import { W1, W2, keys, plain, memberPage } from './fixtures/asset-pages.mjs';

const KINDS = ['wall', 'holds', 'boulders', 'gallery', 'comments'];
const docSha = (kind, n = 0) => sha('abcde'[KINDS.indexOf(kind)]).slice(0, 63) + String(n);
const index = (docs, etag = '"i1"') => () => json({ cursor: '1.2.3', docs }, 200, { etag });
const docsOf = (n = 0) => KINDS.map(kind => ({ kind, sha: docSha(kind, n), bytes: 100 + KINDS.indexOf(kind), cursor: `1.2.${n}` }));
const docsRoute = (wallId, route) => ({ [`/api/walls/${wallId}/docs`]: route });
const rowOf = async (page, wallId, partition = USER_A) => (await page.ledger()).getWall(partition, wallId);
const docFetches = page => page.dataFetches().filter(f => f.path.includes('/d/'));

test('a manifest round stores the docs index in `data`, and the fill caches all five documents in -3d', async () => {
    const page = memberPage({ routes: docsRoute(W1, index(docsOf())) });
    await page.boot();
    const row = await rowOf(page, W1);
    assert.deepEqual(Object.keys(row.data).sort(), [...KINDS].sort());
    assert.deepEqual(plain(row.data.holds), { sha: docSha('holds'), bytes: 101, cursor: '1.2.0' });
    assert.equal(row.dataEtag, '"i1"');
    assert.deepEqual(docFetches(page).map(f => f.path).sort(), KINDS.map(k => `/api/walls/${W1}/d/${k}/${docSha(k)}`).sort());
    const cached = keys(page.caches, `bw-a1-${USER_A}-3d`);
    KINDS.forEach(k => assert.ok(cached.includes(docSha(k)), `${k} cached`));
    const assets = (await (await page.ledger()).assetsOf(USER_A)).map(a => a.key);
    KINDS.forEach(k => assert.ok(assets.includes(docSha(k)), `${k} in the ledger`));
});

test('the documents are fetched on a metered phone too, with the share token', async () => {
    const page = memberPage({
        label: 'anon', path: `/walls/${W1}/shared/tok`, summary: { wallsWith3d: 0, walls: [] }, ua: 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X)',
        connection: { type: 'cellular' },
        routes: docsRoute(W1, (init, url) => (url.search === '?token=tok' ? index(docsOf())() : new Response('', { status: 403 })))
    });
    await page.boot();
    assert.equal(docFetches(page).length, 5);
    assert.ok(page.dataFetches().every(f => f.search === '?token=tok'));
});

test('the manifest round and a 3D view\'s noteDoc keep `data`; data and view3d documents coexist and are never orphans', async () => {
    const page = memberPage({ routes: docsRoute(W1, index(docsOf())) });
    await page.boot();
    await page.win.bwAsset3d.noteDoc(W1, sha('9'), 50);
    await page.cache.manifestChanged(W1); // a 200: the manifest round writes its fields again
    await page.settle();
    const row = await rowOf(page, W1);
    assert.deepEqual(Object.keys(row.data).sort(), [...KINDS].sort(), 'the manifest round wrote only its own fields');
    assert.equal(row.docs[0].sha, sha('9'));

    const budget = page.win.bwAssetBudget;
    const assets = KINDS.map(k => ({ key: docSha(k), sha: docSha(k), bytes: 100, cachedAt: 0 })).concat([{ key: sha('9'), sha: sha('9'), bytes: 50, cachedAt: 0 }]);
    const items = budget.classify([row], assets);
    assert.deepEqual(plain(items.map(i => i.tier)), ['data', 'data', 'data', 'data', 'data', 'view3d']);
    const evicted = budget.planEviction({ walls: [row], assets, budget: { images: 0, view3d: 1e9 }, openWallId: null, keep3d: true, orphans: true, now: 1e15 });
    assert.deepEqual(plain(evicted), [], 'over every budget and past the orphan grace, a listed document stays');
    assert.deepEqual(plain(budget.oneWallToFree({ walls: [row], assets, openWallId: null }).filter(k => KINDS.some(kind => docSha(kind) === k))), []);
});

test('an unchanged index answers 304 to If-None-Match and fetches no document again', async () => {
    const seen = [];
    const page = memberPage({ routes: docsRoute(W1, init => {
        seen.push(init.headers['If-None-Match'] || null);
        return seen.length > 1 ? new Response(null, { status: 304 }) : index(docsOf())();
    }) });
    await page.boot();
    const before = docFetches(page).length;
    await page.cache.manifestChanged(W1);
    await page.settle();
    assert.deepEqual(seen, [null, '"i1"']);
    assert.equal(docFetches(page).length, before);
    assert.equal(Object.keys((await rowOf(page, W1)).data).length, 5);
});

test('a rebuilt document replaces its sha, and the old one leaves the device at once', async () => {
    let n = 0;
    const page = memberPage({ routes: docsRoute(W1, () => index(docsOf(n), `"i${n}"`)()) });
    await page.boot();
    assert.ok(keys(page.caches, `bw-a1-${USER_A}-3d`).includes(docSha('comments', 0)));
    n = 1;
    await page.cache.dataChanged(W1);
    await page.cache.filled();
    await page.settle();
    const cached = keys(page.caches, `bw-a1-${USER_A}-3d`);
    assert.ok(cached.includes(docSha('comments', 1)));
    assert.ok(!cached.includes(docSha('comments', 0)), 'a deleted comment is not kept for the orphan grace');
    assert.ok(!(await (await page.ledger()).assetsOf(USER_A)).some(a => a.sha === docSha('comments', 0)));
});

test('dropWall removes the wall\'s documents; a 403 index drops the wall like a 403 manifest', async () => {
    const page = memberPage({ routes: docsRoute(W1, index(docsOf())) });
    await page.boot();
    await page.cache.dropWall(W1);
    assert.ok(!keys(page.caches, `bw-a1-${USER_A}-3d`).some(k => KINDS.some(kind => docSha(kind) === k)));
    assert.equal(await rowOf(page, W1), undefined);

    const again = memberPage({ routes: docsRoute(W1, index(docsOf())) });
    await again.boot();
    assert.ok(await rowOf(again, W1));
    again.win.routes[`/api/walls/${W1}/docs`] = () => new Response('', { status: 403 });
    await again.cache.dataChanged(W1);
    await again.settle();
    assert.equal(await rowOf(again, W1), undefined);
    assert.ok(!(await (await again.ledger()).assetsOf(USER_A)).some(a => a.sha === docSha('holds')));
});

test('a 401, a 5xx or no network keep the stored documents', async () => {
    for (const failing of [() => new Response('', { status: 401 }), () => new Response('', { status: 503 }), () => { throw new TypeError('offline'); }]) {
        const page = memberPage({ routes: docsRoute(W1, index(docsOf())) });
        await page.boot();
        page.win.routes[`/api/walls/${W1}/docs`] = failing;
        await page.cache.dataChanged(W1);
        await page.settle();
        assert.equal(Object.keys((await rowOf(page, W1)).data).length, 5);
    }
});

test('a kiosk refreshes its bound wall only; changed() for any other wall fetches nothing', async () => {
    const tablet = memberPage({
        label: KIOSK, path: '/', summary: { wallsWith3d: 0, walls: [W1] },
        routes: Object.assign(docsRoute(W1, index(docsOf())), docsRoute(W2, index(docsOf(2))))
    });
    await tablet.boot();
    assert.equal(Object.keys((await rowOf(tablet, W1, KIOSK)).data).length, 5);
    assert.ok(!tablet.fetches.some(f => f.path === `/api/walls/${W2}/docs`));
    await tablet.cache.dataChanged(W2);
    await tablet.settle();
    assert.ok(!tablet.fetches.some(f => f.path === `/api/walls/${W2}/docs`), 'no row, no refresh: nothing of another wall');
    assert.equal(await rowOf(tablet, W2, KIOSK), undefined);
});

test('changed() refreshes once per 10 s burst, at its end', async () => {
    const page = memberPage({ routes: docsRoute(W1, index(docsOf())) });
    await page.boot();
    const count = () => page.fetches.filter(f => f.path === `/api/walls/${W1}/docs`).length;
    const before = count();
    for (let i = 0; i < 5; i++) {
        page.win.bwAssetData.changed(W1.toUpperCase());
    }
    await page.settle();
    assert.equal(count(), before, 'nothing before the debounce ends');
    assert.equal(page.timers.filter(t => !t.done && t.ms === 10000).length, 1);
    await page.fireLong();
    assert.equal(count(), before + 1);
    page.win.bwAssetData.changed(W1);
    await page.fireLong();
    assert.equal(count(), before + 2, 'the next burst refreshes again');

    const manifests = () => page.fetches.filter(f => f.path === `/api/walls/${W1}/manifest`).length;
    const manifestsBefore = manifests();
    page.win.bwAssetData.changed(W1);
    page.win.bwAssetData.changed(W1, true); // a wall.manifest within the burst
    page.win.bwAssetData.changed(W1);
    await page.fireLong();
    assert.equal(manifests(), manifestsBefore + 1, 'a burst with a manifest event refetches the manifest once');
    assert.equal(count(), before + 3, 'and the docs index after it');
});

test('compact keeps well-formed kinds and shas only, never view3d', () => {
    const page = memberPage();
    const data = page.win.bwAssetData.compact({ docs: [
        { kind: 'holds', sha: sha('1'), bytes: 3, cursor: '1.1.1' }, { kind: 'view3d', sha: sha('2') },
        { kind: '../x', sha: sha('3') }, { kind: 'wall', sha: 'nope' }, null
    ] });
    assert.deepEqual(plain(data), { holds: { sha: sha('1'), bytes: 3, cursor: '1.1.1' } });
});
