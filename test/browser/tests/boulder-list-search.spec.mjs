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
    // Every entry is exactly user,wall,flags,sort,gradeMin,gradeMax: there is no field the search could ride in. This
    // wall's entry is "Not done by me" (4) sorted Oldest (1), with no grades.
    const entries = decodeURIComponent(cookie.value).split('|').map(e => e.split(','));
    for (const entry of entries) {
        expect(entry).toHaveLength(6);
        expect(entry[0]).toMatch(/^[0-9a-f]{8}$/);
        expect(entry[1]).toMatch(/^[0-9a-f]{32}$/);
    }
    const mine = entries.find(e => e[1] === wall.id.replace(/-/g, ''));
    expect(mine.slice(2)).toEqual(['4', '1', '', '']);
    expect(decodeURIComponent(cookie.value)).not.toContain('boulder');

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

// iOS has no scrollend, so the settle that re-marks the pages is an idle timer every touch restarts. The page a swipe
// lands on has to be usable before it: here the settle is switched off altogether, as if the user kept touching.
test('the page a swipe lands on scrolls and takes taps before the carousel settles', async ({ page, world }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    const wall = world.walls.beta;
    await signIn(page, world.users.alice, `/walls/${wall.id}`);
    await page.goto(`/walls/${wall.id}`);
    await expect(page.locator('.wall-carousel:not(.carousel-pending)')).toBeVisible();
    await expect.poll(() => listPage(page).evaluate(p => p.inert)).toBe(true);
    await expect(cards(page)).toHaveCount(40);

    await page.locator('.wall-carousel').evaluate(el => {
        const state = el._bwCarousel;
        el.removeEventListener('scrollend', state.onScrollEnd);
        state.settle = () => { };
        el.scrollTo({ left: el.children[3].offsetLeft, behavior: 'instant' });
    });
    await expect.poll(() => listPage(page).evaluate(p => p.inert)).toBe(false);

    const box = await listPage(page).boundingBox();
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    await page.mouse.wheel(0, 600);
    await expect.poll(() => listPage(page).evaluate(p => p.scrollTop)).toBeGreaterThan(100);
    await cards(page).filter({ visible: true }).nth(1).click();
    await expect(page.locator('.boulder-detail h1')).toBeVisible();
});

test('focus on a page that is swiped away moves to the page on screen, in every engine', async ({ page, world }) => {
    await openList(page, world);
    await listPage(page).getByRole('button', { name: 'Search boulders' }).click();
    const field = listPage(page).getByRole('searchbox');
    await expect(field).toBeFocused();

    await page.locator('.wall-carousel').evaluate(el => el.scrollTo({ left: el.children[2].offsetLeft, behavior: 'instant' }));
    await expect.poll(() => page.evaluate(() => {
        const pages = [...document.querySelectorAll('.wall-carousel > .wall-page')];
        return pages.indexOf(document.activeElement);
    })).toBe(2);
    await expect(field).not.toBeFocused();
});

test('the filters cookie keeps what another tab stored, replaces a malformed value, and leaves with sign-out', async ({ page, world, context, baseURL }) => {
    const host = new URL(baseURL).hostname;
    const read = async () => (await context.cookies()).find(c => c.name === 'blocwerk-boulder-filters')?.value;
    const decoded = async () => { try { return decodeURIComponent((await read()) || ''); } catch { return ''; } };
    const foreign = '0badc0de,' + '9'.repeat(32) + ',2,0,,';

    // Malformed: the list opens unfiltered, and the first change overwrites it.
    await context.addCookies([{ name: 'blocwerk-boulder-filters', value: '%E0%A4%A', domain: host, path: '/', sameSite: 'Lax' }]);
    await openList(page, world);
    await listPage(page).getByRole('button', { name: /Filters/ }).click();
    await listPage(page).getByLabel('Not done by me').check();
    await expect.poll(decoded).toContain(world.walls.beta.id.replace(/-/g, ''));
    expect(await read()).not.toContain('%E0%A4%A');

    // Another tab stored a wall since this page loaded: the next save merges into it.
    await context.addCookies([{ name: 'blocwerk-boulder-filters', value: encodeURIComponent(foreign), domain: host, path: '/', sameSite: 'Lax' }]);
    await listPage(page).locator('.filter-select').selectOption({ label: 'Oldest' });
    await expect.poll(decoded).toContain(',4,1,');
    expect(decodeURIComponent(await read())).toContain(foreign);

    await page.goto('/account/logout');
    await expect.poll(read).toBeUndefined();
});
