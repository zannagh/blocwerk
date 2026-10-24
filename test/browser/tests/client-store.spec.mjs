// The offline client's store (Phase 3 PR 3.10) on the real engines: the client shows what the device cached for the
// ledger's active partition and fetches nothing for it; "Clear offline data" inside the client itself (with the server
// down, so none of Web's page modules is around) wipes the partition and the client then shows nothing; an expired
// session's locked partition renders only the sign-in prompt and Clear; a ledger upgrade held by an old tab shows
// "Updating offline data…"; a share link's /v/ reads only its wall, and a tokenless /v/ wipes `anon` (rule 6).
import { test, expect, signIn, installWorker, partitionOf, settled, ledger, assetKeys } from '../harness/fixtures.mjs';
import { precacheAsAlice, expectClient } from '../harness/client.mjs';

const store = page => page.locator('.bw-client-store');

// What the client store says, once it has read the device.
async function storeState(page, state) {
    await expect(store(page)).toHaveAttribute('data-bw-store', state, { timeout: 15000 });
}

async function consented(page, baseURL) {
    await page.context().addCookies([{ name: 'cookie_consent', value: 'accepted', url: baseURL }]);
}

// Every row of the ledger's `mine` store for `partition`, read straight from IndexedDB.
function mineRows(page, partition) {
    return page.evaluate(p => new Promise((resolve, reject) => {
        const open = indexedDB.open('blocwerk-assets');
        open.onerror = () => reject(open.error);
        open.onsuccess = () => {
            const req = open.result.transaction('mine', 'readonly').objectStore('mine').getAll();
            req.onsuccess = () => { open.result.close(); resolve(req.result.filter(r => r.partition === p)); };
            req.onerror = () => { open.result.close(); reject(req.error); };
        };
    }), partition);
}

const partitionCaches = async (page, label) => Object.keys(await assetKeys(page)).filter(n => n.startsWith(`bw-a1-${label}-`));

test('offline, the client shows the device\'s walls, and its own Clear offline data wipes them: the client then shows nothing', async ({ page, world, server }) => {
    expect(await precacheAsAlice(page, world, server)).toBe('complete');
    const alice = await partitionOf(page);
    const before = await ledger(page, alice);
    expect(before.walls.length).toBeGreaterThan(0);
    expect(before.walls.some(w => w.data && w.data.wall && w.data.boulders)).toBe(true);
    expect((await mineRows(page, alice)).length).toBeGreaterThan(0);
    expect(await partitionCaches(page, alice)).not.toEqual([]);

    await server.unreachable();
    await expectClient(page, `/walls/${world.walls.alpha.id}`);
    await storeState(page, 'ready');
    await expect(store(page)).toHaveAttribute('data-bw-store-walls', String(before.walls.length));
    // The store read the cached documents of its own label, and Web's page modules are not on the page.
    const read = await page.evaluate(([p, w]) => window.bwClientStore.docs(p, w), [alice, world.walls.alpha.id]);
    expect(read.partition).toBe(alice);
    expect(JSON.parse(read.docs.wall.text).name).toBe('Alpha');
    expect(JSON.parse(read.docs.boulders.text).boulders.length).toBeGreaterThan(0);
    expect(await page.evaluate(() => ['bwAssetCache', 'bwAssetStore', 'bwAssetReport', 'bwDeployDrafts'].filter(n => n in window))).toEqual([]);

    await page.getByRole('button', { name: 'Clear offline data' }).click();
    await storeState(page, 'empty');
    await expect(page.getByText('Nothing saved on this device')).toBeVisible();
    const after = await ledger(page, alice);
    expect(after.walls).toEqual([]);
    expect(after.assets).toEqual([]);
    expect(after.meta.activePartition).toBe(alice);
    expect(await mineRows(page, alice)).toEqual([]);
    expect(await partitionCaches(page, alice)).toEqual([]);

    await page.reload();
    await storeState(page, 'empty');
    expect(await page.evaluate(([p, w]) => window.bwClientStore.docs(p, w), [alice, world.walls.alpha.id])).toBeNull();
});

