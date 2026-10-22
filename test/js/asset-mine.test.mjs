// The viewer's own ticks and favourites in the page cache (Phase 3 PR 3.4, asset-mine.js) against the real page modules:
// /api/me/walls/{w}/mine after each manifest round of a `u-` page, the ledger's `mine` row, the refusals, the 2 s debounce
// of bwAssetMine.changed, and the privacy matrix: `k-` and `anon` never fetch or write a row, and every wipe of a
// partition (rule 1, logout, the signed-out page, rule 6, dropWall, Clear offline data, the every-start sweep) leaves no
// mine row of the partition it dropped. Run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { loadPage, json, sha, USER_A, USER_B, KIOSK, ORIGIN } from './fixtures/page-sandbox.mjs';
import { W1, W2, plain, listing, memberPage } from './fixtures/asset-pages.mjs';

const B1 = 'b1b1b1b1-0000-4000-8000-000000000001';
const B2 = 'b2b2b2b2-0000-4000-8000-000000000002';
const body = (wallId, more = {}) => Object.assign({
    v: 1, wallId, partition: USER_A, cursor: '1.2.3',
    ticks: [{ boulderId: B1, attempts: 3, sent: true, flashed: false, lastAt: '2026-10-22T10:00:00Z' }],
    favourites: [B2]
}, more);
const mineRoute = (wallId, fn) => ({ [`/api/me/walls/${wallId}/mine`]: fn || (() => json(body(wallId))) });
const mineRoutes = () => Object.assign(mineRoute(W1), mineRoute(W2));
const mineFetches = page => page.fetches.filter(f => /^\/api\/me\/walls\/[^/]+\/mine$/.test(f.path));
const rowsOf = async (page, partition = USER_A) => plain(await (await page.ledger()).mineOf(partition));
const allMine = async ledger => plain(await ledger.mineOf(USER_A)).concat(plain(await ledger.mineOf(USER_B)), plain(await ledger.mineOf(KIOSK)), plain(await ledger.mineOf('anon')));

async function signedInWithMarks(more = {}) {
    const page = memberPage(Object.assign({ swReply: 'none' }, more, { routes: Object.assign(mineRoutes(), more.routes || {}) }));
    await page.boot();
    assert.equal((await rowsOf(page)).length, 2, 'both walls\' marks are stored');
    return page;
}

test('a signed-in page stores each current wall\'s marks in `mine`, fetched no-store with its cookie', async () => {
    const page = await signedInWithMarks();
    const rows = await rowsOf(page);
    const w1 = rows.find(r => r.wallId === W1);
    assert.deepEqual(Object.keys(w1).sort(), ['cursor', 'favourites', 'fetchedAt', 'partition', 'ticks', 'wallId']);
    assert.equal(w1.partition, USER_A);
    assert.equal(w1.cursor, '1.2.3');
    assert.deepEqual(w1.ticks, [{ boulderId: B1, attempts: 3, sent: true, flashed: false, lastAt: '2026-10-22T10:00:00Z' }]);
    assert.deepEqual(w1.favourites, [B2]);
    assert.ok(mineFetches(page).every(f => f.init.cache === 'no-store' && f.init.credentials === 'same-origin'));
    assert.ok(mineFetches(page).every(f => f.search === ''), 'a share token is never sent with the marks');
});

test('a body for another wall, or a malformed one, is not stored; bad ticks and ids are dropped', async () => {
    const page = memberPage({ swReply: 'none', routes: Object.assign(
        mineRoute(W1, () => json(body(W2))),
        mineRoute(W2, () => json(body(W2, { ticks: [{ boulderId: 'nope' }, { boulderId: B1.toUpperCase(), attempts: -4, sent: 'yes' }], favourites: [B2, 7, 'x'] })))) });
    await page.boot();
    const rows = await rowsOf(page);
    assert.deepEqual(rows.map(r => r.wallId), [W2]);
    assert.deepEqual(rows[0].ticks, [{ boulderId: B1, attempts: 0, sent: false, flashed: false, lastAt: null }]);
    assert.deepEqual(rows[0].favourites, [B2]);
});

