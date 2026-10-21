// A slow partition lookup (#189, client review W1): the worker waits at most ASSET_LOOKUP_TIMEOUT_MS (2.5 s) for the
// page's lookup, then goes read-only on its last label. A stored asset is served from the cache without reaching the
// server, the page's later requests answer at once, a miss meanwhile goes to the network and is never stored, and once
// the lookup answers the page caches again.
import { test, expect, signIn, installWorker, partitionOf, assetKeys, allKeys, assetPath, pageFetch } from '../harness/fixtures.mjs';

test.use({ workerOnly: true });

const LOOKUP_DELAY_MS = 4000;
const LOOKUP_TIMEOUT_MS = 2500;

async function timed(fn) {
    const start = Date.now();
    const result = await fn();
    return { result, ms: Date.now() - start };
}

test('with a slow lookup, a stored asset comes from the cache with no server hit, and later requests come back fast', async ({ page, world, server }) => {
    const wall = world.walls.alpha;
    const stored = assetPath(wall.id, wall.panels[0], 640);
    const missed = assetPath(wall.id, wall.panels[1], 640);
    await signIn(page, world.users.alice);
    await installWorker(page);
    const partition = await partitionOf(page);
    expect((await pageFetch(page, stored)).status).toBe(200);
    await expect.poll(async () => (await assetKeys(page))[`bw-a1-${partition}-img`] || []).toContain(`${wall.panels[0]}/640`);

    // A new page (a new client) whose lookup the server answers only after LOOKUP_DELAY_MS.
    await server.faults([{ path: '^/api/me/cache-partition$', delay: LOOKUP_DELAY_MS }]);
    await page.goto('/__pw/blank');
    const mark = await server.mark();

    // `no-store` keeps the HTTP cache out of it: only the worker's Cache Storage can answer without the server.
    const first = await timed(() => pageFetch(page, stored, { cache: 'no-store' }));
    expect(first.result).toMatchObject({ status: 200, type: 'image/jpeg' });
    // It waited for the lookup up to the worker's timeout (so the fault did make it slow), not for the whole lookup.
    expect(first.ms).toBeGreaterThanOrEqual(LOOKUP_TIMEOUT_MS - 300);
    expect(first.ms).toBeLessThan(LOOKUP_DELAY_MS);
    const second = await timed(() => pageFetch(page, stored, { cache: 'no-store' }));
    expect(second.result.status).toBe(200);
    expect(second.ms).toBeLessThan(800);
    expect(await server.hits(mark, stored)).toBe(0);

    // Read-only: a miss is fetched (and answers at once, without waiting for the lookup) but never stored.
    const miss = await timed(() => pageFetch(page, missed, { cache: 'no-store' }));
    expect(miss.result.status).toBe(200);
    expect(miss.ms).toBeLessThan(LOOKUP_TIMEOUT_MS);
    expect(await server.hits(mark, missed)).toBe(1);
    expect(allKeys(await assetKeys(page))).not.toContain(`${wall.panels[1]}/640`);

    // Once the slow lookup has answered, the next page caches again.
    await server.faults([]);
    await expect.poll(async () => (await server.log(mark)).some(e => e.url === '/api/me/cache-partition' && e.status === 200),
        { timeout: LOOKUP_DELAY_MS + 5000 }).toBe(true);
    await page.goto('/__pw/blank');
    expect((await pageFetch(page, missed)).status).toBe(200);
    await expect.poll(async () => (await assetKeys(page))[`bw-a1-${partition}-img`] || []).toContain(`${wall.panels[1]}/640`);
});
