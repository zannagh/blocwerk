// The leaner boulder page at 375 px: no hold-count tags, no "My stats" line, the pencil opens the full editor, a status
// icon sits by the grade, and the setter's free-text custom rule shows as plain text and is editable in the editor.
import { test, expect, signIn } from '../harness/fixtures.mjs';

test.use({ phone: 'iPhone 13 Mini' });

// The editor holds an edit lease; leave the page so no later spec sees a busy deploy gate.
test.afterEach(async ({ page }) => {
    if (page.url().startsWith('http')) {
        await page.evaluate(() => window.Blazor && window.Blazor.navigateTo('/walls')).catch(() => undefined);
    }
    await expect.poll(async () => {
        const ready = await page.request.get('/health/ready-to-deploy');
        return `${ready.status()} ${(await ready.text()).trim()}`;
    }, { timeout: 15000 }).toBe('200 idle');
});

// The browsers share one seeded world, so a spec that logs or edits picks a boulder of its own per browser.
const slot = testInfo => ['chromium', 'webkit', 'firefox'].indexOf(testInfo.project.name);

async function openBetaBoulder(page, world, name) {
    await signIn(page, world.users.alice, `/walls/${world.walls.beta.id}?view=boulders`);
    await page.locator('.boulder-card', { hasText: name }).first().click();
    await expect(page.locator('.rules-bar')).toBeVisible();
}

const noHorizontalScroll = page => expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);

test('the boulder page drops the hold counts, the stats line and the separate Revise button, and shows the sent icon', async ({ page, world }) => {
    const alpha = world.walls.alpha;
    await signIn(page, world.users.alice, `/walls/${alpha.id}/boulders/${alpha.boulder}`);
    await expect(page.locator('.rules-bar')).toBeVisible();
    await expect(page.locator('.legend-chip')).toHaveCount(0);
    await expect(page.locator('.my-stats')).toHaveCount(0);
    await expect(page.locator('.btn-revise')).toHaveCount(0);
    await expect(page.locator('.view-toggle')).toHaveCount(0);

    const status = page.locator('.title-row .boulder-status');
    await expect(status).toHaveCount(1);
    await expect(status).toHaveAccessibleName('Sent');
    await expect(page.locator('.boulder-edit-btn')).toHaveAttribute('href', `/walls/${alpha.id}/boulders/${alpha.boulder}/revise`);

    // The custom rule is a banner of plain text: the markup in it is shown, not parsed.
    await expect(page.locator('.boulder-custom-rule')).toHaveText('No heel hooks <b>here</b>');
    await expect(page.locator('.boulder-custom-rule b')).toHaveCount(0);
    await noHorizontalScroll(page);
    await page.screenshot({ path: '.out/shots/boulder-page-375.png' });
    await page.emulateMedia({ colorScheme: 'dark' });
    await expect(page.locator('.title-row .boulder-status')).toBeVisible();
    await page.screenshot({ path: '.out/shots/boulder-page-375-dark.png' });
});

test('a flash shows the thunder icon; a boulder without activity shows no icon', async ({ page, world }) => {
    const alpha = world.walls.alpha;
    await signIn(page, world.users.carol, `/walls/${alpha.id}/boulders/${alpha.boulder}`);
    await expect(page.locator('.title-row .boulder-status')).toHaveAccessibleName('Flashed');

    // A Beta boulder nothing ever logs on (the logging spec below uses others): no activity, no icon.
    await openBetaBoulder(page, world, 'List boulder 30');
    await expect(page.locator('.boulder-status')).toHaveCount(0);
});

test('the header row keeps favourite, edit, 3D and the status icon inside 375 px', async ({ page, world }) => {
    const alpha = world.walls.alpha;
    await signIn(page, world.users.alice, `/walls/${alpha.id}/boulders/${alpha.boulder}`);
    const parts = ['.title-row .boulder-status', '.title-actions .fav-toggle:visible', '.title-actions .boulder-edit-btn', '.title-actions .btn-view3d'];
    for (const sel of parts) {
        const box = await page.locator(sel).first().boundingBox().catch(() => null);
        if (box) {
            expect(box.x + box.width, sel).toBeLessThanOrEqual(375);
        }
    }
    await noHorizontalScroll(page);
});

test('the pencil opens the full editor, where the custom rule is set and then shown on the boulder page', async ({ page, world }, testInfo) => {
    const text = `Bring chalk ${slot(testInfo)}`;
    const mp = world.multiPanel;
    await signIn(page, world.users.pia, `/walls/${mp.wallId}/boulders/${mp.boulder}`);
    await page.locator('.boulder-edit-btn').click();
    await expect(page).toHaveURL(new RegExp(`/boulders/${mp.boulder}/revise$`));
    await expect(page.locator('.form-group', { hasText: 'Name' }).locator('input')).toBeVisible();
    await expect(page.locator('.form-group', { hasText: 'Grade' })).toBeVisible();
    const rule = page.locator('#boulder-custom-rule');
    await rule.scrollIntoViewIfNeeded();
    await expect.poll(() => rule.evaluate(e => parseFloat(getComputedStyle(e).fontSize))).toBeGreaterThanOrEqual(16);
    await noHorizontalScroll(page);
    await page.screenshot({ path: '.out/shots/revise-rules-375.png', fullPage: true });

    await rule.fill(`  ${text}  `);
    // The cookie banner sits over the bottom of a phone screen: accept it, as a visitor would, then bring Save to the middle.
    const consent = page.locator('.bw-consent');
    if (await consent.isVisible()) {
        await consent.getByRole('button', { name: 'OK' }).click();
        await expect(consent).toBeHidden();
    }
    const save = page.locator('.btn-submit');
    await expect(save).toBeEnabled();
    await save.evaluate(e => e.scrollIntoView({ block: 'center' }));
    // While a selected hold sits on a panel not looked at yet, Save first shows that panel ("Check 3 selected ... first").
    for (let i = 0; i < 3 && /^Check/.test((await save.textContent()).trim()); i++) {
        await save.click({ timeout: 15000 });
        await expect(save).toHaveText(/Save Revision/);
        await save.evaluate(e => e.scrollIntoView({ block: 'center' }));
    }
    await save.click({ timeout: 15000 });
    await expect(page).toHaveURL(new RegExp(`/boulders/${mp.boulder}$`));
    await expect(page.locator('.boulder-custom-rule')).toHaveText(text);
    await noHorizontalScroll(page);
});

test('logging an attempt, a send and a flash still works, and the icon follows the best result', async ({ page, world }, testInfo) => {
    await openBetaBoulder(page, world, `List boulder ${String(5 + slot(testInfo)).padStart(2, '0')}`);
    const status = page.locator('.title-row .boulder-status');
    await expect(status).toHaveCount(0);
    await page.locator('[data-bw-action="attempt"][data-bw-type="Attempt"]').click();
    await expect(status).toHaveAccessibleName('Attempted', { timeout: 20000 });
    await page.locator('[data-bw-action="attempt"][data-bw-type="Send"]').click();
    await expect(status).toHaveAccessibleName('Sent', { timeout: 20000 });
    await page.locator('[data-bw-action="attempt"][data-bw-type="Flash"]').click();
    await expect(status).toHaveAccessibleName('Flashed', { timeout: 20000 });
    await page.screenshot({ path: '.out/shots/boulder-flashed-375.png' });
});
