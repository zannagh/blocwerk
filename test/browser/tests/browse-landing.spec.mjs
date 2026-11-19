// The boulder browser's landing: All across the grid, By Hold Type and By Style side by side and taller than wide on a
// phone, the viewer's My projects and Favorites cards and their lists, the whole list under a divider rendered on the
// desktop tier only, and no own-list card or ?mine= level on a share link or a kiosk, signed in or not. The Landing wall
// (seed/SeedLanding.cs) is only listed here: 5 boulders, Fav one and Fav two Lena's favourites, the Plain ones her projects.
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
    // The cookie notice floats over the bottom of the screen; accepted, the screenshot shows what a returning user sees.
    const ok = page.locator('.bw-consent-accept');
    if (await ok.isVisible().catch(() => false)) {
        await ok.click();
    }
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

    await expect(list.locator('[data-browse-list="landing"]')).toHaveCount(0);
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

for (const viewport of [DESKTOP, { width: 1280, height: 800 }]) {
    test(`desktop ${viewport.width}x${viewport.height}: compact cards, a divider, and the whole list with its search under it`, async ({ page, world }) => {
        await open(page, world, 'view=boulders', viewport);
        const list = listPage(page);
        const below = list.locator('[data-browse-list="landing"]');
        await expect(list.locator('nav.style-cards .style-card')).toHaveCount(5);
        await expect(list.locator('.browse-list-divider')).toBeVisible();
        await expect(rows(below)).toHaveCount(5);

        // One banner and one row of landscape cards, so the list starts in the top half of the screen.
        const [nav, all, holds, styles, projects, favorites] = await Promise.all([list.locator('nav.style-cards'),
            card(list, 'all'), card(list, 'holds'), card(list, 'styles'), card(list, 'projects'), card(list, 'favorites')].map(box));
        expect(nav.width).toBeLessThanOrEqual(960);
        for (const small of [holds, styles, projects, favorites]) {
            expect(Math.abs(small.top - holds.top)).toBeLessThan(1);
            expect(small.width).toBeGreaterThan(small.height);
        }
        expect(all.height).toBeLessThan(100);
        const divider = await box(list.locator('.browse-list-divider'));
        expect(divider.top).toBeGreaterThanOrEqual(nav.bottom);
        expect(divider.top).toBeLessThan(viewport.height * 0.45);
        const firstRow = await box(rows(below).first());
        expect(firstRow.top).toBeGreaterThan(divider.bottom);
        await expect(rows(below).nth(3)).toBeInViewport();
        await shoot(page, `desktop-${viewport.width}`);

        await below.locator('[data-bw-search-toggle]').click();
        await below.locator('.list-search-input').fill('Fav');
        await expect(rows(below)).toHaveCount(2);
    });
}

// The landing on a share link or a kiosk: the base cards, no own-list card, and a ?mine= URL opens the landing.
async function expectNoOwnLists(page, url, cards) {
    const list = listPage(page);
    for (const mine of ['', '&mine=favorites', '&mine=projects']) {
        await page.goto(url + mine);
        await expect(page.locator('.wall-carousel:not(.carousel-pending)')).toBeVisible();
        await expect(list.locator('nav.style-cards .style-card')).toHaveCount(cards);
        await expect(card(list, 'favorites')).toHaveCount(0);
        await expect(card(list, 'projects')).toHaveCount(0);
        await expect(list.locator('[data-browse-current]')).toHaveCount(0);
    }
}

test('signed out (share link): no own-list cards, and a ?mine= URL opens the landing', async ({ page, world }) => {
    await page.setViewportSize(PHONE);
    const wall = world.walls.landing;
    await expectNoOwnLists(page, `/walls/${wall.id}/shared/${SHARE}?view=boulders`, 3);
    await expect(card(listPage(page), 'all').locator('.style-card-count')).toHaveText('5');
});

test('signed in on a share link: still no own-list cards, and ?mine= is ignored', async ({ page, world }) => {
    const wall = await open(page, world, 'view=boulders', PHONE);
    await expect(card(listPage(page), 'favorites')).toHaveCount(1);
    await expectNoOwnLists(page, `/walls/${wall.id}/shared/${SHARE}?view=boulders`, 3);
});

test('kiosk acting as a member with favourites: no own-list cards, and ?mine= is ignored', async ({ page, world }) => {
    await page.setViewportSize(PHONE);
    await page.goto('/oauth-select');
    await page.getByText('Register this device as a wall kiosk').click();
    await page.getByLabel('Kiosk key').fill(world.kioskToken);
    await page.getByRole('button', { name: 'Register kiosk' }).click();
    await page.waitForURL(/\/walls\/[0-9a-f-]{36}/);
    // Act as Carol, who favourited a boulder on Alpha (the act-as form POST, as in kiosk-readonly-social).
    await page.goto(`/kiosk/users/${world.users.carol.id}`);
    const form = page.locator('form[action="/kiosk/act-as"]').first();
    await expect(form).toBeAttached();
    const antiforgery = await form.locator('input[name="__RequestVerificationToken"]').inputValue();
    const response = await page.request.post('/kiosk/act-as', {
        form: { userId: world.users.carol.id, pin: '', __RequestVerificationToken: antiforgery },
    });
    expect(response.ok()).toBe(true);
    await page.goto(`/walls/${world.walls.alpha.id}`);
    await expect(page.locator('body')).toHaveAttribute('data-bw-user', world.users.carol.id);

    await expectNoOwnLists(page, `/walls/${world.walls.alpha.id}?view=boulders`, 3);
    // Nor the list under the cards: a kiosk never renders it, whatever its width.
    await page.setViewportSize(DESKTOP);
    await page.reload();
    await expect(page.locator('.wall-carousel:not(.carousel-pending)')).toBeVisible();
    await expect(listPage(page).locator('[data-browse-list="landing"]')).toHaveCount(0);
});

test('phone: no list is rendered under the cards, and widening the window to desktop renders it', async ({ page, world }) => {
    await open(page, world, 'view=boulders', PHONE);
    const list = listPage(page);
    await expect(list.locator('nav.style-cards .style-card')).toHaveCount(5);
    await expect(list.locator('[data-browse-list="landing"]')).toHaveCount(0);
    await expect(rows(list)).toHaveCount(0);
    await page.setViewportSize(DESKTOP);
    await expect(rows(list.locator('[data-browse-list="landing"]'))).toHaveCount(5);
    await page.setViewportSize(PHONE);
    await expect(list.locator('[data-browse-list="landing"]')).toHaveCount(0);
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
    await expect(page.locator('[data-browse-list]')).toHaveCount(0);
    await shoot(page, 'client-phone');

    await card(page, 'favorites').click();
    await expect(page).toHaveURL(/mine=favorites/);
    await expect(rows(page)).toHaveCount(2);
    await page.locator('[data-browse-back]').click();
    await card(page, 'projects').click();
    await expect(page).toHaveURL(/mine=projects/);
    await expect(rows(page)).toHaveCount(3);
});
