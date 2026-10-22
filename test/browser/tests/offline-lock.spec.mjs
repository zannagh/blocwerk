// The 401 lock in a real browser (Phase 3 PR 3.5): a signed-in device whose session expires (the browser no longer sends
// the cookie) keeps its `u-` partition LOCKED behind "Sign in to keep your offline data" instead of wiping it; the
// worker never serves it while locked; the same user signing in again gets it back intact and served from the cache;
// another user signing in wipes it at once.
import { test, expect, signIn, installWorker, partitionOf, hasPageModules, settled, ledger, assetKeys, assetPath } from '../harness/fixtures.mjs';

// Consent answered up front, as a returning user's is: the consent banner (which the lock banner yields to) never shows,
// so no test races its late, interactive render.
async function consented(page, baseURL) {
    await page.context().addCookies([{ name: 'cookie_consent', value: 'accepted', url: baseURL }]);
}

async function signedInAndSettled(page, user, wallId, baseURL) {
    await consented(page, baseURL);
    await signIn(page, user);
    await installWorker(page, `/walls/${wallId}`);
    test.skip(!(await hasPageModules(page)), 'needs the page modules');
    await settled(page);
    return partitionOf(page);
}

// The session expires: the browser drops the session cookie, as it does an expired one (consent stays).
async function expire(page, context) {
    await context.clearCookies({ name: '.AspNetCore.Cookies' });
    expect((await context.cookies()).map(c => c.name)).not.toContain('.AspNetCore.Cookies');
    await page.goto('/');
    await expect(page.locator('meta[name="bw-partition"]')).toHaveAttribute('content', 'anon');
    await expect(page.locator('meta[name="bw-partition"]')).toHaveAttribute('data-signout', 'expired');
    await page.evaluate(() => window.bwAssetCache.ready);
}

// One cached panel image of the partition, as { sha, width } and its asset URL on the wall.
async function cachedPanel(page, label, wallId) {
    const key = ((await assetKeys(page))[`bw-a1-${label}-img`] || []).find(k => k.includes('/'));
    expect(key, 'a cached panel image').toBeTruthy();
    const [sha, width] = key.split('/');
    return assetPath(wallId, sha, width);
}

// Whether the page's fetch of `path` reached the server (the worker did not answer it from the cache).
async function reachedServer(page, server, path) {
    const mark = await server.mark();
    await page.evaluate(u => fetch(u).then(r => r.arrayBuffer()).catch(() => null), path);
    return (await server.hits(mark, path)) > 0;
}

test('an expired session locks the data: kept, never served, the banner shown; the same user gets it back', async ({ page, context, world, server, baseURL }) => {
    const alpha = world.walls.alpha;
    const alice = await signedInAndSettled(page, world.users.alice, alpha.id, baseURL);
    const before = await ledger(page, alice);
    expect(before.walls.length).toBeGreaterThan(0);
    const panel = await cachedPanel(page, alice, alpha.id);
    expect(await reachedServer(page, server, panel), 'signed in, the cache answers').toBe(false);

    await expire(page, context);

    const locked = await ledger(page, alice);
    expect(locked.meta.activePartition).toBe('anon');
    expect(locked.meta.locked).toEqual(expect.objectContaining({ label: alice }));
    expect(locked.walls.length).toBe(before.walls.length);
    expect(locked.assets.length).toBe(before.assets.length);
    expect((await assetKeys(page))[`bw-a1-${alice}-img`]).toContain(panel.split('/a/')[1]);
    await expect(page.locator('#bw-offline-lock')).toBeVisible();
    await expect(page.locator('#bw-offline-lock')).toContainText('Sign in to keep your offline data');
    expect(await reachedServer(page, server, panel), 'locked, the worker never serves it').toBe(true);

    // Anonymous browsing keeps it: another anonymous page (rule 6, the every-start sweep).
    await page.goto('/oauth-select');
    await page.evaluate(() => window.bwAssetCache.ready);
    expect((await ledger(page, alice)).walls.length).toBe(before.walls.length);

    await signIn(page, world.users.alice, `/walls/${alpha.id}`);
    await settled(page);
    const back = await ledger(page, alice);
    expect(back.meta.activePartition).toBe(alice);
    expect(back.meta.locked ?? null).toBeNull();
    expect(back.walls.length).toBe(before.walls.length);
    await expect(page.locator('#bw-offline-lock')).toBeHidden();
    await expect.poll(() => reachedServer(page, server, panel), { timeout: 15000 }).toBe(false);
});

test('another user signing in on a locked device wipes the locked data at once', async ({ page, context, world, baseURL }) => {
    const alpha = world.walls.alpha;
    const alice = await signedInAndSettled(page, world.users.alice, alpha.id, baseURL);
    await expire(page, context);
    expect((await ledger(page, alice)).meta.locked).toEqual(expect.objectContaining({ label: alice }));

    await signIn(page, world.users.carol, `/walls/${alpha.id}`);
    await page.evaluate(() => window.bwAssetCache.ready);
    const after = await ledger(page, alice);
    expect(after.walls).toEqual([]);
    expect(after.assets).toEqual([]);
    expect(after.meta.locked ?? null).toBeNull();
    expect(Object.keys(await assetKeys(page)).filter(n => n.startsWith(`bw-a1-${alice}-`))).toEqual([]);
    await expect(page.locator('#bw-offline-lock')).toBeHidden();
});

test('"Clear offline data" on the lock banner wipes the locked data', async ({ page, context, world, baseURL }) => {
    const alpha = world.walls.alpha;
    const alice = await signedInAndSettled(page, world.users.alice, alpha.id, baseURL);
    await expire(page, context);
    const banner = page.locator('#bw-offline-lock');
    await expect(banner).toBeVisible();
    await banner.getByRole('button', { name: 'Clear offline data' }).click();
    // The clear is done once the lock is gone from the ledger (it goes last, after the partition's rows).
    await expect.poll(async () => {
        const state = await ledger(page, alice);
        return { walls: state.walls.length, locked: state.meta.locked ?? null };
    }).toEqual({ walls: 0, locked: null });
    await expect(banner).toBeHidden();
    expect(Object.keys(await assetKeys(page)).filter(n => n.startsWith(`bw-a1-${alice}-`))).toEqual([]);
});
