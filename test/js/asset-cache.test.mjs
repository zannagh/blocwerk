// The asset-cache page modules end to end: the wipe rules (page load, logout, the signed-out page, manifest 404s,
// rule 5, unknown labels, stale tabs). Run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { loadPage, fakeCaches, json, manifest, sha, USER_A, USER_B, KIOSK, ORIGIN } from './fixtures/page-sandbox.mjs';
import { W1, W2, keys, names, plain, listing, standardCaches, memberPage } from './fixtures/asset-pages.mjs';

test('rule 1: a page under another identity wipes every other partition, never the worker state, shell or offline queue', async () => {
    const caches = standardCaches();
    const { win } = loadPage();
    const ledger = win.bwAssetLedger.memory();
    await ledger.setMeta({ activePartition: USER_B, userTag: win.bwAssetWipe.userTagOf({ body: { getAttribute: () => 'bob' } }) });

    const drafts = [];
    const page = loadPage({ label: USER_A, user: 'alice', caches, ledger, swReply: 'none', summary: { wallsWith3d: 0, walls: [W1] }, routes: listing(W1, sha('a')), deployDrafts: { clearAll: () => drafts.push('cleared') } });
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
    const page = loadPage({ label: USER_A, user: 'alice', swReply: 'none' });
    await page.boot();
    assert.deepEqual(plain(page.events), []);
    const next = loadPage({ label: USER_B, user: 'bob', swReply: 'none', ledger: await page.ledger() });
    await next.boot();
    assert.deepEqual(plain(next.events), ['bw:identity-changed']);
});

test('a new label for the same user (a server restart on a run-only partition key) wipes the cache but keeps the drafts', async () => {
    const caches = standardCaches();
    const first = loadPage({ label: USER_B, user: 'alice', caches, swReply: 'none' });
    await first.boot();
    const drafts = [];
    const rotated = loadPage({ label: USER_A, user: 'alice', caches, swReply: 'none', ledger: await first.ledger(), deployDrafts: { clearAll: () => drafts.push('cleared') } });
    await rotated.boot();
    assert.ok(!names(caches).includes(`bw-a1-${USER_B}-img`), 'the old label\'s cache is wiped');
    assert.deepEqual(plain(rotated.events), []);
    assert.deepEqual(drafts, []);
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
    assert.deepEqual(plain(tokenless.events), [], 'anon→anon is no identity change');
    assert.deepEqual(drafts, []);

    const member = loadPage({ label: USER_A, user: 'alice', swReply: 'none' });
    await member.boot();
    const signedOut = loadPage({ label: 'anon', path: '/login', swReply: 'none', ledger: await member.ledger(), deployDrafts: { clearAll: () => drafts.push('cleared') } });
    await signedOut.boot();
    assert.deepEqual(plain(signedOut.events), ['bw:identity-changed'], 'a member\'s device turning anonymous is one');
    assert.deepEqual(drafts, ['cleared']);
});

test('rule 2: a plain click on the logout link wipes every partition first, then navigates; kiosk release does not wipe', async () => {
    const caches = standardCaches();
    const drafts = [];
    const page = loadPage({ label: USER_A, caches, swReply: 'none', deployDrafts: { clearAll: () => drafts.push('cleared') } });
    await page.boot();
    let prevented = false;
    const link = { href: `${ORIGIN}/account/logout`, getAttribute: () => '/account/logout', closest: () => link };
    page.docEvent('click', { target: link, button: 0, preventDefault: () => { prevented = true; }, defaultPrevented: false });
    await page.settle();

    assert.ok(prevented);
    assert.ok(!names(caches).some(n => n.startsWith('bw-a1-')));
    assert.deepEqual(page.assigned, [`${ORIGIN}/account/logout`], 'navigated once, not again when the cap fires');
    assert.deepEqual(plain(page.events), ['bw:identity-changed']);
    assert.deepEqual(drafts, ['cleared']);
    assert.equal((await (await page.ledger()).getMeta()).activePartition, null);

    const kiosk = fakeCaches({ [`bw-a1-${KIOSK}-img`]: [`${ORIGIN}/__bw/${sha('k')}`] });
    const tablet = loadPage({ label: KIOSK, caches: kiosk, swReply: 'none', summary: { wallsWith3d: 0, walls: [W1] }, routes: listing(W1, sha('k')) });
    await tablet.boot();
    const form = { dataset: {}, getAttribute: () => '/kiosk/release', submit: () => {} };
    tablet.docEvent('submit', { target: form, preventDefault: () => assert.fail('release must not be held') });
    await tablet.settle();
    assert.deepEqual(keys(kiosk, `bw-a1-${KIOSK}-img`), [sha('k')]);
});

