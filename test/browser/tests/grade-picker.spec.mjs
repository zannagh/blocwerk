// The grade picker opens on the scale the grade is written in: the boulder page's rename editor on a Font grade ("6B")
// used to open on the V-Scale with nothing selected, because it passed no preferred scale.
import { test, expect, signIn } from '../harness/fixtures.mjs';

// The rename editor holds an edit lease; leave the page so no later spec sees a busy deploy gate.
test.afterEach(async ({ page }) => {
    await page.evaluate(() => window.Blazor && window.Blazor.navigateTo('/walls')).catch(() => undefined);
    await expect.poll(async () => {
        const ready = await page.request.get('/health/ready-to-deploy');
        return `${ready.status()} ${(await ready.text()).trim()}`;
    }, { timeout: 15000 }).toBe('200 idle');
});

test('the inline grade editor opens on Font with the boulder\'s Font grade selected', async ({ page, world }) => {
    const mp = world.multiPanel;
    await signIn(page, world.users.pia, `/walls/${mp.wallId}/boulders/${mp.boulder}`);
    await page.locator('.boulder-edit-btn').click();
    const editor = page.locator('.boulder-meta-edit');
    await expect(editor.locator('.scale-btn.active')).toHaveText('Font');
    await expect(editor.locator('select.grade-select')).toHaveValue('6B');
    await editor.getByRole('button', { name: 'Cancel' }).click();
    await expect(editor).toHaveCount(0);
});
