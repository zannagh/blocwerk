// Boulder variants on a 375 px phone: a family is one row of the list (the parent, a pill and the grade range), Add Variant
// starts from the origin's outlined holds and rules and lets holds be removed and added, Mark Variant links both ways with a
// peek at the route, and the offline client shows the same rows and the Variants card from the device alone. The Variants
// wall is seeded by SeedVariants; the tests that change it undo their links or add only variants, so they can run again on
// the same world (Chromium and WebKit share one).
import { test, expect, signIn } from '../harness/fixtures.mjs';
import { precacheAs, expectClient } from '../harness/client.mjs';

test.use({ phone: 'iPhone 13 Mini' });

const listPage = page => page.locator('.wall-carousel > .wall-page').nth(3);
const row = (page, name) => listPage(page).locator('.boulder-card', { hasText: name });
const pickRow = (page, name) => page.locator('.variant-row', { hasText: name });
const pillCount = rowLocator => rowLocator.locator('.boulder-variants-pill [aria-hidden="true"]');

// Names what sticks out, so a failure says where to look.
async function expectNoOverflow(page) {
    const overflow = await page.evaluate(() => {
        const { scrollWidth, clientWidth } = document.documentElement;
        if (scrollWidth <= clientWidth) {
            return null;
        }

        const wide = [...document.querySelectorAll('body *')].filter(el => el.getBoundingClientRect().right > clientWidth + 1);
        return { scrollWidth, clientWidth, wide: wide.slice(0, 12).map(el => `${el.tagName}.${el.getAttribute('class')}`) };
    });
    expect(overflow).toBeNull();
}

// BW_VARIANT_SHOTS names a folder to keep screenshots in, for looking at the screens by eye.
async function shot(page, name) {
    await expectNoOverflow(page);
    if (process.env.BW_VARIANT_SHOTS) {
        await page.screenshot({ path: `${process.env.BW_VARIANT_SHOTS}/${name}.png`, fullPage: true });
    }
}

// Consent answered up front, as a returning climber's is, so the banner does not cover the page's buttons.
const answerConsent = (page, baseURL) => page.context().addCookies([{ name: 'cookie_consent', value: 'accepted', url: baseURL }]);

async function openList(page, world, baseURL) {
    const wall = world.walls.variants;
    await answerConsent(page, baseURL);
    await signIn(page, world.users.vera, `/walls/${wall.id}?view=boulders`);
    await page.goto(`/walls/${wall.id}?view=boulders`);
    await expect(page.locator('.wall-carousel:not(.carousel-pending)')).toBeVisible();
    return wall;
}

async function openBoulder(page, world, baseURL, name) {
    await openList(page, world, baseURL);
    await row(page, name).click();
    await expect(page.locator('h1').first()).toHaveText(name);
}

// The Mark Variant page is usable once its circuit answers: a peek that opens proves it.
async function peekAt(page, name) {
    const panel = page.locator('.variant-peek-panel');
    await expect(async () => {
        if (!await panel.isVisible()) {
            await pickRow(page, name).getByRole('button', { name: /Peek at/ }).click({ timeout: 2000 });
        }

        await expect(panel).toBeVisible({ timeout: 5000 });
    }).toPass();
    await expect(panel.getByRole('heading', { name })).toBeVisible();
    // The route itself: the wall's photo with the boulder's four outlined holds on it.
    await expect(panel.locator('img').first()).toBeVisible();
    await expect.poll(() => panel.locator('svg.hold-overlay polygon').evaluateAll(all => new Set(all.map(h => h.getAttribute('points'))).size)).toBe(4);
}

// How many of the wall's holds the boulder page draws for this boulder (a hold is drawn in several layers).
const drawnHolds = page => page.locator('.boulder-view-slot svg.hold-overlay polygon')
    .evaluateAll(all => new Set(all.map(h => h.getAttribute('points'))).size);

// The picker's holds from left to right (the seed rises from left to right, the spare hold last).
async function holdsByPosition(page) {
    const holds = page.locator('.photo-picker svg.hold-overlay [data-hold]');
    await expect(holds).toHaveCount(5);
    const placed = await holds.evaluateAll(all => all.map(h => ({ id: h.getAttribute('data-hold'), x: h.getBoundingClientRect().x })));
    return placed.sort((a, b) => a.x - b.x).map(h => page.locator(`.photo-picker svg.hold-overlay [data-hold="${h.id}"]`));
}

