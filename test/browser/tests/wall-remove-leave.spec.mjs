// Removing a member and leaving a wall, on the Members wall (seed/SeedMembers.cs). Rhea, its owner, removes Max from the
// member list after a confirm; Nia leaves from her own row (the "delete my beta videos" choice ticked by default) and
// lands on her wall list without the wall; Max's old invite link no longer lets him back in, while the wall's public share
// link still shows it. One browser only: the steps change the seeded world. Screenshots of the member list
// (phone 402x874 at 3x and desktop 1440x900, light and dark) land in test/browser/.out/members-*.png.
import { test, expect, signIn } from '../harness/fixtures.mjs';

const PHONE = { viewport: { width: 402, height: 874 }, deviceScaleFactor: 3 };
const DESKTOP = { viewport: { width: 1440, height: 900 } };

test.skip(({ browserName }) => browserName !== 'chromium', 'Removing and leaving change the shared seed; one browser runs them.');

async function openAs(browser, baseURL, user, path, size) {
    const context = await browser.newContext({ baseURL, ...size });
    await context.addCookies([{ name: 'cookie_consent', value: 'accepted', url: baseURL }]);
    const page = await context.newPage();
    await signIn(page, user, path);
    return { context, page };
}

// The member list is the wall carousel's Members page; the overview's Members tile goes there once the circuit is live.
async function members(page) {
    await expect(page.locator('.wall-carousel:not(.carousel-pending)')).toBeVisible();
    const rows = page.locator('.member-row');
    await expect(async () => {
        await page.locator('.stat.clickable', { hasText: 'Members' }).click({ timeout: 2000 });
        await expect(rows.first()).toBeInViewport({ timeout: 2000 });
    }).toPass();
    return rows;
}

async function shoot(page, name) {
    for (const scheme of ['light', 'dark']) {
        await page.emulateMedia({ colorScheme: scheme });
        await page.screenshot({ path: `.out/members-${name}-${scheme}.png` });
    }
    await page.emulateMedia({ colorScheme: 'light' });
}

test('an admin removes a member, a member leaves, and the old invite no longer joins', async ({ browser, baseURL, world }) => {
    test.setTimeout(180_000); // four sign-ins and two circuits per step
    const wall = world.walls.members;
    const { rhea, max, nia } = world.users;

    const desktop = await openAs(browser, baseURL, rhea, `/walls/${wall.id}`, DESKTOP);
    await members(desktop.page);
    await shoot(desktop.page, 'admin-desktop');
    await desktop.context.close();

    const owner = await openAs(browser, baseURL, rhea, `/walls/${wall.id}`, PHONE);
    const rows = await members(owner.page);
    await expect(rows).toHaveCount(4);
    await expect(owner.page.getByRole('button', { name: 'Remove', exact: true })).toHaveCount(3); // never on the owner's row
    await shoot(owner.page, 'admin-phone');

    await owner.page.getByRole('button', { name: 'Remove', exact: true }).first().click();
    await expect(owner.page.locator('.confirm-action')).toContainText('Remove Max');
    await shoot(owner.page, 'confirm-phone');
    await owner.page.locator('.confirm-action').getByRole('button', { name: 'Remove', exact: true }).click();
    await expect(rows).toHaveCount(3);
    await expect(rows.filter({ hasText: 'Max' })).toHaveCount(0);
    await owner.context.close();

    const leaver = await openAs(browser, baseURL, nia, `/walls/${wall.id}`, PHONE);
    await members(leaver.page);
    await expect(leaver.page.getByRole('button', { name: 'Remove', exact: true })).toHaveCount(0);
    await shoot(leaver.page, 'member-phone');
    await leaver.page.getByRole('button', { name: 'Leave wall' }).click();
    await expect(leaver.page.locator('.confirm-action input[type=checkbox]')).toBeChecked();
    await shoot(leaver.page, 'leave-confirm-phone');
    await leaver.page.locator('.confirm-action').getByRole('button', { name: 'Leave', exact: true }).click();
    await expect(leaver.page).toHaveURL(/\/walls$/);
    await expect(leaver.page.getByText('Members', { exact: true })).toHaveCount(0);
    await leaver.context.close();

    const removed = await openAs(browser, baseURL, max, '/join/browser-members-join-token', PHONE);
    await expect(removed.page.locator('.error-state')).toContainText('Invalid invite token');
    await removed.page.goto('/walls');
    await expect(removed.page.getByText('Members', { exact: true })).toHaveCount(0);
    await removed.context.close();

    const anonymous = await browser.newContext({ baseURL, ...PHONE });
    const viewer = await anonymous.newPage();
    const shared = await viewer.request.get(`/walls/${wall.id}/shared/browser-members-share-token`, { maxRedirects: 0 });
    expect(shared.status()).toBe(200);
    await anonymous.close();
});