test('rule 2: a modified or non-primary click on the logout link is left to the browser', async () => {
    const caches = standardCaches();
    const page = loadPage({ label: USER_A, caches, swReply: 'none' });
    await page.boot();
    const link = { href: `${ORIGIN}/account/logout`, getAttribute: () => '/account/logout', closest: () => link };
    for (const extra of [{ metaKey: true }, { ctrlKey: true }, { shiftKey: true }, { altKey: true }, { button: 1 }]) {
        page.docEvent('click', Object.assign({ target: link, button: 0, preventDefault: () => assert.fail('held a modified click'), defaultPrevented: false }, extra));
    }
    await page.settle();
    assert.deepEqual(page.assigned, []);
    assert.equal(names(caches).filter(n => n.startsWith('bw-a1-')).length, 1);
});

test('rule 2: a ledger that never opens cannot hold the logout: the cap navigates anyway', async () => {
    const page = loadPage({ label: USER_A, swReply: 'none' });
    const listeners = {};
    const assigned = [];
    const fakeWin = {
        document: { addEventListener: (t, fn) => { listeners[t] = fn; }, querySelector: () => null },
        location: { href: `${ORIGIN}/walls`, assign: u => assigned.push(u) },
        URL, setTimeout: (fn, ms) => setTimeout(fn, ms), dispatchEvent: () => {}, CustomEvent: class {},
    };
    page.win.bwAssetWipe.installLogout(fakeWin, () => new Promise(() => {}));
    const link = { getAttribute: () => '/account/logout', href: `${ORIGIN}/account/logout` };
    listeners.click({ target: { closest: () => link }, button: 0, defaultPrevented: false, preventDefault: () => {} });
    await new Promise(r => setTimeout(r, 1700));
    assert.deepEqual(assigned, [`${ORIGIN}/account/logout`]);
});

test('the signed-out page\'s bwAssetWipe.signedOut() wipes every partition when no interceptor ran; a second wipe is harmless', async () => {
    const caches = standardCaches();
    const page = loadPage({ label: USER_A, caches, swReply: 'none', summary: { wallsWith3d: 0, walls: [W1] }, routes: listing(W1, sha('a')) });
    await page.boot();
    assert.deepEqual(keys(caches, `bw-a1-${USER_A}-img`), [sha('a')]);

    await page.win.bwAssetWipe.signedOut();
    await page.win.bwAssetWipe.signedOut();
    assert.ok(!names(caches).some(n => n.startsWith('bw-a1-')));
    assert.ok(names(caches).includes('blocwerk-offline') && names(caches).includes('bw-sw-state'));
    assert.equal((await (await page.ledger()).getMeta()).activePartition, null);
});