async function tapHold(page, hold) {
    await expect(async () => {
        await hold.click({ timeout: 2000 });
        await expect(page.locator('.classify-root')).toBeVisible({ timeout: 2000 });
    }).toPass();
}

async function closePeek(page) {
    await page.locator('.variant-peek-panel').getByRole('button', { name: 'Close' }).click();
    await expect(page.locator('.variant-peek-panel')).toHaveCount(0);
}

async function unlink(page, name) {
    await page.getByRole('link', { name: 'Mark Variant' }).click();
    await expect(page.getByRole('radio', { name: /This boulder is a variant of/ })).toBeChecked();
    await peekAt(page, name);
    await closePeek(page);
    await page.getByRole('radio', { name: 'Not a variant' }).check();
    await page.getByRole('button', { name: 'Confirm parent' }).click();
    await expect(page.locator('.variants-card')).toHaveCount(0);
}

test('a family is one row: the parent, a pill and the grade range, and a variant is found through it', async ({ page, world, baseURL }) => {
    await openList(page, world, baseURL);

    await expect(pillCount(row(page, 'Family parent'))).toHaveText('+2');
    await expect(row(page, 'Family parent').locator('.boulder-variants-label')).toHaveText('2 variants');
    await expect(row(page, 'Family parent').locator('.boulder-grade')).toHaveText('5+–7A');
    await expect(row(page, 'Family hard')).toHaveCount(0);
    await expect(row(page, 'Family easy')).toHaveCount(0);
    // A draft variant does not count, and keeps a row of its own so it can be finished.
    await expect(row(page, 'Family draft').locator('.boulder-variants-pill')).toHaveCount(0);
    await shot(page, 'list-375');

    await listPage(page).getByRole('button', { name: 'Search boulders' }).click();
    await listPage(page).getByRole('searchbox', { name: 'Search boulders' }).fill('family hard');
    await expect(listPage(page).locator('.boulder-card')).toHaveCount(1);
    await expect(row(page, 'Family parent')).toBeVisible();
});

test('the card lists the variants on a parent, and the marked parent and the sibling on a variant', async ({ page, world, baseURL }) => {
    await openBoulder(page, world, baseURL, 'Family parent');
    const card = page.locator('.variants-card');
    await expect(card.locator('.variants-row')).toHaveCount(2);
    await expect(card).not.toContainText('Family draft');
    await expect(card.locator('.variants-parent')).toHaveCount(0);

    await card.locator('.variants-row', { hasText: 'Family hard' }).click();
    await expect(page.locator('h1').first()).toHaveText('Family hard');
    await expect(card.locator('.variants-row', { hasText: 'Family parent' }).locator('.variants-parent')).toHaveText('parent');
    await expect(card.locator('.variants-row', { hasText: 'Family easy' })).toBeVisible();
    await shot(page, 'variant-card-375');
});

test('Add Variant starts from the origin, holds can be removed and added, and the original keeps its own', async ({ page, world, baseURL }) => {
    await openBoulder(page, world, baseURL, 'Origin');
    await page.getByRole('link', { name: 'Add Variant' }).click();

    await expect(page.getByRole('heading', { name: 'Add Variant' })).toBeVisible();
    await expect(page.locator('#bw-boulder-name')).toHaveValue(/^Origin Variant( \d+)?$/);
    await expect(page.locator('.grade-select').first()).toHaveValue('6B');
    await expect(page.locator('#boulder-custom-rule')).toHaveValue('Start sitting');
    const publish = page.locator('button.btn-submit');
    await expect(publish).toHaveText('Publish (4 holds)');
    await shot(page, 'add-variant-375');

    // Take out the two middle holds and add the spare one on the far right.
    const [, second, third, , spare] = await holdsByPosition(page);
    const selected = page.locator('.pick-count');
    await tapHold(page, second);
    await page.locator('.classify-bin').click();
    await expect(selected).toHaveText('3 selected');
    await tapHold(page, third);
    await page.locator('.classify-bin').click();
    await expect(selected).toHaveText('2 selected');
    await tapHold(page, spare);
    await shot(page, 'add-variant-menu-375');
    await page.locator('.classify-wedge.wedge-middle .classify-text').click();
    await expect(selected).toHaveText('3 selected');
    await expect(publish).toHaveText('Publish (3 holds)');

    await page.locator('.grade-select').first().selectOption('7A');
    await publish.click();
    await expect(page).not.toHaveURL(/boulders\/create/);

    // The variant: the holds as edited, the origin's rules, and the origin as its parent.
    await expect.poll(() => drawnHolds(page)).toBe(3);
    await expect(page.locator('.rule-chip', { hasText: 'Dedicated footholds' })).toBeVisible();
    await expect(page.locator('.boulder-custom-rule')).toHaveText('Start sitting');
    const parent = page.locator('.variants-row', { has: page.locator('.variants-parent') });
    await expect(parent).toContainText('Origin');
    await shot(page, 'add-variant-published-375');

    // The original still has all of its holds.
    await parent.click();
    await expect(page.locator('h1').first()).toHaveText('Origin');
    await expect.poll(() => drawnHolds(page)).toBe(4);

    await page.goto(`/walls/${world.walls.variants.id}?view=boulders`);
    await expect(row(page, 'Origin').first().locator('.boulder-grade')).toHaveText('6B–7A');
});

