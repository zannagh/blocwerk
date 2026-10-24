// The kiosk's offline mode (Phase 3 PR 3.15) on the real engines. A paired tablet precaches the offline client like any
// device (the server lets a kiosk session have /v), and with the server gone it shows its bound wall from its `k-`
// partition: the kiosk class (no draft, no archived boulder, an unconsented member as "A member"), never another
// wall, never marks, also while it acts as a member who has some. A tablet whose answers were `no-store` keeps nothing
// and shows "Not available offline". Data planted for another wall or another partition is never read. The grade-scale
// cookie the client reads is set for a signed-in person and never on a tablet.
import { test, expect, signIn, installWorker, partitionOf, settled, ledger } from '../harness/fixtures.mjs';
import { registerKiosk, actAs } from '../harness/kiosk.mjs';
import { declareRoutes, precacheOutcome, expectClient, missingDocs } from '../harness/client.mjs';

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

test('an airplane-mode tablet shows only its bound wall, in the kiosk class', async ({ page, context, world, server }) => {
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
    expect(await gradeCookie(context)).toEqual([]);
});

test('acting as a member, the offline tablet shows nothing of hers: no marks stored or read, the kiosk class only', async ({ page, context, world, server }) => {
    const alpha = world.walls.alpha;
    const carol = world.users.carol;
    expect(await precacheKiosk(page, server, world.kioskToken, alpha.id, { member: carol })).toBe('complete');
    await expect(page.locator('body')).toHaveAttribute('data-bw-user', carol.id);
    const kiosk = await partitionOf(page);
    expect(kiosk, 'act-as keeps the tablet\'s partition').toMatch(/^k-[a-z2-7]{20}$/);
    expect(await allMine(page), 'Carol\'s tick and favourite are never stored on the tablet').toEqual([]);
    expect(await gradeCookie(context)).toEqual([]);

    await server.unreachable();
    await expectClient(page, `/walls/${alpha.id}`);
    await storeState(page, 'ready');
    const snap = await snapshot(page);
    expect(snap.partition).toBe(kiosk);
    expect(snap.mine).toEqual([]);
    const read = await docs(page, kiosk, alpha.id);
    expect(Object.keys(read.docs)).toContain('boulders');
    expect(JSON.stringify(read.docs), 'no document names the acting member').not.toContain(carol.id);
    expect(await gradeCookie(context)).toEqual([]);
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

test('the grade cookie: a signed-in page sets it; a kiosk page never does and deletes one, acting as a member too', async ({ page, context, world, baseURL }) => {
    const wall = `/walls/${world.walls.alpha.id}`;
    const planted = () => context.addCookies([{ name: 'blocwerk-grade-pref', value: 'v', url: baseURL }]);
    await signIn(page, world.users.alice);
    await page.goto(wall);
    expect((await gradeCookie(context)).map(c => c.value), 'the writer is live, so its absence below means something').toEqual(['font']);
    await context.clearCookies();

    await registerKiosk(page, world.kioskToken);
    await planted();
    await page.goto(wall);
    expect(await gradeCookie(context), 'a tablet page deletes one the browser had').toEqual([]);

    await actAs(page, world.users.carol);
    await page.goto(wall);
    await expect(page.locator('body')).toHaveAttribute('data-bw-user', world.users.carol.id);
    expect(await gradeCookie(context), 'acting as a member sets none').toEqual([]);
    await planted();
    await page.goto(`/walls/${world.walls.alpha.id}/boulders/${world.walls.alpha.boulder}`);
    expect(await gradeCookie(context)).toEqual([]);
});
