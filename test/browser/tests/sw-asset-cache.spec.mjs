// The worker's /a/ routing in a real browser (§6.7, PR 2.6b): cache-first after one load, keyed without wall/token/rev/
// bwretry, Range and failures never stored, and served offline from Cache Storage.
import { test, expect, signIn, installWorker, partitionOf, assetKeys, allKeys, assetPath, pageFetch } from '../harness/fixtures.mjs';

test.use({ workerOnly: true });

test.beforeEach(async ({ page, world }) => {
    await signIn(page, world.users.alice);
    await installWorker(page);
});

test('a hashed asset is fetched once, stored under its sha, and then served without reaching the server', async ({ page, world, server }) => {
    const wall = world.walls.alpha;
    const sha = wall.panels[0];
    const path = assetPath(wall.id, sha, 640);
    const partition = await partitionOf(page);
    expect(partition).toMatch(/^u-[a-z2-7]{20}$/);

    const first = await pageFetch(page, path);
    expect(first.status).toBe(200);
    expect(first.type).toBe('image/jpeg');
    await expect.poll(async () => (await assetKeys(page))[`bw-a1-${partition}-img`] || []).toContain(`${sha}/640`);

    const mark = await server.mark();
    const again = await pageFetch(page, path);
    expect(again).toMatchObject({ status: 200, bytes: first.bytes });
    // The key drops token, rev and bwretry: all of these are the same cached bytes.
    for (const query of ['?rev=7', '?bwretry=1', '?token=whatever&bwretry=2']) {
        expect((await pageFetch(page, path + query)).bytes).toBe(first.bytes);
    }
    expect(await server.hits(mark, path)).toBe(0);
});

test('a Range request bypasses the cache and is never stored', async ({ page, world, server }) => {
    const wall = world.walls.alpha;
    const path = assetPath(wall.id, wall.splat);
    const mark = await server.mark();
    const ranged = await pageFetch(page, path, { headers: { Range: 'bytes=0-99' } });
    expect([200, 206]).toContain(ranged.status);
    expect(await server.hits(mark, path)).toBe(1);
    expect(allKeys(await assetKeys(page))).not.toContain(wall.splat);
});

test('a 404 is passed through and not stored', async ({ page, world, server }) => {
    // Bob's wall: Alice is no member, so the server answers 404 for its panel.
    const wall = world.walls.bobs;
    const path = assetPath(wall.id, wall.panels[0], 640);
    const mark = await server.mark();
    expect((await pageFetch(page, path)).status).toBe(404);
    expect((await pageFetch(page, path)).status).toBe(404);
    expect(await server.hits(mark, path)).toBe(2);
    expect(allKeys(await assetKeys(page))).not.toContain(`${wall.panels[0]}/640`);
});

async function storeOne(page, world) {
    const wall = world.walls.alpha;
    const stored = assetPath(wall.id, wall.panels[0], 640);
    expect((await pageFetch(page, stored)).status).toBe(200);
    const partition = await partitionOf(page);
    await expect.poll(async () => (await assetKeys(page))[`bw-a1-${partition}-img`] || []).toContain(`${wall.panels[0]}/640`);
    return { stored, unstored: assetPath(wall.id, wall.panels[1], 1280) };
}

// Served offline: a fresh navigation lands on the worker's offline page, a new client whose partition lookup fails and
// falls back to the worker's last label. `no-store` keeps the HTTP cache out of it: only Cache Storage can answer.
async function expectServedOffline(page, { stored, unstored }) {
    await page.goto('/walls');
    await expect(page.locator('meta[name="bw-partition"]')).toHaveAttribute('content', 'offline');
    expect(await pageFetch(page, stored, { cache: 'no-store' })).toMatchObject({ status: 200, type: 'image/jpeg' });
    expect((await pageFetch(page, unstored, { cache: 'no-store' })).status).toBe(0);
}

test('with the server unreachable, a stored asset is served from Cache Storage and an unstored one fails', async ({ page, world, server }) => {
    const urls = await storeOne(page, world);
    await server.faults([{ path: '.*', drop: true }]);
    await expectServedOffline(page, urls);
});

test('with the browser offline, a stored asset is served from Cache Storage', async ({ page, context, world, browserName }) => {
    // Playwright's WebKit offline emulation fails every request in the page before the worker sees it (a cached
    // asset fails too, and navigations raise an internal error), so it cannot stand for a device without network.
    // The unreachable-server test above covers WebKit.
    test.skip(browserName === 'webkit', 'WebKit offline emulation bypasses the service worker');
    const urls = await storeOne(page, world);
    await context.setOffline(true);
    try {
        await expectServedOffline(page, urls);
    } finally {
        await context.setOffline(false);
    }
});
