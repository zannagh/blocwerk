// Boulder styles: the By Style cards of the boulder browser (one per style, greyed out when no boulder carries it), the list
// limited to a style with the style in the URL, tagging from the boulder page, and the same cards in the offline client.
// The Styled wall has eight boulders: tension 2, dynamic 1, funky 1, endurance 1, power 2, technical 1, warmup, reachy and
// flexy none, and two untagged; the Beta wall (no tags) still opens on the landing, its All card leading to the list.
import { test, expect, signIn } from '../harness/fixtures.mjs';
import { precacheAs, expectClient } from '../harness/client.mjs';

const PHONE = { width: 402, height: 874 }; // iPhone 16 Pro
const listPage = page => page.locator('.wall-carousel > .wall-page').nth(3);
const cards = page => listPage(page).locator('.style-card');
const rows = page => listPage(page).locator('.boulder-card');

async function openBrowser(page, world, query = 'view=boulders&browse=styles') {
    const wall = world.walls.styled;
    await signIn(page, world.users.stella, `/walls/${wall.id}?${query}`);
    await page.goto(`/walls/${wall.id}?${query}`);
    await expect(page.locator('.wall-carousel:not(.carousel-pending)')).toBeVisible();
    return wall;
}

async function expectNoOverflow(page) {
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
}

test('a wall without tags opens on the landing, its By Style card greyed out, All leading to the list', async ({ page, world }) => {
    await signIn(page, world.users.alice, `/walls/${world.walls.beta.id}?view=boulders`);
    await expect(page.locator('.wall-carousel:not(.carousel-pending)')).toBeVisible();
    await expect(cards(page)).toHaveCount(5);
    await expect(listPage(page).locator('.style-card[data-card="styles"]')).toHaveClass(/is-empty/);
    await listPage(page).locator('.style-card[data-card="all"]').click();
    await expect(page).toHaveURL(/style=all/);
    await expect(rows(page)).toHaveCount(40);
});

test('phone: nine cards fit one screen with their counts, and a card opens the list for that style', async ({ page, world }) => {
    await page.setViewportSize(PHONE);
    await openBrowser(page, world);
    await expect(cards(page)).toHaveCount(9);
    for (const empty of ['warmup', 'reachy', 'flexy']) {
        await expect(listPage(page).locator(`.style-card[data-card="${empty}"]`)).toHaveClass(/is-empty/);
    }
    for (const [style, n] of [['tension', '2'], ['dynamic', '1'], ['funky', '1'], ['endurance', '1'], ['power', '2'], ['technical', '1']]) {
        await expect(listPage(page).locator(`.style-card[data-card="${style}"] .style-card-count`)).toHaveText(n);
    }
    await expectNoOverflow(page);
    const lastBottom = await cards(page).last().evaluate(el => el.getBoundingClientRect().bottom);
    expect(lastBottom).toBeLessThanOrEqual(PHONE.height);
    expect(await listPage(page).evaluate(el => el.scrollHeight <= el.clientHeight + 1)).toBe(true);
    await page.screenshot({ path: '../../.out/styles-cards-402.png' });

    await listPage(page).locator('.style-card[data-card="tension"]').click();
    await expect(page).toHaveURL(/style=tension/);
    await expect(rows(page)).toHaveCount(2);
    await expect(listPage(page).locator('.style-current')).toContainText('Tension');
    await expectNoOverflow(page);
    await page.screenshot({ path: '../../.out/styles-list-402.png' });

    // A boulder opened from this list backs into this list (the style kept, the list as it was), by its back link and by
    // the browser's back button.
    await rows(page).first().click();
    await expect(page).toHaveURL(/\/boulders\/.*style=tension/);
    await page.locator('.back-link').click();
    await expect(page).toHaveURL(/style=tension/);
    await expect(rows(page)).toHaveCount(2);
    await rows(page).first().click();
    await page.goBack();
    await expect(rows(page)).toHaveCount(2);

    // Reload keeps the style; back returns to the cards; the bar goes up to the landing, whose All card lists every boulder.
    await page.reload();
    await expect(rows(page)).toHaveCount(2);
    await page.goBack();
    await expect(cards(page)).toHaveCount(9);
    await listPage(page).locator('[data-browse-back]').click();
    await expect(cards(page)).toHaveCount(5);
    await expect(listPage(page).locator('.style-card[data-card="all"] .style-card-count')).toHaveText('8');
    await listPage(page).locator('.style-card[data-card="all"]').click();
    await expect(page).toHaveURL(/style=all/);
    await expect(rows(page)).toHaveCount(8);
    await listPage(page).locator('[data-browse-back]').click();
    await expect(cards(page)).toHaveCount(5);
});