test('403, 404 and 410 drop the stored marks but not the wall; 401, a 5xx and no network keep them', async () => {
    for (const [status, kept] of [[403, false], [404, false], [410, false], [401, true], [503, true], ['throw', true]]) {
        let answer = null;
        const page = await signedInWithMarks({ routes: mineRoute(W1, () => {
            if (answer === 'throw') {
                throw new TypeError('Failed to fetch');
            }
            return answer ? new Response('{}', { status: answer }) : json(body(W1));
        }) });
        answer = status;
        await page.cache.manifestChanged(W1);
        await page.settle();
        const ledger = await page.ledger();
        assert.equal(!!(await ledger.mineOf(USER_A)).find(r => r.wallId === W1), kept, `status ${status}`);
        assert.ok(await ledger.getWall(USER_A, W1), `the wall row stays on ${status}`);
    }
});

test('a kiosk tablet and a share-link page never ask for marks and never store a row', async () => {
    const routes = Object.assign(mineRoutes(), listing(W1, sha('k')));
    const tablet = loadPage({ label: KIOSK, user: 'member-id', swReply: 'none', summary: { wallsWith3d: 0, walls: [W1] }, routes, path: `/walls/${W1}` });
    await tablet.boot();
    const shared = loadPage({ label: 'anon', swReply: 'none', path: `/walls/${W1}/shared/tok`, routes });
    await shared.boot();
    for (const [page, label] of [[tablet, KIOSK], [shared, 'anon']]) {
        assert.ok(await (await page.ledger()).getWall(label, W1), `${label} cached the wall`);
        assert.deepEqual(mineFetches(page), [], `${label} fetched no marks`);
        page.win.bwAssetMine.changed(W1);
        await page.cache.mineChanged(W1);
        await page.settle();
        assert.equal(await page.win.bwAssetMine.refresh(page.cache.state().env, label, W1), 'skipped');
        // A write that got this far anyway is refused by the session's guard.
        await page.cache.state().env.ledger.putMine({ partition: label, wallId: W1, ticks: [], favourites: [] });
        assert.deepEqual(mineFetches(page), []);
        assert.deepEqual(await allMine(await page.ledger()), [], `${label} holds no mine row`);
    }
});

test('the guard refuses marks for a partition that is not the active one (a stale tab)', async () => {
    let cursor = '1.2.3';
    const page = await signedInWithMarks({ routes: mineRoute(W1, () => json(body(W1, { cursor }))) });
    const ledger = await page.ledger();
    await page.cache.state().env.ledger.putMine({ partition: USER_B, wallId: W1, ticks: [], favourites: [] });
    assert.deepEqual(plain(await ledger.mineOf(USER_B)), [], 'not the active partition');

    await ledger.setMeta({ activePartition: USER_B }); // another tab signed in as B since
    cursor = '9.9.9';
    await page.cache.mineChanged(W1);
    await page.settle();
    assert.equal(mineFetches(page).length, 3, 'it asked');
    assert.equal((await ledger.mineOf(USER_A)).find(r => r.wallId === W1).cursor, '1.2.3', 'and wrote nothing');
});

test('marks under another partition label (B signed in in another tab) are not stored, and this partition\'s row goes', async () => {
    let partition = USER_A;
    const page = await signedInWithMarks({ routes: mineRoute(W1, () => json(body(W1, { partition }))) });
    partition = USER_B; // the shared cookie is B's now; this tab still runs as A
    await page.cache.mineChanged(W1);
    await page.settle();
    const ledger = await page.ledger();
    assert.equal((await ledger.mineOf(USER_A)).find(r => r.wallId === W1), undefined, 'A\'s stale row went');
    assert.deepEqual(plain(await ledger.mineOf(USER_B)), [], 'and B\'s marks were stored nowhere');
    assert.ok((await ledger.mineOf(USER_A)).find(r => r.wallId === W2), 'the other wall\'s row is untouched');

    const unlabelled = memberPage({ swReply: 'none', routes: mineRoute(W1, () => json(body(W1, { partition: undefined }))) });
    await unlabelled.boot();
    assert.equal((await (await unlabelled.ledger()).mineOf(USER_A)).find(r => r.wallId === W1), undefined, 'no label, no row');
});

test('a docs-index 403 that drops the wall while its marks are in flight leaves no mine row', async () => {
    let release = null;
    const gate = new Promise(resolve => { release = resolve; });
    const page = memberPage({ swReply: 'none', routes: Object.assign(
        mineRoute(W1, async () => { await gate; return json(body(W1)); }),
        mineRoute(W2),
        { [`/api/walls/${W1}/docs`]: () => { setTimeout(() => release(), 50); return new Response('', { status: 403 }); } }) });
    await page.boot();
    await page.settle();
    const ledger = await page.ledger();
    assert.equal(await ledger.getWall(USER_A, W1), undefined, 'the 403 dropped the wall');
    assert.equal((await ledger.mineOf(USER_A)).find(r => r.wallId === W1), undefined, 'and no orphan mine row came after it');
});

