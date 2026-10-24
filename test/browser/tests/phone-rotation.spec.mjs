// Turning a phone to landscape and back on a carousel page (the boulder list, the wall overview) must leave the track
// on the page it was on: same slide, same dot, same ?view=. The browser re-snaps the mandatory track against the new
// page width before any resize signal arrives, and WebKit's `scrollend` for that snap used to be measured as a swipe:
// 3 x 390px is 1.95 pages of 600px, so the list "became" the overview and that was reported to the server for good.
import { test, expect, signIn } from '../harness/fixtures.mjs';

test.use({ phone: 'iPhone 13' });

const PORTRAIT = { width: 390, height: 664 };
const LANDSCAPE = { width: 844, height: 390 };

function track(page) {
    return page.evaluate(() => {
        const el = document.querySelector('.wall-carousel');
        const dots = [...document.querySelectorAll('.carousel-dots .dot')];
        return {
            page: Math.round(el.scrollLeft / el.clientWidth),
            offPage: Math.abs(el.scrollLeft - Math.round(el.scrollLeft / el.clientWidth) * el.clientWidth),
            dot: dots.findIndex(d => d.classList.contains('active')),
            url: location.pathname + location.search,
            stage: !!document.querySelector('.bw-stage-overlay')
        };
    });
}

async function expectOn(page, index, url) {
    await expect.poll(async () => {
        const t = await track(page);
        return t.offPage <= 1 ? t.page : -1;
    }, { message: 'the track resting on the page it was on' }).toBe(index);
    const t = await track(page);
    expect(t.dot).toBe(index);
    expect(t.url).toBe(url);
    // A rotation never brings up the landscape takeover on a carousel page: that is only ever opened by its button.
    expect(t.stage).toBe(false);
}

for (const index of [1, 2, 3]) {
    test(`rotating to landscape and back keeps carousel page ${index + 1}`, async ({ page, world }) => {
        const wall = world.walls.beta;
        await page.setViewportSize(PORTRAIT);
        await signIn(page, world.users.alice, `/walls/${wall.id}`);
        await expect(page.locator('.wall-carousel:not(.carousel-pending)')).toBeVisible();
        await page.getByRole('button', { name: `Page ${index + 1}`, exact: true }).click();
        await expect.poll(async () => (await track(page)).offPage <= 1 && (await track(page)).page === index).toBe(true);
        await page.waitForTimeout(600);
        const url = (await track(page)).url;

        await page.setViewportSize(LANDSCAPE);
        await page.waitForTimeout(900);
        await expectOn(page, index, url);

        await page.setViewportSize(PORTRAIT);
        await page.waitForTimeout(900);
        await expectOn(page, index, url);

        // Still a working carousel afterwards: a dot moves it, and the list's own scroll is untouched.
        await page.getByRole('button', { name: 'Page 4', exact: true }).click();
        await expect(page).toHaveURL(/\?view=boulders$/);
        await expect(page.locator('.wall-carousel > .wall-page').nth(3).locator('.boulder-card').first()).toBeInViewport();
    });
}

test('a track left between pages by a resize is put back on its page', async ({ page, world }) => {
    const wall = world.walls.beta;
    await page.setViewportSize(PORTRAIT);
    await signIn(page, world.users.alice, `/walls/${wall.id}?view=boulders`);
    await expect(page.locator('.wall-carousel:not(.carousel-pending)')).toBeVisible();
    await page.waitForTimeout(600);
    // Narrower and back, with the track nudged off its page in between: the remembered page wins over the pixels.
    await page.setViewportSize({ width: 360, height: 664 });
    await page.evaluate(() => { const el = document.querySelector('.wall-carousel'); el.style.scrollSnapType = 'none'; el.scrollLeft -= 140; });
    await page.evaluate(() => { document.querySelector('.wall-carousel').style.scrollSnapType = ''; });
    await page.setViewportSize(PORTRAIT);
    await page.waitForTimeout(900);
    await expectOn(page, 3, `/walls/${wall.id}?view=boulders`);
});
