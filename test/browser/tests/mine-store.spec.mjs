// The viewer's own ticks and favourites on the device (Phase 3 PR 3.4): a signed-in page keeps them per wall in the
// ledger's `mine` store, a change on the page reaches that row through the circuit's feed, and nobody else ever gets
// one: the route refuses nobody, a non-member and a kiosk tablet, a tablet stores none, and logout or another user wipes
// them at once.
import { test, expect, signIn, installWorker, partitionOf, hasPageModules, settled, pageFetch } from '../harness/fixtures.mjs';
import { registerKiosk, actAs } from '../harness/kiosk.mjs';

const minePath = wallId => `/api/me/walls/${wallId}/mine`;

// Every row of the `mine` store, read straight from IndexedDB ([] before the v2 store exists).
function mineRows(page) {
    return page.evaluate(() => new Promise((resolve, reject) => {
        const open = indexedDB.open('blocwerk-assets');
        open.onerror = () => reject(open.error);
        open.onsuccess = () => {
            const db = open.result;
            if (!db.objectStoreNames.contains('mine')) {
                db.close();
                resolve([]);
                return;
            }
            const req = db.transaction('mine', 'readonly').objectStore('mine').getAll();
            req.onsuccess = () => { db.close(); resolve(req.result); };
            req.onerror = () => { db.close(); reject(req.error); };
        };
    }));
}

async function signedInAndSettled(page, user, wallId) {
    await signIn(page, user);
    await installWorker(page, `/walls/${wallId}`);
    test.skip(!(await hasPageModules(page)), 'needs the page modules');
    await settled(page);
    return partitionOf(page);
}

const rowOf = (rows, wallId) => rows.find(r => r.wallId === wallId);

test('a signed-in device keeps its own marks per wall, no-store on the wire, and the Logout link leaves none', async ({ page, world }) => {
    const alpha = world.walls.alpha;
    const alice = await signedInAndSettled(page, world.users.alice, alpha.id);

    const rows = await mineRows(page);
    expect(new Set(rows.map(r => r.partition))).toEqual(new Set([alice]));
    const row = rowOf(rows, alpha.id);
    expect(row.ticks).toEqual([expect.objectContaining({ boulderId: alpha.boulder, attempts: 1, sent: true, flashed: false })]);
    expect(row.cursor).toMatch(/\./);
    const wire = await pageFetch(page, minePath(alpha.id));
    expect(wire.status).toBe(200);
    expect(wire.cacheControl).toBe('no-store');

    const signedOut = page.waitForResponse(r => new URL(r.url()).pathname === '/account/logout');
    await page.getByRole('link', { name: 'Logout' }).click();
    expect((await signedOut).status()).toBe(200);
    await expect(page.locator('meta[name="bw-partition"]')).toHaveAttribute('content', 'anon');
    await page.waitForLoadState('load');
    expect(await mineRows(page)).toEqual([]);
});

test('another user signing in on the device wipes the first user\'s marks before storing their own', async ({ page, world }) => {
    const alpha = world.walls.alpha;
    const alice = await signedInAndSettled(page, world.users.alice, alpha.id);
    expect(rowOf(await mineRows(page), alpha.id).partition).toBe(alice);

    const carol = await signedInAndSettled(page, world.users.carol, alpha.id);
    expect(carol).not.toBe(alice);
    const rows = await mineRows(page);
    expect(new Set(rows.map(r => r.partition))).toEqual(new Set([carol]));
    const row = rowOf(rows, alpha.id);
    expect(row.ticks).toEqual([expect.objectContaining({ boulderId: alpha.boulder, flashed: true })]);
    expect(row.favourites).toEqual([alpha.boulder]);
});

test('a kiosk tablet acting as a member stores no marks, and the route refuses it', async ({ page, world }) => {
    const alpha = world.walls.alpha;
    await registerKiosk(page, world.kioskToken);
    await actAs(page, world.users.carol);
    await installWorker(page, `/walls/${alpha.id}`);
    test.skip(!(await hasPageModules(page)), 'needs the page modules');
    await expect(page.locator('body')).toHaveAttribute('data-bw-user', world.users.carol.id);
    await settled(page);
    expect(await partitionOf(page)).toMatch(/^k-/);

    expect((await pageFetch(page, minePath(alpha.id))).status).toBe(403);
    expect(await mineRows(page)).toEqual([]);
});

test('the route answers nobody 401 and a signed-in non-member 403', async ({ page, world }) => {
    const alpha = world.walls.alpha;
    await page.goto('/');
    expect((await pageFetch(page, minePath(alpha.id))).status).toBe(401);
    await signIn(page, world.users.bob);
    expect((await pageFetch(page, minePath(alpha.id))).status).toBe(403);
    expect((await pageFetch(page, minePath(world.walls.bobs.id))).status).toBe(200);
});

test('a favourite changed on the boulder page reaches the stored marks through the feed', async ({ page, world }) => {
    const alpha = world.walls.alpha;
    await signIn(page, world.users.alice);
    await installWorker(page, `/walls/${alpha.id}/boulders/${alpha.boulder}`);
    test.skip(!(await hasPageModules(page)), 'needs the page modules');
    await settled(page);
    const favourites = async () => (rowOf(await mineRows(page), alpha.id) || {}).favourites;
    const start = await favourites();
    const [show, back] = start.includes(alpha.boulder)
        ? ['Remove from favourites', 'Add to favourites']
        : ['Add to favourites', 'Remove from favourites'];

    // The circuit must be up for the feed to reach the page.
    await page.waitForFunction(() => !!window.Blazor);
    await page.getByRole('button', { name: show }).click();
    await expect.poll(favourites, { timeout: 30000 }).not.toEqual(start);
    await page.getByRole('button', { name: back }).click();
    await expect.poll(favourites, { timeout: 30000 }).toEqual(start);
});
