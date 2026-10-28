// Kiosk act-as (PR 2.5 follow-up 1addaf7): a beta-video thumbnail is outside the Kiosk class, so the server sends it
// no-store and the worker must never put it in the tablet's shared k- partition. Kiosk-class kinds still cache.
import { test, expect, installWorker, partitionOf, assetKeys, allKeys, assetPath, pageFetch } from '../harness/fixtures.mjs';
import { registerKiosk, actAs } from '../harness/kiosk.mjs';

test.use({ workerOnly: true });

test('a kiosk act-as session is served the video thumbnail but never stores it', async ({ page, world, server }) => {
    const alpha = world.walls.alpha;
    await registerKiosk(page, world.kioskToken);
    await actAs(page, world.users.carol);
    await installWorker(page, `/walls/${alpha.id}`);
    await expect(page.locator('body')).toHaveAttribute('data-bw-user', world.users.carol.id);
    const partition = await partitionOf(page);
    expect(partition).toMatch(/^k-[a-z2-7]{20}$/);

    const thumbnail = assetPath(alpha.id, alpha.thumbnail);
    const mark = await server.mark();
    const first = await pageFetch(page, thumbnail);
    expect(first.status).toBe(200);
    expect(first.cacheControl).toContain('no-store');
    expect(first.etag).toBeNull();
    expect((await pageFetch(page, thumbnail)).status).toBe(200);
    expect(await server.hits(mark, thumbnail)).toBe(2);

    // The kiosk-class panel in the same session is stored, so the worker is caching here at all.
    expect((await pageFetch(page, assetPath(alpha.id, alpha.panels[0], 640))).status).toBe(200);
    await expect.poll(async () => (await assetKeys(page))[`bw-a1-${partition}-img`] || []).toContain(`${alpha.panels[0]}/640`);
    expect(allKeys(await assetKeys(page))).not.toContain(alpha.thumbnail);
});
