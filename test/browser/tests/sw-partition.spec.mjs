// Partition isolation (§6.6): after Alice and then Bob on the same browser, Bob is never served Alice's assets. First
// in the worker alone (PR 2.6b): Bob signs in over Alice's session without a sign-out page, so nothing on the device
// wipes Alice's partition. Then with the 2.7 page modules and the real Logout link, which wipe it.
import { test, expect, signIn, installWorker, partitionOf, assetKeys, assetPath, pageFetch, hasPageModules, settled } from '../harness/fixtures.mjs';

test.describe('the worker alone', () => {
test.use({ workerOnly: true });

test('user A, then user B without a wipe: B is never served A\'s assets', async ({ page, world, server }) => {
    const alpha = world.walls.alpha;
    const alicePanel = assetPath(alpha.id, alpha.panels[0], 640);

    await signIn(page, world.users.alice);
    await installWorker(page);
    const alice = await partitionOf(page);
    expect((await pageFetch(page, alicePanel)).status).toBe(200);
    await expect.poll(async () => (await assetKeys(page))[`bw-a1-${alice}-img`] || []).toContain(`${alpha.panels[0]}/640`);

    // No sign-out page (it wipes the device since 6f2442f) and no page modules: Alice's partition stays on the device.
    await signIn(page, world.users.bob);
    const bob = await partitionOf(page);
    expect(bob).toMatch(/^u-/);
    expect(bob).not.toBe(alice);
    expect(Object.keys(await assetKeys(page))).toContain(`bw-a1-${alice}-img`);

    // Bob is no member of Alpha: the server says 404, and the worker must ask the server rather than answer from
    // Alice's partition.
    const mark = await server.mark();
    expect.soft((await pageFetch(page, alicePanel)).status).toBe(404);
    expect.soft(await server.hits(mark, alicePanel)).toBe(1);
    // Nor may Alice's bytes end up in Bob's partition by any route (the worker's network leg included).
    expect((await assetKeys(page))[`bw-a1-${bob}-img`] || []).not.toContain(`${alpha.panels[0]}/640`);

    // Bob's own asset goes to Bob's partition, never Alice's.
    const bobs = world.walls.bobs;
    expect((await pageFetch(page, assetPath(bobs.id, bobs.panels[0], 640))).status).toBe(200);
    await expect.poll(async () => (await assetKeys(page))[`bw-a1-${bob}-img`] || []).toContain(`${bobs.panels[0]}/640`);
    expect((await assetKeys(page))[`bw-a1-${alice}-img`]).not.toContain(`${bobs.panels[0]}/640`);
});

test('signed out, the anonymous viewer is not served the signed-in partition', async ({ page, world, server }) => {
    const alpha = world.walls.alpha;
    const path = assetPath(alpha.id, alpha.panels[0], 640);
    await signIn(page, world.users.alice);
    await installWorker(page);
    expect((await pageFetch(page, path)).status).toBe(200);

    await page.goto('/account/logout');
    await expect(page.locator('meta[name="bw-partition"]')).toHaveAttribute('content', 'anon');
    expect(await partitionOf(page)).toBe('anon');
    // Anonymous: the server says 401. The worker must ask it.
    const mark = await server.mark();
    expect((await pageFetch(page, path)).status).toBe(401);
    expect(await server.hits(mark, path)).toBe(1);
});
});

test('with the page modules: A fills, logs out by the link, B signs in and is never served A\'s assets', async ({ page, world, server }) => {
    const alpha = world.walls.alpha;
    // One asset the background fill stored, and one Alice's page asked for itself (a worker miss, as an <img> is).
    const filled = assetPath(alpha.id, alpha.panels[0], 640);
    const browsed = assetPath(alpha.id, alpha.panels[1], 2560);
    await signIn(page, world.users.alice);
    await installWorker(page);
    test.skip(!(await hasPageModules(page)), 'needs the 2.7 page modules');
    await settled(page);
    expect((await pageFetch(page, browsed)).status).toBe(200);

    await page.getByRole('link', { name: 'Logout' }).click();
    await expect(page.locator('meta[name="bw-partition"]')).toHaveAttribute('content', 'anon');
    await signIn(page, world.users.bob);
    const bob = await partitionOf(page);

    for (const path of [filled, browsed]) {
        const mark = await server.mark();
        expect.soft((await pageFetch(page, path)).status, path).toBe(404);
        expect.soft(await server.hits(mark, path), path).toBe(1);
    }
    const bobs = (await assetKeys(page))[`bw-a1-${bob}-img`] || [];
    expect.soft(bobs).not.toContain(`${alpha.panels[0]}/640`);
    expect(bobs).not.toContain(`${alpha.panels[1]}/2560`);
});
