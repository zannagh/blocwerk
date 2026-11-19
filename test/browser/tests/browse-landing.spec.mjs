// The boulder browser's landing: All across the grid, By Hold Type and By Style side by side and taller than wide on a
// phone, the viewer's My projects and Favorites cards and their lists, the whole list under a divider on the desktop tier (absent on a
// phone), and no own-list card for a viewer without marks. The Landing wall (seed/SeedLanding.cs) is only listed here:
// 5 boulders, Fav one and Fav two Lena's favourites, the three Plain ones her projects.
import { test, expect, signIn } from '../harness/fixtures.mjs';
import { precacheAs, expectClient } from '../harness/client.mjs';

const PHONE = { width: 402, height: 874 };
const DESKTOP = { width: 1440, height: 900 };
const SHARE = 'browser-landing-share-token';
const listPage = page => page.locator('.wall-carousel > .wall-page').nth(3);
const card = (scope, key) => scope.locator(`.style-card[data-card="${key}"]`);
const rows = scope => scope.locator('.boulder-card');
const box = locator => locator.evaluate(el => el.getBoundingClientRect().toJSON());

async function open(page, world, query, viewport) {
    await page.setViewportSize(viewport);
    const wall = world.walls.landing;
    await signIn(page, world.users.lena, `/walls/${wall.id}?${query}`);
    await page.goto(`/walls/${wall.id}?${query}`);
    await expect(page.locator('.wall-carousel:not(.carousel-pending)')).toBeVisible();
    return wall;
}

async function shoot(page, name) {
    for (const scheme of ['light', 'dark']) {
        await page.emulateMedia({ colorScheme: scheme });
        await page.screenshot({ path: `../../.out/landing-${name}-${scheme}.png` });
    }
    await page.emulateMedia({ colorScheme: 'light' });
}

test('phone: All spans the grid, the pair is portrait, everything fits, and no list under the cards', async ({ page, world }) => {
    await open(page, world, 'view=boulders', PHONE);
    const list = listPage(page);
    const nav = list.locator('nav.style-cards');
    await expect(nav.locator('.style-card')).toHaveCount(5);
    await expect(card(list, 'all').locator('.style-card-count')).toHaveText('5');
    await expect(card(list, 'favorites').locator('.style-card-count')).toHaveText('2');
    await expect(card(list, 'projects').locator('.style-card-count')).toHaveText('3');

    const [grid, all, holds, styles, favorites, projects] = await Promise.all(
        [nav, card(list, 'all'), card(list, 'holds'), card(list, 'styles'), card(list, 'favorites'), card(list, 'projects')].map(box));
    expect(Math.abs(all.width - grid.width)).toBeLessThan(1);
    expect(Math.abs(holds.top - styles.top)).toBeLessThan(1);
    expect(holds.right).toBeLessThanOrEqual(styles.left);
    for (const tall of [holds, styles]) {
        expect(tall.height).toBeGreaterThan(tall.width);
        expect(tall.top).toBeGreaterThanOrEqual(all.bottom);
    }
    expect(favorites.top).toBeGreaterThanOrEqual(holds.bottom);
    expect(Math.abs(favorites.top - projects.top)).toBeLessThan(1);
    expect(favorites.bottom).toBeLessThanOrEqual(PHONE.height);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);

    await expect(list.locator('[data-browse-list="landing"]')).toBeHidden();
    await expect(list.locator('.browse-list-divider')).toBeHidden();
    await shoot(page, 'phone');
});

test('phone: Favorites opens the favourites, a boulder backs into them, reload keeps them, the bar goes to the landing', async ({ page, world }) => {
    await open(page, world, 'view=boulders', PHONE);
    const list = listPage(page);
    await card(list, 'favorites').click();
    await expect(page).toHaveURL(/mine=favorites/);
    await expect(rows(list)).toHaveCount(2);
    await expect(rows(list).filter({ hasText: 'Plain' })).toHaveCount(0);
    await expect(list.locator('[data-browse-current]')).toContainText('Favorites');

    await rows(list).filter({ hasText: 'Fav one' }).click();
    await expect(page).toHaveURL(/\/boulders\/.*mine=favorites/);
    await page.locator('.back-link').click();
    await expect(page).toHaveURL(/mine=favorites/);
    await expect(rows(list)).toHaveCount(2);
    await page.reload();
    await expect(rows(list)).toHaveCount(2);
    await list.locator('[data-browse-back]').click();
    await expect(list.locator('nav.style-cards .style-card')).toHaveCount(5);
});