test('a locked partition renders only the sign-in prompt and Clear, and Clear removes it with the lock', async ({ page, context, world, baseURL, server }) => {
    await consented(page, baseURL);
    await signIn(page, world.users.alice);
    await installWorker(page, `/walls/${world.walls.alpha.id}`);
    await settled(page);
    const alice = await partitionOf(page);
    expect((await ledger(page, alice)).walls.length).toBeGreaterThan(0);

    await context.clearCookies({ name: '.AspNetCore.Cookies' });
    await page.goto('/');
    await expect(page.locator('meta[name="bw-partition"]')).toHaveAttribute('data-signout', 'expired');
    await page.evaluate(() => window.bwAssetCache.ready);
    expect((await ledger(page, alice)).meta.locked).toEqual(expect.objectContaining({ label: alice }));

    const mark = await server.mark();
    await page.goto(`/v/walls/${world.walls.alpha.id}`);
    await storeState(page, 'locked');
    await expect(page.locator('h1')).toHaveText('Sign in to keep your offline data');
    await expect(page.getByRole('link', { name: 'Sign in' })).toHaveAttribute('href', '/account/login');
    await expect(page.getByText('Not available offline')).toHaveCount(0);
    await expect(page.locator('.bw-client-saved')).toHaveCount(0);
    await expect(store(page)).toHaveAttribute('data-bw-store-walls', '0');
    expect(await page.evaluate(([p, w]) => window.bwClientStore.docs(p, w), [alice, world.walls.alpha.id])).toBeNull();
    expect(await page.evaluate(() => window.bwClientStore.docs('anon', '00000000-0000-0000-0000-000000000000'))).toBeNull();
    expect((await server.log(mark)).filter(e => e.url.startsWith('/api/')), 'the store reads the device only').toEqual([]);

    await page.getByRole('button', { name: 'Clear offline data' }).click();
    await storeState(page, 'empty');
    const after = await ledger(page, alice);
    expect(after.walls).toEqual([]);
    expect(after.meta.locked ?? null).toBeNull();
    expect(await partitionCaches(page, alice)).toEqual([]);
    expect(await page.evaluate(() => caches.match('/__bw/locked', { cacheName: 'bw-sw-state' }).then(Boolean))).toBe(false);
});

test('a ledger upgrade an old tab holds back shows Updating offline data…', async ({ page, context }) => {
    // A Release 1/2 tab: the v1 database, held open and never closed on versionchange.
    await page.goto('/__pw/blank');
    await page.evaluate(() => new Promise((resolve, reject) => {
        const req = indexedDB.open('blocwerk-assets', 1);
        req.onupgradeneeded = () => {
            req.result.createObjectStore('walls', { keyPath: ['partition', 'wallId'] });
            req.result.createObjectStore('assets', { keyPath: ['partition', 'key'] });
            req.result.createObjectStore('meta', { keyPath: 'id' });
        };
        req.onsuccess = () => { window.__v1 = req.result; resolve(); };
        req.onerror = () => reject(req.error);
    }));

    const client = await context.newPage();
    await client.goto('/v/');
    await storeState(client, 'updating');
    await expect(client.locator('h1')).toHaveText('Updating offline data…');
    await expect(client.getByRole('button', { name: 'Clear offline data' })).toBeVisible();
    await client.close();
});

test('a share link\'s /v/ reads only its wall, and a tokenless /v/ wipes the anonymous partition', async ({ page, world }) => {
    const alpha = world.walls.alpha;
    await installWorker(page, `/walls/${alpha.id}/shared/${world.shareToken}`);
    await settled(page);
    const before = await ledger(page, 'anon');
    expect(before.walls.map(w => w.wallId)).toEqual([alpha.id]);

    await page.goto(`/v/walls/${alpha.id}/shared/${world.shareToken}`);
    await storeState(page, 'ready');
    await expect(store(page)).toHaveAttribute('data-bw-store-walls', '1');

    await page.goto(`/v/walls/${alpha.id}`);
    await storeState(page, 'empty');
    expect((await ledger(page, 'anon')).walls).toEqual([]);
    expect(await partitionCaches(page, 'anon')).toEqual([]);
});
