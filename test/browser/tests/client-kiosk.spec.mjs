// The kiosk's offline mode (Phase 3 PR 3.15) on the real engines. A paired tablet precaches the offline client like any
// device (the server lets a kiosk session have /v), and with the server gone it shows its bound wall from its `k-`
// partition: the kiosk class (no draft, no archived boulder, an unconsented member as "A member"), never another
// wall, never marks, also while it acts as a member who has some. A tablet whose answers were `no-store` keeps nothing
// and shows "Not available offline" (a defence in depth: in production such a tablet is `none`). Data planted for another
// wall or another partition is never read. A copy past 7 days since its last online fill is not handed over. No kiosk
// response sets the grade-scale cookie.
import { test, expect, installWorker, partitionOf, settled, ledger } from '../harness/fixtures.mjs';
import { registerKiosk, actAs } from '../harness/kiosk.mjs';
import { declareRoutes, precacheOutcome, expectClient, expectOfflinePage, missingDocs } from '../harness/client.mjs';

const store = page => page.locator('.bw-client-store');
const storeState = (page, state) => expect(store(page)).toHaveAttribute('data-bw-store', state, { timeout: 15000 });
const docs = (page, partition, wallId) => page.evaluate(([p, w]) => window.bwClientStore.docs(p, w), [partition, wallId]);
const snapshot = page => page.evaluate(() => window.bwClientStore.snapshot());
// A data document answered `no-store`, as the server answers a kiosk that may not keep its wall (DocumentCaching).
const NO_STORE_DOCS = { path: '^/api/walls/[^/]+/d/(wall|holds|boulders|gallery|comments)/', rewrite: ['\u0000', '\u0000'], headers: { 'cache-control': 'no-store' } };

// Every row of the ledger's `mine` store, whatever its partition.
const allMine = page => page.evaluate(() => new Promise((resolve, reject) => {
    const open = indexedDB.open('blocwerk-assets');
    open.onerror = () => reject(open.error);
    open.onsuccess = () => {
        const req = open.result.transaction('mine', 'readonly').objectStore('mine').getAll();
        req.onsuccess = () => { open.result.close(); resolve(req.result); };
        req.onerror = () => { open.result.close(); reject(req.error); };
    };
}));

/**
 * Pairs the context as `token`'s tablet, opens its wall under the installed worker and waits for the precache, and
 * (unless `stored` is false) for the wall's data documents in the device cache.
 */
async function precacheKiosk(page, server, token, wallId, { faults = [], member = null, stored = true } = {}) {
    await server.faults([declareRoutes(), ...faults]);
    await registerKiosk(page, token);
    if (member) {
        await actAs(page, member);
    }
    await installWorker(page, `/walls/${wallId}`);
    await settled(page);
    if (stored) {
        const partition = await partitionOf(page);
        await expect.poll(() => missingDocs(page, partition, wallId), { timeout: 20000 }).toEqual([]);
    }
    return precacheOutcome(page);
}

const gradeCookie = async context => (await context.cookies()).filter(c => c.name === 'blocwerk-grade-pref');

