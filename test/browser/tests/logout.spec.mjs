// Sign-out on the device: the signed-out page announces bw:identity-changed (PR 2.5 W1), and with the 2.7 page
// modules the header's Logout link wipes the asset cache before it leaves (rule 2), under a live Blazor page.
import { test, expect, signIn, installWorker, partitionOf, cacheNames, hasPageModules, settled, ledger, assetKeys } from '../harness/fixtures.mjs';

// Every bw:identity-changed, with the path of the document that fired it, kept across this tab's navigations.
async function recordIdentityEvents(page) {
    await page.addInitScript(() => {
        window.addEventListener('bw:identity-changed', () => {
            const seen = JSON.parse(sessionStorage.getItem('bw-test-identity') || '[]');
            seen.push(location.pathname);
            sessionStorage.setItem('bw-test-identity', JSON.stringify(seen));
        });
    });
}

const identityEvents = page => page.evaluate(() => JSON.parse(sessionStorage.getItem('bw-test-identity') || '[]'));

async function logOutByLink(page) {
    const signedOut = page.waitForResponse(r => new URL(r.url()).pathname === '/account/logout');
    await page.getByRole('link', { name: 'Logout' }).click();
    expect((await signedOut).status()).toBe(200);
    // The signed-out page moves on by location.replace; the page it lands on is anonymous.
    await expect(page.locator('meta[name="bw-partition"]')).toHaveAttribute('content', 'anon');
    await expect(page.getByRole('link', { name: 'Logout' })).toHaveCount(0);
    // Settled on the landing page with its modules, not on a document still on its way there.
    await page.waitForLoadState('load');
    await page.waitForFunction(() => location.pathname !== '/account/logout' && !!window.bwAssetCache && !!window.bwAssetLedger);
    await page.evaluate(() => window.bwAssetCache.ready);
}

test('the signed-out page fires bw:identity-changed and moves on signed out', async ({ page, world }) => {
    await recordIdentityEvents(page);
    await signIn(page, world.users.alice);
    await installWorker(page);
    await logOutByLink(page);
    expect(await identityEvents(page)).toContain('/account/logout');
    expect(await partitionOf(page)).toBe('anon');
});

test('the Logout link wipes the signed-in partition first, and the navigation still happens', async ({ page, world }) => {
    await recordIdentityEvents(page);
    await signIn(page, world.users.alice);
    await installWorker(page);
    test.skip(!(await hasPageModules(page)), 'needs the 2.7 page modules');
    await settled(page);
    const alice = await partitionOf(page);
    expect((await assetKeys(page))[`bw-a1-${alice}-img`] || []).not.toHaveLength(0);
    const before = await ledger(page, alice);
    expect(before.kind).toBe('idb');
    expect(before.walls.length).toBeGreaterThan(0);
    const from = new URL(page.url()).pathname;

    await logOutByLink(page);

    // Fired twice: by the click interception on the page that was left, and by the signed-out page.
    expect(await identityEvents(page)).toEqual(expect.arrayContaining([from, '/account/logout']));
    expect((await cacheNames(page)).filter(n => n.startsWith(`bw-a1-${alice}-`))).toEqual([]);
    const after = await ledger(page, alice);
    expect(after.walls).toEqual([]);
    expect(after.assets).toEqual([]);
    expect(after.meta.activePartition).toBe('anon');
});
