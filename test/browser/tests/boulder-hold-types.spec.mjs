// The boulder browser by hold type: the landing (All, By Hold Type, By Style), the hold-type cards with greyed-out empty
// ones, the list of one type with its families, the boulder page's type (inferred "from holds", or set) and back to the
// same list, setting and clearing the type on the boulder page, and the same browsing in the offline client. The Grips
// wall (seed/SeedHoldTypes.cs) is only listed here: 6 rows (the variant folds into its family), Jug 1, Crimp 2 (Crimpy and the family), Pinch, Sloper and
// Pocket empty, one Warmup boulder.
import { test, expect, signIn } from '../harness/fixtures.mjs';
import { precacheAs, expectClient } from '../harness/client.mjs';

const PHONE = { width: 390, height: 844 };
const listPage = page => page.locator('.wall-carousel > .wall-page').nth(3);
const card = (scope, key) => scope.locator(`.style-card[data-card="${key}"]`);
const rows = scope => scope.locator('.boulder-card');

async function open(page, world, query) {
    const wall = world.walls.grips;
    await signIn(page, world.users.gina, `/walls/${wall.id}?${query}`);
    await page.goto(`/walls/${wall.id}?${query}`);
    await expect(page.locator('.wall-carousel:not(.carousel-pending)')).toBeVisible();
    return wall;
}

async function expectFits(page, scope) {
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
    const bottom = await scope.locator('.style-card').last().evaluate(el => el.getBoundingClientRect().bottom);
    expect(bottom).toBeLessThanOrEqual(PHONE.height);
}

test('phone: landing, hold-type cards, a type\'s list, its boulder and back again', async ({ page, world }) => {
    await page.setViewportSize(PHONE);
    await open(page, world, 'view=boulders');
    const list = listPage(page);
    await expect(list.locator('.style-card')).toHaveCount(3);
    await expect(card(list, 'all').locator('.style-card-count')).toHaveText('6');
    await expect(card(list, 'holds').locator('.style-card-count')).toHaveText('3');
    await expect(card(list, 'styles').locator('.style-card-count')).toHaveText('1');
    await expectFits(page, list);
    await page.screenshot({ path: '../../.out/holds-landing-390.png' });

    await card(list, 'holds').click();
    await expect(page).toHaveURL(/browse=holds/);
    await expect(list.locator('.style-card')).toHaveCount(5);
    await expect(card(list, 'jug').locator('.style-card-count')).toHaveText('1');
    await expect(card(list, 'crimp').locator('.style-card-count')).toHaveText('2');
    for (const empty of ['pinch', 'sloper', 'pocket']) {
        await expect(card(list, empty)).toHaveClass(/is-empty/);
        await expect(card(list, empty)).toHaveAttribute('aria-disabled', 'true');
    }
    // An empty card is no link: tapping it stays on the cards.
    await card(list, 'pinch').click();
    await expect(page).toHaveURL(/browse=holds/);
    await expectFits(page, list);
    await page.screenshot({ path: '../../.out/holds-cards-390.png' });

    await card(list, 'crimp').click();
    await expect(page).toHaveURL(/hold=crimp/);
    await expect(rows(list)).toHaveCount(2);
    await expect(rows(list).filter({ hasText: 'Family base' })).toHaveCount(1);
    await expect(list.locator('[data-browse-current]')).toContainText('Crimp');

    await rows(list).filter({ hasText: 'Crimpy' }).click();
    await expect(page).toHaveURL(/\/boulders\/.*hold=crimp/);
    await expect(page.locator('[data-hold-type-panel]')).toContainText('Crimp · from holds');
    await expect(page.locator('[data-hold-type-panel] .style-pick[data-hold-type="crimp"]')).toHaveClass(/inferred/);
    await page.screenshot({ path: '../../.out/holds-boulder-390.png' });
    await page.locator('.back-link').click();
    await expect(page).toHaveURL(/hold=crimp/);
    await expect(rows(list)).toHaveCount(2);

    // The browser's back button and a reload keep the list; the bar goes up a level at a time.
    await rows(list).first().click();
    await expect(page).toHaveURL(/\/boulders\//);
    await page.goBack();
    await expect(rows(list)).toHaveCount(2);
    await page.reload();
    await expect(rows(list)).toHaveCount(2);
    await list.locator('[data-browse-back]').click();
    await expect(page).toHaveURL(/browse=holds/);
    await expect(list.locator('.style-card')).toHaveCount(5);
    await list.locator('[data-browse-back]').click();
    await expect(list.locator('.style-card')).toHaveCount(3);
});

test('the boulder page sets a hold type and clears it again', async ({ page, world }) => {
    const wall = await open(page, world, 'view=boulders&style=all');
    await rows(listPage(page)).filter({ hasText: 'Override me' }).click();
    const sloper = page.locator('[data-hold-type-panel] .style-pick[data-hold-type="sloper"]');
    await expect(page.locator('[data-hold-type-panel]')).toContainText('None yet');
    await sloper.click();
    await expect(sloper).toHaveAttribute('aria-pressed', 'true');
    await page.reload();
    await expect(sloper).toHaveAttribute('aria-pressed', 'true');

    await page.goto(`/walls/${wall.id}?view=boulders&browse=holds`);
    await expect(card(listPage(page), 'sloper').locator('.style-card-count')).toHaveText('1');
    await card(listPage(page), 'sloper').click();
    await rows(listPage(page)).filter({ hasText: 'Override me' }).click();

    // Cleared, the holds decide again (this boulder has none), and the wall is as seeded for the next browser.
    await sloper.click();
    await expect(sloper).toHaveAttribute('aria-pressed', 'false');
    await page.reload();
    await expect(sloper).toHaveAttribute('aria-pressed', 'false');
    await page.goto(`/walls/${wall.id}?view=boulders&browse=holds`);
    await expect(card(listPage(page), 'sloper')).toHaveClass(/is-empty/);
});

test('offline client: the same levels, cards and list at phone width', async ({ page, world, server }) => {
    await page.setViewportSize(PHONE);
    const wall = world.walls.grips;
    expect(await precacheAs(page, server, world.users.gina, `/walls/${wall.id}`)).toBe('complete');
    await server.unreachable();
    await expectClient(page, `/walls/${wall.id}`);
    await expect(page.locator('.style-card')).toHaveCount(3);
    await expect(card(page, 'holds').locator('.style-card-count')).toHaveText('3');
    await expectFits(page, page);
    await page.screenshot({ path: '../../.out/holds-client-landing-390.png' });

    await card(page, 'holds').click();
    await expect(page).toHaveURL(/browse=holds/);
    await expect(page.locator('.style-card')).toHaveCount(5);
    await expect(card(page, 'pinch')).toHaveAttribute('aria-disabled', 'true');
    await expectFits(page, page);
    await card(page, 'crimp').click();
    await expect(page).toHaveURL(/hold=crimp/);
    await expect(rows(page)).toHaveCount(2);

    await rows(page).filter({ hasText: 'Crimpy' }).click();
    await expect(page.locator('.hold-type-badge')).toContainText('from holds');
    await page.screenshot({ path: '../../.out/holds-client-boulder-390.png' });
    await page.locator('.back-link').click();
    await expect(page).toHaveURL(/hold=crimp/);
    await expect(rows(page)).toHaveCount(2);
    await page.locator('[data-browse-back]').click();
    await expect(page.locator('.style-card')).toHaveCount(5);
    await page.locator('[data-browse-back]').click();
    await expect(page.locator('.style-card')).toHaveCount(3);
});
