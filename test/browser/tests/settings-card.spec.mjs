// Settings → "Offline storage" (PR 2.9: OfflineStorageCard, asset-report.js, asset-clear.js) on a live Blazor page:
// the card lists what this device holds, "Keep offline" pins a wall in the ledger, and "Clear offline data" deletes
// the partition's caches and rebuilds them at once.
import { test, expect, signIn, installWorker, partitionOf, assetKeys, assetPath, hasPageModules, settled, ledger } from '../harness/fixtures.mjs';

async function cardAsAlice(page, world) {
    await signIn(page, world.users.alice);
    await installWorker(page);
    test.skip(!(await hasPageModules(page)) || !(await page.evaluate(() => !!window.bwAssetClear)), 'needs the 2.9 card modules');
    await settled(page);
    await page.goto('/profile?tab=account');
    const card = page.locator('#offline-storage');
    await expect(card.getByRole('heading', { name: 'Offline storage' })).toBeVisible();
    await expect(card.locator('.offline-storage-wall')).toHaveCount(2);
    return card;
}

test('the card lists the walls this device holds, and Keep offline pins one in the ledger', async ({ page, world }) => {
    const card = await cardAsAlice(page, world);
    const partition = await partitionOf(page);
    await expect.poll(async () => (await card.locator('.offline-storage-wall-name').allInnerTexts()).map(t => t.trim()).sort())
        .toEqual(['Alpha', 'Beta']);
    await expect(card.locator('.offline-storage-total')).not.toContainText(/^0 B/);

    const alpha = card.locator('.offline-storage-wall', { hasText: 'Alpha' });
    await alpha.getByLabel('Keep offline').check();
    await expect.poll(async () => (await ledger(page, partition)).walls.find(w => w.wallId === world.walls.alpha.id)?.keepOffline)
        .toBe(true);
    await expect(alpha.getByLabel('Keep offline')).toBeChecked();
});

test('Clear offline data deletes the partition\'s caches and rebuilds them, refetching from the server', async ({ page, world, server }) => {
    const card = await cardAsAlice(page, world);
    const partition = await partitionOf(page);
    const wall = world.walls.alpha;
    const key = `${wall.panels[0]}/640`;
    expect((await assetKeys(page))[`bw-a1-${partition}-img`] || []).toContain(key);
    const mark = await server.mark();

    await card.getByRole('button', { name: 'Clear offline data' }).click();
    await card.getByRole('button', { name: 'Clear', exact: true }).click();

    // Rebuilt: the asset is back in the cache, and it came from the server again (the clear really deleted it).
    await expect.poll(async () => server.hits(mark, assetPath(wall.id, wall.panels[0], 640)), { timeout: 20000 }).toBeGreaterThan(0);
    await expect.poll(async () => (await assetKeys(page))[`bw-a1-${partition}-img`] || [], { timeout: 20000 }).toContain(key);
    await expect(card.locator('.field-error')).toHaveCount(0);
    expect((await ledger(page, partition)).meta.activePartition).toBe(partition);
});
