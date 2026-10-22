// The wall page's "Update wall" link lives on the settings page (carousel page 1), right below the Activity card, and
// no longer on the overview under "View in 3D", where a stray tap started an update. Wall admins only.
import { test, expect, signIn } from '../harness/fixtures.mjs';

const pages = page => page.locator('.wall-carousel > .wall-page');

for (const width of [1280, 390]) {
    test(`a wall admin finds Update wall below Activity on the settings page (${width}px)`, async ({ page, world }) => {
        await page.setViewportSize({ width, height: 844 });
        const wall = world.walls.alpha;
        await signIn(page, world.users.alice, `/walls/${wall.id}`);
        await expect(pages(page)).toHaveCount(5);

        const settings = pages(page).nth(0);
        const link = settings.locator('details:has(> summary h2:text-is("Activity")) + a.wall-update-link');
        await expect(link).toHaveText('Update wall');
        await expect(link).toHaveAttribute('href', `/walls/${wall.id}/update`);
        await expect(page.locator('a.wall-update-link')).toHaveCount(1);
        await expect(pages(page).nth(2).locator(`a[href="/walls/${wall.id}/update"]`)).toHaveCount(0);

        await page.getByRole('button', { name: 'Page 1', exact: true }).click();
        // The carousel scrolls there smoothly: wait for the link to come to rest inside the viewport.
        await expect.poll(async () => {
            const box = await link.boundingBox();
            return box && box.x >= 0 && box.x + box.width <= width;
        }).toBe(true);
        await expect(link).toBeInViewport({ ratio: 1 });
    });
}

test('a wall member sees no Update wall', async ({ page, world }) => {
    const wall = world.walls.alpha;
    await signIn(page, world.users.carol, `/walls/${wall.id}`);
    await expect(pages(page).nth(0).getByRole('heading', { name: 'Activity' })).toBeAttached();
    await expect(page.locator(`a[href="/walls/${wall.id}/update"]`)).toHaveCount(0);
});