test('Mark Variant links both ways, with a peek before confirming', async ({ page, world, baseURL }) => {
    test.setTimeout(150000);
    await openBoulder(page, world, baseURL, 'Mark start');
    await page.getByRole('link', { name: 'Mark Variant' }).click();

    // This boulder stays the parent: the checklist.
    await expect(page.getByRole('radio', { name: /Variants of this boulder/ })).toBeChecked();
    await peekAt(page, 'Mark other');
    await shot(page, 'peek-375');
    await closePeek(page);
    await pickRow(page, 'Mark other').getByRole('checkbox').check();
    await shot(page, 'mark-checklist-375');
    await page.getByRole('button', { name: 'Confirm 1 variant' }).click();
    await page.locator('.variants-row', { hasText: 'Mark other' }).click();
    await expect(page.locator('h1').first()).toHaveText('Mark other');
    await unlink(page, 'Mark start');

    // The picked boulder becomes the parent.
    await page.getByRole('link', { name: 'Mark Variant' }).click();
    await peekAt(page, 'Mark start');
    await closePeek(page);
    await page.getByRole('radio', { name: /This boulder is a variant of/ }).check();
    await pickRow(page, 'Mark start').getByRole('radio').check();
    await shot(page, 'mark-parent-375');
    await page.getByRole('button', { name: 'Confirm parent' }).click();
    await expect(page.locator('.variants-row', { hasText: 'Mark start' }).locator('.variants-parent')).toHaveText('parent');
    await unlink(page, 'Mark start');
});

test('offline: the family row, its pill and range, and the Variants card, from the device alone', async ({ page, world, server }) => {
    const wall = world.walls.variants;
    expect(await precacheAs(page, server, world.users.vera, `/walls/${wall.id}`)).toBe('complete');
    await server.unreachable();
    await expectClient(page, `/walls/${wall.id}`);

    const parentRow = page.locator('.boulder-card', { hasText: 'Family parent' });
    await expect(pillCount(parentRow)).toHaveText('+2');
    await expect(parentRow.locator('.boulder-variants-label')).toHaveText('2 variants');
    await expect(parentRow.locator('.boulder-grade')).toHaveText('5+–7A');
    await expect(page.locator('.boulder-card', { hasText: 'Family hard' })).toHaveCount(0);
    // The overview counts what the list shows: a family is one boulder.
    await expect(page.locator('.stat-value').nth(1)).toHaveText(String(await page.locator('.boulder-card').count()));
    await parentRow.scrollIntoViewIfNeeded();
    await shot(page, 'offline-list-375');
    await page.emulateMedia({ colorScheme: 'dark' });
    await shot(page, 'offline-list-dark-375');
    await page.emulateMedia({ colorScheme: 'light' });

    await parentRow.click();
    const card = page.locator('.bw-client-variants');
    await expect(card.locator('.bw-client-variants-row')).toHaveCount(2);
    await expect(card).not.toContainText('Family draft');
    await shot(page, 'offline-parent-card-375');

    await card.locator('.bw-client-variants-row', { hasText: 'Family hard' }).click();
    await expect(page.locator('h1').first()).toHaveText('Family hard');
    await expect(card.locator('.bw-client-variants-row', { hasText: 'Family parent' }).locator('.bw-client-variants-parent')).toHaveText('parent');
    await expect(card.locator('.bw-client-variants-row', { hasText: 'Family easy' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Add Variant' })).toHaveCount(0);
    await expect(page.getByRole('link', { name: 'Mark Variant' })).toHaveCount(0);
    await shot(page, 'offline-variant-card-375');
    await page.emulateMedia({ colorScheme: 'dark' });
    await shot(page, 'offline-variant-card-dark-375');
});
