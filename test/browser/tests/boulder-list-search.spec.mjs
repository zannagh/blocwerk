// The boulder list's search chip (a free-text field that filters as you type, closed by Escape or its close button),
// its filters remembered per wall in the blocwerk-boulder-filters cookie across a reload, and the carousel keeping
// every page but the current one inert, so the browser's find-in-page only searches what is on screen.
import { test, expect, signIn } from '../harness/fixtures.mjs';

const LIST_PAGE = 3;
const listPage = page => page.locator('.wall-carousel > .wall-page').nth(LIST_PAGE);
const cards = page => listPage(page).locator('.boulder-card');

async function openList(page, world) {
    const wall = world.walls.beta;
    await signIn(page, world.users.alice, `/walls/${wall.id}?view=boulders`);
    await page.goto(`/walls/${wall.id}?view=boulders`);
    await expect(page.locator('.wall-carousel:not(.carousel-pending)')).toBeVisible();
    await expect(cards(page)).toHaveCount(40);
    return wall;
}

test('the search chip opens a field that filters the list as you type, and closing it restores the list', async ({ page, world }) => {
    await openList(page, world);
    const chip = listPage(page).getByRole('button', { name: 'Search boulders' });
    const field = listPage(page).getByRole('searchbox', { name: 'Search boulders' });
    await expect(field).toBeHidden();

    await chip.click();
    await expect(field).toBeVisible();
    await expect(field).toBeFocused();
    await expect(chip).toHaveAttribute('aria-expanded', 'true');

    // Case-insensitive, every word anywhere in the row.
    await field.pressSequentially('LIST BOULDER 07');
    await expect(cards(page)).toHaveCount(1);
    await expect(cards(page).first()).toContainText('List boulder 07');
    // Words match separately: "4" is in 04, 14, 24, 34 and 40 (the seed numbers them 01 to 40).
    await field.fill('boulder 4');
    await expect(cards(page)).toHaveCount(5);
    await field.fill('no such boulder');
    await expect(listPage(page).locator('.empty-boulders')).toContainText('No boulders match your search.');

    // Clearing the field restores the list.
    await field.fill('');
    await expect(cards(page)).toHaveCount(40);

    // Escape closes it, clears it and hands focus back to the chip.
    await field.fill('boulder 07');
    await expect(cards(page)).toHaveCount(1);
    await field.press('Escape');
    await expect(field).toBeHidden();
    await expect(cards(page)).toHaveCount(40);
    await expect(chip).toBeFocused();
    await expect(chip).toHaveAttribute('aria-expanded', 'false');

    // So does the close button.
    await chip.click();
    await expect(field).toHaveValue('');
    await field.fill('boulder 4');
    await expect(cards(page)).toHaveCount(5);
    await listPage(page).getByRole('button', { name: 'Close search' }).click();
    await expect(field).toBeHidden();
    await expect(cards(page)).toHaveCount(40);
});

test('the list\'s filters and sort survive a reload, through a year-long Lax cookie; the search does not', async ({ page, world, context }) => {
    const wall = await openList(page, world);
    await expect(cards(page).first()).toContainText('List boulder 40');

    await listPage(page).getByRole('button', { name: /Filters/ }).click();
    await listPage(page).locator('.filter-select').selectOption({ label: 'Oldest' });
    await expect(cards(page).first()).toContainText('List boulder 01');
    await listPage(page).getByLabel('Has beta video').check();
    await expect(listPage(page).locator('.empty-boulders')).toContainText('No boulders match your filters.');
    await listPage(page).getByLabel('Has beta video').uncheck();
    await listPage(page).getByLabel('Not done by me').check();
    await expect(cards(page)).toHaveCount(40);

    await listPage(page).getByRole('button', { name: 'Search boulders' }).click();
    await listPage(page).getByRole('searchbox').fill('boulder 07');
    await expect(cards(page)).toHaveCount(1);

    await expect.poll(async () => (await context.cookies()).find(c => c.name === 'blocwerk-boulder-filters')?.value || '')
        .toContain(wall.id.replace(/-/g, ''));
    const cookie = (await context.cookies()).find(c => c.name === 'blocwerk-boulder-filters');
    expect(cookie.sameSite).toBe('Lax');
    expect(cookie.path).toBe('/');
    expect(cookie.expires - Date.now() / 1000).toBeGreaterThan(300 * 24 * 3600);
    expect(decodeURIComponent(cookie.value)).not.toContain('07');

    // Every painted frame after the reload: the first card the list shows, whenever it shows one.
    await page.addInitScript(() => {
        window.__bwFirstCards = [];
        const tick = () => {
            const card = document.querySelector('.wall-carousel > .wall-page:nth-child(4) .boulder-card');
            if (card) {
                window.__bwFirstCards.push(card.textContent.trim().split('\n')[0]);
            }
            requestAnimationFrame(tick);
        };
        requestAnimationFrame(tick);
    });
    await page.reload();
    await expect(page.locator('.wall-carousel:not(.carousel-pending)')).toBeVisible();
    // The first list on screen is already sorted and filtered, and the search is gone.
    await expect(cards(page)).toHaveCount(40);
    await expect(cards(page).first()).toContainText('List boulder 01');
    await expect(listPage(page).locator('.filter-count')).toHaveText('1');
    await expect(listPage(page).getByRole('searchbox')).toBeHidden();
    // No flash of the unfiltered, newest-first list before the stored sort applied.
    const firstCards = await page.evaluate(() => [...new Set(window.__bwFirstCards)]);
    expect(firstCards).toEqual(['List boulder 01']);
    await listPage(page).getByRole('button', { name: /Filters/ }).click();
    await expect(listPage(page).getByLabel('Not done by me')).toBeChecked();
    await expect(listPage(page).locator('.filter-select')).toHaveValue('Oldest');

    // Back to the defaults: the wall drops out of the cookie.
    await listPage(page).getByLabel('Not done by me').uncheck();
    await listPage(page).locator('.filter-select').selectOption({ label: 'Newest' });
    await expect.poll(async () => (await context.cookies()).some(c => c.name === 'blocwerk-boulder-filters')).toBe(false);
});

test('only the page on screen is searchable: every other carousel page is inert', async ({ page, world }) => {
    await openList(page, world);
    const inert = () => page.locator('.wall-carousel > .wall-page').evaluateAll(pages => pages.map(p => p.inert));
    await expect.poll(inert).toEqual([true, true, true, false, true]);

    await page.locator('.carousel-dots .dot').nth(1).click();
    await expect.poll(inert).toEqual([true, false, true, true, true]);
    // The list's neighbour is still painted for a swipe; only find-in-page, focus and taps skip it.
    await expect(listPage(page)).toBeAttached();
    await expect(cards(page).first()).not.toBeInViewport();
});