test('bwAssetMine.changed refetches one wall once per burst, after the debounce', async () => {
    const page = await signedInWithMarks();
    const before = mineFetches(page).length;
    page.win.bwAssetMine.changed(W1.toUpperCase());
    page.win.bwAssetMine.changed(W1);
    page.win.bwAssetMine.changed(W1);
    const timer = page.timers.filter(t => t.ms === page.win.bwAssetMine.DEBOUNCE_MS);
    assert.equal(timer.length, 1);
    await page.settle();
    assert.deepEqual(mineFetches(page).slice(before).map(f => f.path), [`/api/me/walls/${W1}/mine`]);
});

test('rule 1: another user\'s page wipes the first user\'s marks; so does a kiosk page after it', async () => {
    const page = await signedInWithMarks();
    const ledger = await page.ledger();
    const bob = loadPage({ label: USER_B, user: 'bob', swReply: 'none', ledger });
    await bob.boot();
    assert.deepEqual(await allMine(ledger), []);

    const again = await signedInWithMarks({ ledger });
    const tablet = loadPage({ label: KIOSK, swReply: 'none', ledger: await again.ledger() });
    await tablet.boot();
    assert.deepEqual(await allMine(ledger), []);
});

test('rule 2 and the signed-out page (account sign-out, kiosk unregister) leave no mine row', async () => {
    const page = await signedInWithMarks();
    const link = { href: `${ORIGIN}/account/logout`, getAttribute: () => '/account/logout', closest: () => link };
    page.docEvent('click', { target: link, button: 0, preventDefault: () => undefined, defaultPrevented: false });
    await page.settle();
    assert.deepEqual(await allMine(await page.ledger()), []);

    const second = await signedInWithMarks();
    await second.win.bwAssetWipe.signedOut();
    assert.deepEqual(await allMine(await second.ledger()), []);
});

test('rule 6: a tokenless anonymous page after a member\'s leaves no mine row', async () => {
    const page = await signedInWithMarks();
    const anon = loadPage({ label: 'anon', path: '/login', swReply: 'none', ledger: await page.ledger() });
    await anon.boot();
    assert.deepEqual(await allMine(await page.ledger()), []);
});

test('rules 4/5: dropWall takes that wall\'s marks only', async () => {
    const page = await signedInWithMarks();
    await page.cache.dropWall(W1);
    assert.deepEqual((await rowsOf(page)).map(r => r.wallId), [W2]);
});

test('Clear offline data deletes this partition\'s marks; the rebuild fetches them again', async () => {
    let answer = 200;
    const page = await signedInWithMarks({ routes: mineRoute(W1, () => (answer === 200 ? json(body(W1)) : new Response('', { status: answer }))) });
    answer = 503; // the rebuild cannot fetch W1's marks: only the clear can have removed them
    assert.equal(await page.win.bwAssetReport.clear(), true);
    await page.settle();
    await page.cache.filled();
    await page.settle();
    assert.deepEqual((await rowsOf(page)).map(r => r.wallId), [W2], 'W2 came back with the rebuild, W1 did not');
});

test('the next durable start sweeps every other partition\'s marks (a degraded tab\'s clear could not reach them)', async () => {
    const page = await signedInWithMarks();
    const ledger = await page.ledger();
    // What a stale or degraded tab left behind: marks of another user, and rows no `k-`/`anon` page writes.
    for (const partition of [USER_B, KIOSK, 'anon']) {
        await ledger.putMine({ partition, wallId: W1, ticks: [], favourites: [] });
    }
    const next = await signedInWithMarks({ ledger });
    assert.deepEqual([...new Set((await allMine(await next.ledger())).map(r => r.partition))], [USER_A]);

    for (const partition of [USER_B, 'anon']) {
        await ledger.putMine({ partition, wallId: W1, ticks: [], favourites: [] });
    }
    const tablet = loadPage({ label: KIOSK, swReply: 'none', ledger });
    await tablet.boot();
    assert.deepEqual(await allMine(ledger), [], 'a kiosk start keeps no mine row of anyone');
});