test('an airplane-mode tablet shows only its bound wall, in the kiosk class', async ({ page, world, server }) => {
    const wallId = world.social.wallId;
    expect(await precacheKiosk(page, server, world.social.kioskToken, wallId)).toBe('complete');
    const kiosk = await partitionOf(page);
    expect(kiosk).toMatch(/^k-[a-z2-7]{20}$/);
    expect((await ledger(page, kiosk)).walls.map(w => w.wallId)).toEqual([wallId]);

    await server.unreachable();
    await expectClient(page, `/walls/${wallId}`);
    await storeState(page, 'ready');
    await expect(store(page)).toHaveAttribute('data-bw-store-walls', '1');
    const snap = await snapshot(page);
    expect(snap.partition).toBe(kiosk);
    expect(snap.walls.map(w => w.wallId)).toEqual([wallId]);
    expect(snap.mine).toEqual([]);

    const read = await docs(page, kiosk, wallId);
    const names = JSON.parse(read.docs.boulders.text).boulders.map(b => b.name);
    expect(names).toContain('Social boulder');
    expect(names).not.toContain('Secret draft');
    expect(names).not.toContain('Old archived');
    const comments = read.docs.comments.text;
    expect(comments).toContain(world.social.unconsentedComment);
    expect(comments, 'a member without kiosk consent is never named on the tablet').not.toContain(world.users.dave.name);
    expect(comments).not.toContain(world.users.dave.id);

    // Another wall, by URL or by the store: nothing.
    expect(await docs(page, kiosk, world.walls.alpha.id)).toBeNull();
    await page.goto(`/v/walls/${world.walls.alpha.id}`);
    await storeState(page, 'ready');
    expect((await snapshot(page)).walls.map(w => w.wallId)).toEqual([wallId]);
});

test('acting as a member who sees drafts, the offline tablet keeps the kiosk class and nothing of hers', async ({ page, world, server }) => {
    const wallId = world.social.wallId;
    const cora = world.users.cora;
    expect(await precacheKiosk(page, server, world.social.kioskToken, wallId, { member: cora })).toBe('complete');
    await expect(page.locator('body')).toHaveAttribute('data-bw-user', cora.id);
    const kiosk = await partitionOf(page);
    expect(kiosk, 'act-as keeps the tablet\'s partition').toMatch(/^k-[a-z2-7]{20}$/);
    expect(await allMine(page), 'no marks are ever stored on the tablet').toEqual([]);

    await server.unreachable();
    await expectClient(page, `/walls/${wallId}`);
    await storeState(page, 'ready');
    const snap = await snapshot(page);
    expect(snap.partition).toBe(kiosk);
    expect(snap.mine).toEqual([]);
    // Cora's member class would carry the draft, the archived boulder and Dave's name: the tablet holds the kiosk class.
    const text = JSON.stringify((await docs(page, kiosk, wallId)).docs);
    expect(text).toContain('Social boulder');
    for (const absent of ['Secret draft', 'Old archived', world.users.dave.name]) {
        expect(text, absent).not.toContain(absent);
    }
});

test('a no-store tablet keeps no document and shows Not available offline', async ({ page, world, server }) => {
    const alpha = world.walls.alpha;
    expect(await precacheKiosk(page, server, world.kioskToken, alpha.id, { faults: [NO_STORE_DOCS], stored: false })).toBe('complete');
    const kiosk = await partitionOf(page);
    const row = (await ledger(page, kiosk)).walls.find(w => w.wallId === alpha.id);
    expect(row && row.data && row.data.wall, 'the index named the documents').toBeTruthy();

    await server.unreachable();
    await expectClient(page, `/walls/${alpha.id}`);
    await storeState(page, 'kiosk');
    await expect(page.locator('h1')).toHaveText('Not available offline');
    await expect(page.locator('.bw-client-saved')).toHaveCount(0);
    expect(await docs(page, kiosk, alpha.id)).toBeNull();
});

test('a copy past 7 days since its last online fill is not handed over: offline.html, as for a tablet that may keep nothing', async ({ page, world, server }) => {
    const alpha = world.walls.alpha;
    expect(await precacheKiosk(page, server, world.kioskToken, alpha.id)).toBe('complete');
    const kiosk = await partitionOf(page);
    const stamp = () => page.evaluate(async () => {
        const hit = await caches.match('/__bw/kiosk-fresh', { cacheName: 'bw-sw-state' });
        return hit ? hit.json() : null;
    });
    const written = await stamp();
    expect(written.label).toBe(kiosk);
    expect(Date.now() - written.at).toBeLessThan(10 * 60 * 1000);

    const old = Date.now() - 8 * 24 * 60 * 60 * 1000;
    await page.evaluate(async ([k, at]) => (await caches.open('bw-sw-state')).put('/__bw/kiosk-fresh', new Response(JSON.stringify({ label: k, at }))), [kiosk, old]);
    await server.unreachable();
    await expectOfflinePage(page, `/walls/${alpha.id}`);
});