test('desktop: the same cards and filtering', async ({ page, world }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await openBrowser(page, world);
    await expect(cards(page)).toHaveCount(9);
    await listPage(page).locator('.style-card[data-card="power"]').click();
    await expect(rows(page)).toHaveCount(2);
});

test('tagging from the boulder page saves at once and the style shows up in the cards', async ({ page, world }) => {
    const wall = await openBrowser(page, world, 'view=boulders&style=all');
    await rows(page).filter({ hasText: 'Styled boulder 7' }).click();
    await expect(page).toHaveURL(/\/boulders\//);
    const funky = page.locator('.boulder-styles .style-pick[data-style="funky"]');
    await funky.click();
    await expect(funky).toHaveAttribute('aria-pressed', 'true');
    await page.reload();
    await expect(page.locator('.boulder-styles .style-pick[data-style="funky"]')).toHaveAttribute('aria-pressed', 'true');

    await page.goto(`/walls/${wall.id}?view=boulders&browse=styles`);
    await expect(listPage(page).locator('.style-card[data-card="funky"] .style-card-count')).toHaveText('2');

    // The database is shared by the browser projects: put the boulder back as seeded, so the next project counts the same.
    await page.goto(`/walls/${wall.id}?view=boulders&style=all`);
    await rows(page).filter({ hasText: 'Styled boulder 7' }).click();
    await page.locator('.boulder-styles .style-pick[data-style="funky"]').click();
    await expect(page.locator('.boulder-styles .style-pick[data-style="funky"]')).toHaveAttribute('aria-pressed', 'false');
    await page.reload();
    await expect(page.locator('.boulder-styles .style-pick[data-style="funky"]')).toHaveAttribute('aria-pressed', 'false');
});

test('offline client: the cards, the counts and the filtered list at phone width', async ({ page, world, server }) => {
    await page.setViewportSize(PHONE);
    const wall = world.walls.styled;
    expect(await precacheAs(page, server, world.users.stella, `/walls/${wall.id}`)).toBe('complete');
    await server.unreachable();
    await expectClient(page, `/walls/${wall.id}`);
    await expect(page.locator('.style-card')).toHaveCount(5);
    await expect(page.locator('.style-card[data-card="all"] .style-card-count')).toHaveText('8');
    await page.locator('.style-card[data-card="styles"]').click();
    await expect(page).toHaveURL(/browse=styles/);
    await expect(page.locator('.style-card')).toHaveCount(9);
    // The cards are the screen: all nine sit inside the viewport without scrolling.
    const box = async n => page.locator('.style-card').nth(n).evaluate(el => { const r = el.getBoundingClientRect(); return { top: r.top, bottom: r.bottom }; });
    expect((await box(0)).top).toBeGreaterThanOrEqual(0);
    expect((await box(8)).bottom).toBeLessThanOrEqual(PHONE.height);
    await expect(page.locator('.wall-photo-container')).toHaveCount(0);
    await expect(page.locator('.style-card[data-card="power"] .style-card-count')).toHaveText('2');
    await expectNoOverflow(page);
    await page.screenshot({ path: '../../.out/styles-client-402.png' });

    await page.locator('.style-card[data-card="power"]').click();
    await expect(page).toHaveURL(/style=power/);
    await expect(page.locator('.boulder-card')).toHaveCount(2);
    await expect(page.locator('[data-browse-back]')).toBeVisible();
    await page.screenshot({ path: '../../.out/styles-client-list-402.png' });

    // Badges on the boulder page: "Styled boulder 1" is tension + power.
    await page.locator('.boulder-card', { hasText: 'Styled boulder 1' }).click();
    await expect(page.locator('.style-badge')).toHaveCount(2);
    await page.screenshot({ path: '../../.out/styles-client-badges-402.png' });
    // Back from the boulder lands on the same style list, not on the cards.
    await page.locator('.back-link').click();
    await expect(page).toHaveURL(/style=power/);
    await expect(page.locator('.boulder-card')).toHaveCount(2);
    await page.locator('[data-browse-back]').click();
    await expect(page.locator('.style-card')).toHaveCount(9);
});

test('dark mode at phone width: cards, list header and the tagging chips', async ({ page, world }) => {
    await page.setViewportSize(PHONE);
    await page.emulateMedia({ colorScheme: 'dark' });
    const wall = await openBrowser(page, world);
    await expect(cards(page)).toHaveCount(9);
    await page.screenshot({ path: '../../.out/styles-cards-dark-402.png' });
    await listPage(page).locator('.style-card[data-card="power"]').click();
    await expect(rows(page)).toHaveCount(2);
    await page.screenshot({ path: '../../.out/styles-list-dark-402.png' });
    await rows(page).first().click();
    await expect(page.locator('.boulder-styles .style-pick[data-style]')).toHaveCount(9);
    await page.screenshot({ path: '../../.out/styles-boulder-dark-402.png' });
    expect(wall.id).toBeTruthy();
});
