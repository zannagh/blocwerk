// A kiosk tablet resting on its wall (nobody picked) reads the wall's comments and activity, read-only: no comment box,
// no delete, and only the public part of the log (no draft's or archived boulder's entries, no membership entries).
// New comments and activity reach it live. Seeded by seed/SeedSocial.cs.
import { test, expect, signIn } from '../harness/fixtures.mjs';

async function registerKiosk(page, token) {
    await page.goto('/oauth-select');
    await page.getByText('Register this device as a wall kiosk').click();
    await page.getByLabel('Kiosk key').fill(token);
    await page.getByRole('button', { name: 'Register kiosk' }).click();
    await page.waitForURL(/\/walls\/[0-9a-f-]{36}/);
}

const boulderUrl = (world, id) => `/walls/${world.walls.alpha.id}/boulders/${id}`;

test('the resting kiosk reads the wall activity card, without the private part', async ({ page, world }) => {
    await registerKiosk(page, world.kioskToken);
    await page.goto(`/walls/${world.walls.alpha.id}`);

    const card = page.locator('.settings-card').filter({ has: page.locator('h2', { hasText: 'Activity' }) });
    await expect(card.locator('.activity-row').first()).toBeAttached();
    await expect(card).not.toContainText("Log in to see this wall's activity");
    await expect(card).toContainText('Carol logged an attempt');
    await expect(card).toContainText('Carol commented on a boulder');
    await expect(card.locator('.activity-row', { hasText: 'created a boulder' })).toHaveCount(1); // not the draft's
    await expect(card).not.toContainText('joined the wall');
});

test('the resting kiosk reads a boulder\'s comments and activity read-only, and gets new ones live', async ({ page, browser, baseURL, world }) => {
    await registerKiosk(page, world.kioskToken);
    await page.goto(boulderUrl(world, world.social.boulder));

    const comments = page.locator('.comments-section');
    await expect(comments).toContainText(world.social.comment);
    await expect(comments.locator('.comment-input')).toHaveCount(0);
    await expect(comments.locator('.comment-submit')).toHaveCount(0);
    await expect(comments.locator('.delete-btn')).toHaveCount(0);
    const activity = page.locator('.activity-feed');
    await expect(activity).toContainText('Carol sent this');
    await expect(activity).toContainText('Alice created this');
    await expect(activity).not.toContainText('No activity yet');

    // Carol comments from her own phone; the tablet's circuit gets the comment and the activity entry from the feed.
    const phone = await browser.newContext({ baseURL });
    try {
        const carol = await phone.newPage();
        await signIn(carol, world.users.carol, boulderUrl(world, world.social.boulder));
        const text = `Live from Carol ${Date.now()}`;
        await carol.locator('.comment-input').fill(text);
        await carol.locator('.comment-submit').click();
        await expect(carol.locator('.comments-section')).toContainText(text);

        await expect(comments).toContainText(text);
        await expect(activity.locator('.activity-row').first()).toContainText('Carol commented');
    } finally {
        await phone.close();
    }
});

test('the resting kiosk reads nothing of an archived boulder', async ({ page, world }) => {
    await registerKiosk(page, world.kioskToken);
    await page.goto(boulderUrl(world, world.social.archived));

    await expect(page.locator('.comments-section')).toContainText('No comments yet.');
    await expect(page.locator('.comments-section')).not.toContainText('Old beta');
    await expect(page.locator('.activity-feed')).toContainText('No activity yet.');
});