test('data planted for another wall or another partition is never read offline', async ({ page, world, server }) => {
    const alpha = world.walls.alpha;
    const beta = world.walls.beta;
    expect(await precacheKiosk(page, server, world.kioskToken, alpha.id)).toBe('complete');
    const kiosk = await partitionOf(page);
    await server.unreachable();
    await expectClient(page, `/walls/${alpha.id}`);
    await storeState(page, 'ready');

    // A member's partition with Beta and her marks (a stale tab of someone who signed in on this browser).
    const member = 'u-' + 'm'.repeat(20);
    await page.evaluate(async ([m, w]) => {
        const l = await window.bwAssetLedger.open();
        await l.updateWall(m, w, () => ({ partition: m, wallId: w, member: true, data: {} }));
        await l.putMine({ partition: m, wallId: w, cursor: '1.1.1', ticks: [], favourites: [w], fetchedAt: 1 });
    }, [member, beta.id]);
    await page.reload();
    await storeState(page, 'ready');
    expect((await snapshot(page)).walls.map(w => w.wallId)).toEqual([alpha.id]);
    expect(await docs(page, member, beta.id)).toBeNull();
    expect((await ledger(page, member)).walls, 'swept by the client\'s start').toEqual([]);
    expect((await allMine(page)).filter(m => m.partition === member)).toEqual([]);

    // A second wall under the tablet's own label: not the bound wall's alone, so neither wall is shown.
    await page.evaluate(async ([k, w, row]) => {
        const l = await window.bwAssetLedger.open();
        await l.updateWall(k, w, () => Object.assign({}, row, { wallId: w }));
    }, [kiosk, beta.id, (await ledger(page, kiosk)).walls[0]]);
    await page.goto(`/v/walls/${beta.id}`);
    await storeState(page, 'kiosk');
    await expect(page.locator('h1')).toHaveText('Not available offline');
    expect(await docs(page, kiosk, beta.id)).toBeNull();
    expect(await docs(page, kiosk, alpha.id)).toBeNull();
});

test('a kiosk session never sets the grade cookie: one planted before stays untouched, resting and acting as a member', async ({ page, context, world, baseURL }) => {
    const wall = `/walls/${world.walls.alpha.id}`;
    // Each response's Set-Cookie headers, read while the page is open: the listener only collects the reads, and they are
    // awaited after it is removed, before the test ends, so none runs against a closed page (a WebKit teardown race).
    const reads = [];
    const onResponse = response => reads.push(response.headersArray()
        .then(headers => headers.filter(h => h.name.toLowerCase() === 'set-cookie' && /blocwerk-grade-pref/i.test(h.value))
            .map(h => `${response.url()} ${h.value}`)));
    page.on('response', onResponse);
    await registerKiosk(page, world.kioskToken);
    await context.addCookies([{ name: 'blocwerk-grade-pref', value: 'planted', url: baseURL }]);
    expect((await gradeCookie(context)).map(c => c.value), 'the lookup sees a planted cookie').toEqual(['planted']);

    await page.goto(wall);
    await actAs(page, world.users.carol);
    await page.goto(wall);
    await expect(page.locator('body')).toHaveAttribute('data-bw-user', world.users.carol.id);
    await page.goto(`${wall}/boulders/${world.walls.alpha.boulder}`);
    page.off('response', onResponse);
    const written = (await Promise.all(reads)).flat();
    expect(reads.length, 'the listener saw the kiosk pages').toBeGreaterThan(0);
    expect(written, 'no kiosk response sets or deletes it').toEqual([]);
    expect((await gradeCookie(context)).map(c => c.value)).toEqual(['planted']);
});