test('phone: My projects opens the projects, and back returns to the landing', async ({ page, world }) => {
    await open(page, world, 'view=boulders', PHONE);
    const list = listPage(page);
    await card(list, 'projects').click();
    await expect(page).toHaveURL(/mine=projects/);
    await expect(rows(list)).toHaveCount(3);
    await expect(rows(list).filter({ hasText: 'Fav' })).toHaveCount(0);
    await expect(list.locator('[data-browse-current]')).toContainText('My projects');
    await rows(list).filter({ hasText: 'Plain two' }).click();
    await expect(page).toHaveURL(/\/boulders\/.*mine=projects/);
    await page.locator('.back-link').click();
    await expect(page).toHaveURL(/mine=projects/);
    await expect(rows(list)).toHaveCount(3);
    await list.locator('[data-browse-back]').click();
    await expect(page).not.toHaveURL(/mine=/);
});

test('desktop: the cards, a divider, and the whole list with its search under it', async ({ page, world }) => {
    await open(page, world, 'view=boulders', DESKTOP);
    const list = listPage(page);
    const below = list.locator('[data-browse-list="landing"]');
    await expect(list.locator('nav.style-cards .style-card')).toHaveCount(5);
    await expect(list.locator('.browse-list-divider')).toBeVisible();
    await expect(rows(below)).toHaveCount(5);
    await expect(rows(below).first()).toBeVisible();

    const nav = await box(list.locator('nav.style-cards'));
    const divider = await box(list.locator('.browse-list-divider'));
    expect(nav.width).toBeLessThanOrEqual(720);
    expect(divider.top).toBeGreaterThanOrEqual(nav.bottom);
    expect((await box(rows(below).first())).top).toBeGreaterThan(divider.bottom);
    await shoot(page, 'desktop');

    await below.locator('[data-bw-search-toggle]').click();
    await below.locator('.list-search-input').fill('Fav');
    await expect(rows(below)).toHaveCount(2);
});

test('signed out (share link): no My projects or Favorites card, and their URLs list nothing', async ({ page, world }) => {
    await page.setViewportSize(PHONE);
    const wall = world.walls.landing;
    await page.goto(`/walls/${wall.id}/shared/${SHARE}?view=boulders`);
    await expect(page.locator('.wall-carousel:not(.carousel-pending)')).toBeVisible();
    const list = listPage(page);
    await expect(list.locator('nav.style-cards .style-card')).toHaveCount(3);
    await expect(card(list, 'favorites')).toHaveCount(0);
    await expect(card(list, 'projects')).toHaveCount(0);
    await expect(card(list, 'all').locator('.style-card-count')).toHaveText('5');

    await page.goto(`/walls/${wall.id}/shared/${SHARE}?view=boulders&mine=favorites`);
    await expect(list.locator('.empty-boulders')).toBeVisible();
    await expect(rows(list)).toHaveCount(0);
    await page.goto(`/walls/${wall.id}/shared/${SHARE}?view=boulders&mine=projects`);
    await expect(list.locator('.empty-boulders')).toBeVisible();
});

test('offline client: the same landing with My projects and Favorites, and their lists', async ({ page, world, server }) => {
    await page.setViewportSize(PHONE);
    const wall = world.walls.landing;
    expect(await precacheAs(page, server, world.users.lena, `/walls/${wall.id}`)).toBe('complete');
    await server.unreachable();
    await expectClient(page, `/walls/${wall.id}`);
    const nav = page.locator('nav.style-cards');
    await expect(nav.locator('.style-card')).toHaveCount(5);
    await expect(card(page, 'favorites').locator('.style-card-count')).toHaveText('2');
    await expect(card(page, 'projects').locator('.style-card-count')).toHaveText('3');
    const [holds, styles] = await Promise.all([box(card(page, 'holds')), box(card(page, 'styles'))]);
    expect(holds.height).toBeGreaterThan(holds.width);
    expect(Math.abs(holds.top - styles.top)).toBeLessThan(1);
    await expect(page.locator('[data-browse-list="landing"]')).toBeHidden();
    await shoot(page, 'client-phone');

    await card(page, 'favorites').click();
    await expect(page).toHaveURL(/mine=favorites/);
    await expect(rows(page)).toHaveCount(2);
    await page.locator('[data-browse-back]').click();
    await card(page, 'projects').click();
    await expect(page).toHaveURL(/mine=projects/);
    await expect(rows(page)).toHaveCount(3);
});
