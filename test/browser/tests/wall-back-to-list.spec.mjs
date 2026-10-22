// Back from a boulder to the wall's boulder list (?view=boulders) must land straight on the list: no painted frame of a
// hidden carousel, of the carousel on another page, or with another page's dot active (the "blank page 1, then jump"
// report), with the list's own scroll position and URL kept, and with one server load per visit.
import { test, expect, signIn } from '../harness/fixtures.mjs';
import { serverStats, statsDelta } from '../harness/server-stats.mjs';

const LIST_PAGE = 3;

// Samples every animation frame (an enhanced navigation keeps the document, so the sampler survives it): what does the
// wall page show right now?
function installSampler() {
    window.__bwFrames = [];
    const tick = () => {
        const el = document.querySelector('.wall-carousel');
        const frame = { t: performance.now(), path: location.pathname + location.search, state: 'none' };
        if (el) {
            const hidden = el.classList.contains('carousel-pending') || getComputedStyle(el).visibility === 'hidden' || el.clientWidth === 0;
            const dots = [...document.querySelectorAll('.carousel-dots .dot')];
            frame.state = hidden ? 'hidden' : 'shown';
            frame.page = el.clientWidth > 0 ? Math.round(el.scrollLeft / el.clientWidth) : -1;
            frame.dot = dots.findIndex(d => d.classList.contains('active'));
            frame.cards = el.querySelectorAll('.boulder-card').length;
        }
        window.__bwFrames.push(frame);
        requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
}

const listPage = page => page.locator('.wall-carousel > .wall-page').nth(LIST_PAGE);

async function openList(page, world, wall) {
    await signIn(page, world.users.alice, `/walls/${wall.id}?view=boulders`);
    await page.addInitScript(installSampler);
    await page.goto(`/walls/${wall.id}?view=boulders`);
    await expect(page.locator('.wall-carousel:not(.carousel-pending)')).toBeVisible();
    await expect(listPage(page).locator('.boulder-card').first()).toBeInViewport();
}

// From the moment the history entry is the list again: every frame that shows a carousel shows it, on the list, with
// the list's dot. Returns the ms from the back press to the first frame with the list's cards on screen.
async function expectStraightToList(page, backAt) {
    await expect.poll(() => page.evaluate(() => window.__bwFrames.some(f => f.state === 'shown' && f.cards > 0 && f.path.includes('view=boulders'))),
        { message: 'the list on screen under ?view=boulders' }).toBe(true);
    await page.waitForTimeout(300);
    const frames = (await page.evaluate(() => window.__bwFrames)).filter(f => f.path.includes('view=boulders'));
    const bad = frames.filter(f => f.state === 'hidden' || (f.state === 'shown' && (f.page !== LIST_PAGE || f.dot !== LIST_PAGE)));
    const shown = frames.find(f => f.state === 'shown' && f.cards > 0);
    const ms = Math.round(shown.t - backAt);
    console.log(`back-to-list: list on screen ${ms} ms after back; ${bad.length} bad frame(s) ${JSON.stringify(bad.slice(0, 3))}`);
    test.info().annotations.push({ type: 'back-to-list-ms', description: String(ms) });
    expect.soft(bad, 'a frame with the carousel hidden, on another page, or with another dot active').toEqual([]);
    return ms;
}

test('back from a boulder lands on the boulder list with no hidden, wrong-page or wrong-dot frame', async ({ page, world }) => {
    const wall = world.walls.alpha;
    await openList(page, world, wall);
    await listPage(page).locator('.boulder-card').first().click();
    await expect(page.locator('.boulder-detail h1')).toBeVisible();
    await page.waitForTimeout(500);

    const before = await serverStats(page);
    const backAt = await page.evaluate(() => { window.__bwFrames = []; return performance.now(); });
    await page.goBack();
    await expect(page).toHaveURL(/\?view=boulders$/);
    await expectStraightToList(page, backAt);

    await page.waitForTimeout(1500);
    const delta = statsDelta(before, await serverStats(page));
    console.log(`back-to-list server: ${JSON.stringify(delta)}`);
    test.info().annotations.push({ type: 'back-to-list-server', description: JSON.stringify(delta) });
    // One wall page per visit: no prerender load before the interactive one, and no settings panel re-reading the wall.
    expect(delta.wallGet).toBeLessThanOrEqual(1);
    expect(delta.boulderList).toBeLessThanOrEqual(1);
});

test('back restores the list\'s scroll position', async ({ page, world }) => {
    const wall = world.walls.beta;
    await page.setViewportSize({ width: 390, height: 844 });
    await openList(page, world, wall);
    const list = listPage(page);
    await expect(list.locator('.boulder-card')).toHaveCount(40);
    await list.evaluate(el => { el.scrollTop = 900; });
    await page.waitForTimeout(400);
    const top = await list.evaluate(el => el.scrollTop);
    expect(top).toBeGreaterThan(800);

    // A plain DOM click, as a tap is: Playwright's own click scrolls the target into view first, which in WebKit moves
    // this nested scroller back to the top before the tap.
    await list.locator('.boulder-card').filter({ visible: true }).nth(2).dispatchEvent('click');
    await expect(page.locator('.boulder-detail h1')).toBeVisible();

    const backAt = await page.evaluate(() => { window.__bwFrames = []; return performance.now(); });
    await page.goBack();
    await expectStraightToList(page, backAt);
    await expect.poll(() => listPage(page).evaluate(el => el.scrollTop)).toBeGreaterThan(top - 5);
    expect(await listPage(page).evaluate(el => el.scrollTop)).toBeLessThan(top + 5);
});

test('"Back to boulder list" goes back in history instead of pushing a new entry', async ({ page, world }) => {
    const wall = world.walls.alpha;
    await openList(page, world, wall);
    await listPage(page).locator('.boulder-card').first().click();
    await expect(page.locator('.boulder-detail h1')).toBeVisible();
    const length = await page.evaluate(() => history.length);

    await page.locator('a.back-link').click();
    await expect(page).toHaveURL(/\?view=boulders$/);
    await expect(listPage(page).locator('.boulder-card').first()).toBeInViewport();
    expect(await page.evaluate(() => history.length)).toBe(length);
    // Forward is the boulder again: the link was a step back, not a new page.
    await page.goForward();
    await expect(page.locator('.boulder-detail h1')).toBeVisible();
});

test('swiping to the list puts ?view=boulders in the URL, so back from a boulder returns to the list', async ({ page, world }) => {
    const wall = world.walls.beta;
    await signIn(page, world.users.alice, `/walls/${wall.id}`);
    await page.addInitScript(installSampler);
    await page.goto(`/walls/${wall.id}`);
    await expect(page.locator('.wall-carousel:not(.carousel-pending)')).toBeVisible();
    await page.getByRole('button', { name: 'Page 4', exact: true }).click();
    await expect(page).toHaveURL(/\?view=boulders$/);
    await page.getByRole('button', { name: 'Page 3', exact: true }).click();
    await expect(page).toHaveURL(new RegExp(`/walls/${wall.id}$`));
    await page.getByRole('button', { name: 'Page 4', exact: true }).click();
    await expect(page).toHaveURL(/\?view=boulders$/);
    // Let the smooth scroll come to rest first: a tap lands on a still track.
    await expect.poll(() => page.evaluate(() => {
        const el = document.querySelector('.wall-carousel');
        return Math.abs(el.scrollLeft - 3 * el.clientWidth) <= 1;
    })).toBe(true);

    await listPage(page).locator('.boulder-card').first().dispatchEvent('click');
    await expect(page.locator('.boulder-detail h1')).toBeVisible();
    const backAt = await page.evaluate(() => { window.__bwFrames = []; return performance.now(); });
    await page.goBack();
    await expectStraightToList(page, backAt);

    // The Home tab still means the overview, although the URL now says something the server never rendered.
    await page.evaluate(() => { window.__bwFrames = []; });
    await page.goto(`/walls/${wall.id}`);
    await expect.poll(() => page.evaluate(() => {
        const el = document.querySelector('.wall-carousel:not(.carousel-pending)');
        return el ? Math.round(el.scrollLeft / el.clientWidth) : -1;
    })).toBe(2);
});

test('the settings cards render their panels only once opened', async ({ page, world }) => {
    const wall = world.walls.alpha;
    await signIn(page, world.users.alice, `/walls/${wall.id}`);
    await expect(page.locator('.wall-carousel:not(.carousel-pending)')).toBeVisible();
    const settings = page.locator('.wall-carousel > .wall-page').nth(0);
    const kiosk = settings.locator('details.settings-card', { has: page.locator('summary h2', { hasText: 'Kiosk Registration' }) });
    await expect(kiosk).toHaveCount(1);
    await expect(kiosk.locator('.settings-card-body > *')).toHaveCount(0);

    await page.getByRole('button', { name: 'Page 1', exact: true }).click();
    await kiosk.locator('summary').click();
    await expect(kiosk.locator('.settings-card-body > *').first()).toBeVisible();
    // Still under Activity: the Update wall link is outside every lazy card.
    await expect(settings.locator('details:has(> summary h2:text-is("Activity")) + a.wall-update-link')).toHaveCount(1);
});
