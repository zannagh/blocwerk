// The offline client's views (Phase 3 PR 3.11) on the real engines, with the server down from the first offline
// navigation on ("airplane mode" is `server.unreachable()`: a connect is refused, as with no network): a cached wall
// opens at its /v/ twin with its photo from the device, its boulder list and, in the client, the boulder detail with
// its comments and the viewer's own marks; nothing on any of them edits; a reload still works from the device alone.
import { test, expect } from '../harness/fixtures.mjs';
import { precacheAs, precacheAsAlice, expectClient } from '../harness/client.mjs';

const READY = 'bw-client-ready';
const ALLOWED_BUTTONS = ['panel-arrow', 'holds-all-tool', 'settings-card-summary', 'scale-btn', 'bw-client-clear'];

// Every control on the page: no form, no textarea, no Server write action, and only the chevrons, "Show all holds", the
// filter chip and the frame's own Clear offline data as buttons.
async function expectReadOnly(page) {
    await expect(page.locator('form, textarea, [contenteditable], [data-bw-action]')).toHaveCount(0);
    const buttons = await page.locator('button').evaluateAll(list => list.map(b => b.className));
    expect(buttons.filter(c => !ALLOWED_BUTTONS.some(a => c.includes(a)))).toEqual([]);
    const hrefs = await page.locator('a[href]').evaluateAll(list => list.map(a => a.getAttribute('href')));
    expect(hrefs.filter(h => /revise|create|update|edit|settings|kiosk/.test(h))).toEqual([]);
}

// The photo the panel viewer shows has loaded (from the device: the server is down).
async function expectPhoto(page) {
    const photo = page.locator('.panel-viewer img.wall-photo');
    await expect(photo).toHaveCount(1);
    await expect.poll(() => photo.evaluate(img => img.complete && img.naturalWidth), { timeout: 15000 }).toBeGreaterThan(0);
}

test('airplane mode on a cached wall: the wall view, the boulder detail with its comments, and a reload, from the device alone', async ({ page, world, server }) => {
    const social = world.social;
    expect(await precacheAs(page, server, world.users.erin, `/walls/${social.wallId}`)).toBe('complete');
    await server.unreachable();
    const mark = await server.mark();

    await expectClient(page, `/walls/${social.wallId}`);
    await expect(page.locator('h1')).toHaveText('Social');
    await expect(page.locator('.bw-client-store')).toHaveAttribute('data-bw-store', 'ready');
    await expect(page.locator('.boulder-card', { hasText: 'Social boulder' })).toBeVisible();
    // Erin set the wall's boulders: the setters' class carries the draft, and the archive sits apart.
    await expect(page.locator('.boulder-card', { hasText: 'Secret draft' }).locator('.draft-badge')).toBeVisible();
    await expect(page.locator('.archive-section .boulder-card')).toHaveCount(1);
    await expectPhoto(page);
    await expectReadOnly(page);

    // Into the boulder within the client: the router navigates in place, the runtime does not start again.
    await page.locator('.boulder-card', { hasText: 'Social boulder' }).click();
    await expect(page).toHaveURL(new RegExp(`/v/walls/${social.wallId}/boulders/${social.boulder}$`));
    await expect(page.locator('h1')).toHaveText('Social boulder');
    expect(await page.evaluate(m => performance.getEntriesByName(m).length, READY)).toBe(1);
    const comment = text => page.locator('.comment-row', { hasText: text });
    await expect(comment(social.comment).locator('.comment-author')).toHaveText('Cora');
    await expect(comment(social.unconsentedComment).locator('.comment-author')).toHaveText('Dave');
    await expectPhoto(page);
    await expectReadOnly(page);

    // A reload while still offline: the shell, the runtime and the data all come from the device.
    await page.reload();
    await expect.poll(() => page.evaluate(m => performance.getEntriesByName(m).length, READY), { timeout: 30000 }).toBe(1);
    await expect(page.locator('h1')).toHaveText('Social boulder');
    await expect(comment(social.comment)).toBeVisible();

    await page.getByRole('link', { name: 'Back to boulder list' }).click();
    await expect(page).toHaveURL(new RegExp(`/v/walls/${social.wallId}$`));
    await expect(page.locator('h1')).toHaveText('Social');
    expect((await server.log(mark)).filter(e => !e.fault), 'nothing reached the server').toEqual([]);
});

test('offline, the viewer\'s own marks show on the list and the boulder, and the wall list holds the device\'s walls', async ({ page, world, server }) => {
    const alpha = world.walls.alpha;
    expect(await precacheAsAlice(page, world, server)).toBe('complete');
    await server.unreachable();

    await expectClient(page, '/walls');
    await expect(page.locator('h1')).toHaveText('My Walls');
    await expect(page.locator('.wall-card h3', { hasText: 'Alpha' })).toBeVisible();
    await expectReadOnly(page);

    await page.locator('.wall-card', { hasText: 'Alpha' }).click();
    await expect(page).toHaveURL(new RegExp(`/v/walls/${alpha.id}$`));
    const row = page.locator(`.boulder-card[data-bw-boulder="${alpha.boulder}"]`);
    await expect(row.locator('.fav-icon')).toBeVisible();
    await expect(row.locator('.send-icon, .flash-icon')).toBeVisible();
    await expectPhoto(page);

    await row.click();
    await expect(page.locator('.my-stats')).toContainText('My stats:');
    await expect(page.locator('.my-stats').locator('.send-chip, .flash-chip')).toBeVisible();
    await expectReadOnly(page);
});
