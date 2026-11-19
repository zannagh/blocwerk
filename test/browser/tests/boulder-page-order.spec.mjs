// The boulder page on a phone, top to bottom: the header (name, grade with the ascent icon, edit, 3D), the image, the
// ascent buttons, the details (name, setters, date), "Log at a specific time", the Project and Favorite toggles, the
// rating, comments, beta videos, variants, styles, hold type, history and activity. The favourite left the header.
import { test, expect, signIn } from '../harness/fixtures.mjs';

test.use({ phone: 'iPhone 16 Pro' });

// The browsers share one seeded world: each takes its own Logging boulder of Pia's Wide wall, whose marks no other
// spec reads (boulder-page-cleanup logs ascents on them, which no mark changes).
const slot = testInfo => ['chromium', 'webkit', 'firefox'].indexOf(testInfo.project.name);

async function openLoggingBoulder(page, world, testInfo) {
    await signIn(page, world.users.pia, `/walls/${world.walls.wide.id}?view=boulders&style=all`);
    await expect(page.locator('.wall-carousel:not(.carousel-pending)')).toBeVisible();
    await page.locator('.wall-carousel > .wall-page').nth(3)
        .locator('.boulder-card', { hasText: `Logging boulder ${slot(testInfo)}` }).click();
    await expect(page.locator('.boulder-marks')).toBeVisible();
    await page.waitForFunction(() => !!window.Blazor);
}

// The top edge of the first visible match of each selector, in page coordinates; null when it is not on the page.
const tops = (page, selectors) => page.evaluate(list => list.map(sel => {
    const el = [...document.querySelectorAll(sel)].find(e => e.getClientRects().length > 0);
    return el ? el.getBoundingClientRect().top + scrollY : null;
}), selectors);

test('the sections stack in the new order, and the favourite is out of the header', async ({ page, world }, testInfo) => {
    await openLoggingBoulder(page, world, testInfo);
    const order = ['.page-header', '.boulder-view-slot', '.attempt-section', '.boulder-below-meta', '.backdate-card',
        '.boulder-marks', '.rating-section', '.comments-section', '.beta-section', '.variant-actions',
        '.boulder-styles', '.activity-feed'];
    // The circuit's first render fills in the sections that load after the prerender; wait for all of them.
    await expect.poll(async () => {
        const shown = await tops(page, order);
        return order.filter((_, i) => shown[i] === null);
    }, { timeout: 20000 }).toEqual([]);
    const at = await tops(page, order);
    for (let i = 1; i < order.length; i++) {
        expect(at[i], `${order[i]} below ${order[i - 1]}`).toBeGreaterThanOrEqual(at[i - 1]);
    }

    await expect(page.locator('.title-actions .mark-wrap')).toHaveCount(0);
    await expect(page.locator('.title-actions .boulder-edit-btn')).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});

test('Project and Favorite are two pressed-state toggles side by side that each flip on their own', async ({ page, world }, testInfo) => {
    await openLoggingBoulder(page, world, testInfo);
    const marks = page.locator('.boulder-marks');
    const project = name => marks.getByRole('button', { name });
    const shown = sel => marks.locator(`${sel} .mark-toggle:visible`);

    // Start from neither, whatever an earlier run left.
    for (const off of ['Remove from projects', 'Remove from favourites']) {
        if (await project(off).isVisible()) {
            await project(off).click();
            await expect(project(off)).toBeHidden({ timeout: 20000 });
        }
    }

    const [left, right] = await Promise.all([shown('.mark-project').boundingBox(), shown('.mark-favorite').boundingBox()]);
    expect(Math.abs(left.y - right.y)).toBeLessThan(2);
    expect(left.x + left.width).toBeLessThanOrEqual(right.x);
    expect(right.x + right.width).toBeLessThanOrEqual(page.viewportSize().width);
    expect(left.height).toBeGreaterThanOrEqual(44);
    await expect(shown('.mark-project')).toHaveAttribute('aria-pressed', 'false');
    await expect(shown('.mark-favorite')).toHaveAttribute('aria-pressed', 'false');

    await project('Mark as project').click();
    await expect(shown('.mark-project')).toHaveAttribute('aria-pressed', 'true');
    await expect(shown('.mark-favorite')).toHaveAttribute('aria-pressed', 'false');
    await page.screenshot({ path: '.out/shots/boulder-marks-project-402.png' });

    // The server took it: a reload still shows it pressed.
    await page.reload();
    await expect(marks.getByRole('button', { name: 'Remove from projects' })).toBeVisible({ timeout: 20000 });
    await page.waitForFunction(() => !!window.Blazor);

    await project('Add to favourites').click();
    await expect(shown('.mark-favorite')).toHaveAttribute('aria-pressed', 'true');
    await page.emulateMedia({ colorScheme: 'dark' });
    await page.screenshot({ path: '.out/shots/boulder-marks-both-402-dark.png' });

    await project('Remove from projects').click();
    await expect(shown('.mark-project')).toHaveAttribute('aria-pressed', 'false');
    await expect(shown('.mark-favorite')).toHaveAttribute('aria-pressed', 'true');
    await project('Remove from favourites').click();
    await expect(shown('.mark-favorite')).toHaveAttribute('aria-pressed', 'false');
    await page.reload();
    await expect(marks.getByRole('button', { name: 'Mark as project' })).toBeVisible({ timeout: 20000 });
    await expect(marks.getByRole('button', { name: 'Add to favourites' })).toBeVisible();
});