test('on a kiosk the logout link keeps the tablet cache (the server answers a plain redirect there)', async () => {
    const caches = fakeCaches({ [`bw-a1-${KIOSK}-img`]: [`${ORIGIN}/__bw/${sha('k')}`] });
    const tablet = loadPage({ label: KIOSK, caches, swReply: 'none', summary: { wallsWith3d: 0, walls: [W1] }, routes: listing(W1, sha('k')) });
    await tablet.boot();
    const link = { href: `${ORIGIN}/account/logout`, getAttribute: () => '/account/logout', closest: () => link };
    tablet.docEvent('click', { target: link, button: 0, preventDefault: () => assert.fail('a kiosk logout is not held'), defaultPrevented: false });
    await tablet.settle();
    assert.deepEqual(keys(caches, `bw-a1-${KIOSK}-img`), [sha('k')]);
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

test('unknown (the server could not resolve the partition): no fill and no wipe; a changed acting user wipes everything', async () => {
    const caches = standardCaches();
    const first = memberPage({ caches, user: 'alice', swReply: 'none', routes: listing(W2, sha('a')) });
    await first.boot();
    const ledger = await first.ledger();

    const same = memberPage({ label: 'unknown', user: 'alice', caches, ledger });
    await same.boot();
    assert.equal(same.assetFetches().length, 0);
    assert.ok(!same.fetches.some(f => f.path.endsWith('/manifest')));
    assert.deepEqual(keys(caches, `bw-a1-${USER_A}-img`), [sha('a')]);
    assert.equal((await ledger.getMeta()).activePartition, USER_A);
    assert.deepEqual(plain(same.events), []);

    const other = memberPage({ label: 'unknown', user: 'bob', caches, ledger });
    await other.boot();
    assert.ok(!names(caches).some(n => n.startsWith('bw-a1-')));
    assert.deepEqual(plain(other.events), ['bw:identity-changed']);
});

test('a stale tab cannot write the partition another tab moved on from, and a start deletes stray rows', async () => {
    const page = memberPage({ swReply: 'none' });
    await page.boot();
    const ledger = await page.ledger();
    // Another tab signed in as B; this tab still runs as A, and its writes are refused.
    await ledger.setMeta({ activePartition: USER_B });
    await page.cache.state().env.ledger.putAsset({ partition: USER_A, key: sha('9'), sha: sha('9') });
    await page.cache.manifestChanged(W1);
    assert.equal(await ledger.getAsset(USER_A, sha('9')), undefined);

    await ledger.setMeta({ activePartition: USER_A });
    await ledger.putAsset({ partition: USER_B, key: sha('8'), sha: sha('8') });
    const next = memberPage({ swReply: 'none', ledger });
    await next.boot();
    assert.deepEqual(plain(await ledger.assetsOf(USER_B)), []);
});

test('rule 5: a share-opened wall is dropped on the next start that is not on it', async () => {
    const shared = loadPage({ label: USER_A, path: `/walls/${W1}/shared/tok`, swReply: 'none', summary: { wallsWith3d: 0, walls: [] }, routes: listing(W1, sha('1')) });
    await shared.boot();
    const ledger = await shared.ledger();
    assert.equal((await ledger.wallsOf(USER_A)).length, 1);
    const later = loadPage({ label: USER_A, path: '/', swReply: 'none', ledger, summary: { wallsWith3d: 0, walls: [] } });
    await later.boot();
    assert.deepEqual(plain(await ledger.wallsOf(USER_A)), []);
});

test('a kiosk tablet whose meta is unknown (a resolver fault) does not wipe on logout: data-bw-kiosk tells it', async () => {
    const caches = fakeCaches({ [`bw-a1-${KIOSK}-img`]: [`${ORIGIN}/__bw/${sha('k')}`] });
    const tablet = loadPage({ label: 'unknown', kioskKey: 'key-1', caches, swReply: 'none' });
    await tablet.boot();
    const link = { href: `${ORIGIN}/account/logout`, getAttribute: () => '/account/logout', closest: () => link };
    tablet.docEvent('click', { target: link, button: 0, preventDefault: () => assert.fail('a kiosk logout is not held'), defaultPrevented: false });
    await tablet.settle();
    assert.deepEqual(keys(caches, `bw-a1-${KIOSK}-img`), [sha('k')]);
});
