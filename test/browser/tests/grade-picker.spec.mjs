// The grade picker opens on the scale the grade is written in: the editor on a Font grade ("6B") used to open on the
// V-Scale with nothing selected, because it passed no preferred scale. The boulder page's pencil opens the full editor.
import { test, expect, signIn } from '../harness/fixtures.mjs';

// The editor holds an edit lease; leave the page so no later spec sees a busy deploy gate.
test.afterEach(async ({ page }) => {
    await page.evaluate(() => window.Blazor && window.Blazor.navigateTo('/walls')).catch(() => undefined);
    await expect.poll(async () => {
        const ready = await page.request.get('/health/ready-to-deploy');
        return `${ready.status()} ${(await ready.text()).trim()}`;
    }, { timeout: 15000 }).toBe('200 idle');
});

test('the pencil opens the full editor on Font with the boulder\'s Font grade selected', async ({ page, world }) => {
    const mp = world.multiPanel;
    await signIn(page, world.users.pia, `/walls/${mp.wallId}/boulders/${mp.boulder}`);
    await page.locator('.boulder-edit-btn').click();
    await expect(page).toHaveURL(new RegExp(`/walls/${mp.wallId}/boulders/${mp.boulder}/revise$`));
    await expect(page.locator('.scale-btn.active')).toHaveText('Font');
    await expect(page.locator('select.grade-select')).toHaveValue('6B');
});
