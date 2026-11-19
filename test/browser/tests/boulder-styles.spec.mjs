// Boulder styles: the cards that open the boulder list of a wall once any boulder is tagged (All with its count, one card
// per style in use), the list limited to a style with the style in the URL, tagging from the boulder page, and the same
// cards in the offline client. The Styled wall has eight boulders: tension 2, dynamic 1, funky 1, endurance 1, power 2,
// technical 1 and two untagged; the Beta wall (no tags) must open its list directly.
import { test, expect, signIn } from '../harness/fixtures.mjs';
import { precacheAs, expectClient } from '../harness/client.mjs';

const PHONE = { width: 375, height: 667 };
const listPage = page => page.locator('.wall-carousel > .wall-page').nth(3);
const cards = page => listPage(page).locator('.style-card');
const rows = page => listPage(page).locator('.boulder-card');

async function openBrowser(page, world, query = 'view=boulders') {
    const wall = world.walls.styled;
    await signIn(page, world.users.stella, `/walls/${wall.id}?${query}`);
    await page.goto(`/walls/${wall.id}?${query}`);
    await expect(page.locator('.wall-carousel:not(.carousel-pending)')).toBeVisible();
    return wall;
}

async function expectNoOverflow(page) {
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
}

test('a wall without tags opens the list directly', async ({ page, world }) => {
    await signIn(page, world.users.alice, `/walls/${world.walls.beta.id}?view=boulders`);
    await expect(page.locator('.wall-carousel:not(.carousel-pending)')).toBeVisible();
    await expect(page.locator('.style-cards')).toHaveCount(0);
    await expect(rows(page)).toHaveCount(40);
});

test('phone: seven cards fit one screen with their counts, and a card opens the list for that style', async ({ page, world }) => {
    await page.setViewportSize(PHONE);
    await openBrowser(page, world);
    await expect(cards(page)).toHaveCount(7);
    await expect(listPage(page).locator('[data-style="all"] .style-card-count')).toHaveText('8');
    for (const [style, n] of [['tension', '2'], ['dynamic', '1'], ['funky', '1'], ['endurance', '1'], ['power', '2'], ['technical', '1']]) {
        await expect(listPage(page).locator(`.style-card[data-style="${style}"] .style-card-count`)).toHaveText(n);
    }
    await expectNoOverflow(page);
    const lastBottom = await cards(page).last().evaluate(el => el.getBoundingClientRect().bottom);
    expect(lastBottom).toBeLessThanOrEqual(PHONE.height);
    expect(await listPage(page).evaluate(el => el.scrollHeight <= el.clientHeight + 1)).toBe(true);
    await page.screenshot({ path: '../../.out/styles-cards-375.png' });

    await listPage(page).locator('.style-card[data-style="tension"]').click();
    await expect(page).toHaveURL(/style=tension/);
    await expect(rows(page)).toHaveCount(2);
    await expect(listPage(page).locator('.style-current')).toContainText('Tension');
    await expectNoOverflow(page);
    await page.screenshot({ path: '../../.out/styles-list-375.png' });

    // Reload keeps the style; back returns to the cards; "Styles" goes back too.
    await page.reload();
    await expect(rows(page)).toHaveCount(2);
    await page.goBack();
    await expect(cards(page)).toHaveCount(7);
    await listPage(page).locator('.style-card[data-style="all"]').click();
    await expect(page).toHaveURL(/style=all/);
    await expect(rows(page)).toHaveCount(8);
    await listPage(page).locator('[data-style-back]').click();
    await expect(cards(page)).toHaveCount(7);
});

test('desktop: the same cards and filtering', async ({ page, world }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await openBrowser(page, world);
    await expect(cards(page)).toHaveCount(7);
    await listPage(page).locator('.style-card[data-style="power"]').click();
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

    await page.goto(`/walls/${wall.id}?view=boulders`);
    await expect(listPage(page).locator('.style-card[data-style="funky"] .style-card-count')).toHaveText('2');
});

test('offline client: the cards, the counts and the filtered list at phone width', async ({ page, world, server }) => {
    await page.setViewportSize(PHONE);
    const wall = world.walls.styled;
    expect(await precacheAs(page, server, world.users.stella, `/walls/${wall.id}`)).toBe('complete');
    await server.unreachable();
    await expectClient(page, `/walls/${wall.id}`);
    await expect(page.locator('.style-card')).toHaveCount(7);
    await expect(page.locator('.style-card[data-style="all"] .style-card-count')).toHaveText('8');
    await expect(page.locator('.style-card[data-style="power"] .style-card-count')).toHaveText('2');
    await expectNoOverflow(page);
    await page.screenshot({ path: '../../.out/styles-client-375.png' });

    await page.locator('.style-card[data-style="power"]').click();
    await expect(page).toHaveURL(/style=power/);
    await expect(page.locator('.boulder-card')).toHaveCount(2);
    await page.goBack();
    await expect(page.locator('.style-card')).toHaveCount(7);
});
