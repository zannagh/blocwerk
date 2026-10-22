// Back from a boulder to the wall's boulder list (?view=boulders) must land straight on the list: no painted frame
// of the carousel on any other page (the "blank page 1, then jump" report), and the list revealed as soon as the
// track is positioned rather than after the interop round-trips that follow.
import { test, expect, signIn } from '../harness/fixtures.mjs';

const BOULDER_PAGE = 3;

// Samples every animation frame from the first document on (an enhanced navigation keeps the document, so the
// sampler survives it): while a carousel is visible, which page is its scroll offset on?
function installSampler() {
    window.__bwCarouselFrames = [];
    const tick = () => {
        const el = document.querySelector('.wall-carousel');
        if (el && !el.classList.contains('carousel-pending') && el.clientWidth > 0) {
            window.__bwCarouselFrames.push({ t: performance.now(), page: Math.round(el.scrollLeft / el.clientWidth), path: location.pathname + location.search });
        }
        requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
}

test('back from a boulder lands on the boulder list with no frame of another carousel page', async ({ page, world }) => {
    const wall = world.walls.alpha;
    await signIn(page, world.users.alice, `/walls/${wall.id}?view=boulders`);
    await page.addInitScript(installSampler);
    await page.goto(`/walls/${wall.id}?view=boulders`);

    const card = page.locator('.boulders-page a[href*="/boulders/"]').first();
    await expect(card).toBeVisible();
    await card.click();
    await expect(page).toHaveURL(/\/boulders\//);
    await expect(page.locator('.boulder-detail h1')).toBeVisible();

    const backAt = await page.evaluate(() => { window.__bwCarouselFrames = []; return performance.now(); });
    await page.goBack();
    await expect(page).toHaveURL(/\?view=boulders$/);
    await expect(page.locator('.wall-carousel:not(.carousel-pending)')).toBeVisible();
    await expect.poll(() => page.evaluate(() => window.__bwCarouselFrames.length)).toBeGreaterThan(5);

    const frames = await page.evaluate(() => window.__bwCarouselFrames);
    const wrong = frames.filter(f => f.path.includes('?view=boulders') && f.page !== BOULDER_PAGE);
    expect(wrong, 'a visible carousel frame on another page').toEqual([]);
    const first = frames.find(f => f.path.includes('?view=boulders'));
    test.info().annotations.push({ type: 'back-to-list-ms', description: String(Math.round(first.t - backAt)) });
    console.log(`back-to-list: list visible ${Math.round(first.t - backAt)} ms after back`);
});
