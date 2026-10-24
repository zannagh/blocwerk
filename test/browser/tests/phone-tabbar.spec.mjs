// Editing a multi-panel boulder on a phone: the bottom tab bar stays pinned to the bottom edge through every panel, and
// steps aside while the keyboard is up or the page is zoomed, where iOS would draw it mid-screen over the form. The
// on-screen keyboard and a pinch cannot be produced in Playwright, so both are simulated through the visual viewport.
import { test, expect, signIn } from '../harness/fixtures.mjs';

test.use({ phone: 'iPhone 13' });

// The revise page and the inline rename editor hold an edit lease, which holds deploys; a closed context leaves its
// circuit (and the lease) for minutes. Leave the page instead, so the next spec starts with no lease (wall-back-to-list
// checks /health/ready-to-deploy).
test.afterEach(async ({ page }) => {
    if (page.url().startsWith('http')) {
        await page.evaluate(() => window.Blazor && window.Blazor.navigateTo('/walls')).catch(() => undefined);
    }
    await expect.poll(async () => {
        const ready = await page.request.get('/health/ready-to-deploy');
        return `${ready.status()} ${(await ready.text()).trim()}`;
    }, { timeout: 15000 }).toBe('200 idle');
});

function tabbar(page) {
    return page.evaluate(() => {
        const bar = document.querySelector('.bw-tabbar');
        const r = bar.getBoundingClientRect();
        return {
            top: r.top, bottom: r.bottom, left: r.left, width: r.width, innerHeight, innerWidth,
            visible: getComputedStyle(bar).visibility === 'visible' && getComputedStyle(bar).display !== 'none'
        };
    });
}

async function expectPinned(page) {
    const bar = await tabbar(page);
    expect(bar.visible).toBe(true);
    expect(Math.abs(bar.bottom - bar.innerHeight)).toBeLessThanOrEqual(1);
    expect(bar.left).toBe(0);
    expect(bar.width).toBe(bar.innerWidth);
}

// What iOS does to the visual viewport: the keyboard shrinks it, a pinch scales it.
function fakeViewport(page, { height, scale }) {
    return page.evaluate(([h, s]) => {
        const vv = window.visualViewport;
        Object.defineProperty(vv, 'height', { configurable: true, get: () => h ?? window.innerHeight });
        Object.defineProperty(vv, 'scale', { configurable: true, get: () => s ?? 1 });
        vv.dispatchEvent(new Event('resize'));
    }, [height ?? null, scale ?? null]);
}

async function openRevise(page, world) {
    const mp = world.multiPanel;
    await signIn(page, world.users.pia, `/walls/${mp.wallId}/boulders/${mp.boulder}/revise`);
    await expect(page.locator('.panel-selector')).toBeVisible();
    await expect(page.locator('.picker-viewport img')).toBeVisible();
}

test('revising a multi-panel boulder keeps the tab bar on the bottom edge through every panel', async ({ page, world }) => {
    await openRevise(page, world);
    await expectPinned(page);
    for (const label of ['Panel 2/2', 'Panel 1/2']) {
        // Retried: a tap before the circuit is interactive does nothing (two panels, so a late double step lands back).
        await expect(async () => {
            if (await page.locator('.panel-selector-label').textContent() !== label) {
                await page.locator('.panel-selector button').nth(1).click();
            }
            await expect(page.locator('.panel-selector-label')).toHaveText(label, { timeout: 1000 });
        }).toPass();
        await expectPinned(page);
        await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
        await expectPinned(page);
        await page.evaluate(() => window.scrollTo(0, 0));
    }
});

test('the tab bar steps aside while the keyboard is up or the page is zoomed, and comes back pinned', async ({ page, world }) => {
    await openRevise(page, world);
    const html = page.locator('html');
    const name = page.locator('.boulder-create input.form-input').first();

    await name.focus();
    await fakeViewport(page, { height: 300 });
    await expect(html).toHaveClass(/\bbw-vv-away\b/);
    expect((await tabbar(page)).visible).toBe(false);

    await name.blur();
    await fakeViewport(page, {});
    await expect(html).not.toHaveClass(/\bbw-vv-away\b/);
    await expectPinned(page);

    // A shrunken viewport with nothing being typed (the URL bar, a toolbar) is not the keyboard.
    await fakeViewport(page, { height: 300 });
    await page.evaluate(() => window.bwTabbarGuard.refresh());
    await expect(html).not.toHaveClass(/\bbw-vv-away\b/);

    await fakeViewport(page, { scale: 2 });
    await expect(html).toHaveClass(/\bbw-vv-away\b/);
    await fakeViewport(page, {});
    await expect(html).not.toHaveClass(/\bbw-vv-away\b/);
    await expectPinned(page);
});

test('the inline rename editor on a multi-panel boulder gets its own row on a phone', async ({ page, world }) => {
    const mp = world.multiPanel;
    await signIn(page, world.users.pia, `/walls/${mp.wallId}/boulders/${mp.boulder}`);
    await page.locator('.boulder-edit-btn').click();
    const editor = page.locator('.boulder-detail .page-header > .boulder-meta-edit');
    await expect(editor).toBeVisible();
    const [header, box] = await Promise.all([
        page.locator('.boulder-detail .page-header').boundingBox(),
        editor.boundingBox()
    ]);
    expect(Math.abs(box.width - header.width)).toBeLessThanOrEqual(1);
    await expectPinned(page);
    await editor.getByRole('button', { name: 'Cancel' }).click();
    await expect(editor).toHaveCount(0);
});

// iOS zooms the page in when a text field under 16px takes focus and leaves it zoomed after the keyboard closes; the
// fixed tab bar is then drawn mid-screen over the form. Playwright cannot zoom on focus, so this pins the cause: no
// text field on the edit flow (revise page, the boulder page with its rename editor open) is under 16px on a phone.
const smallFields = page => page.evaluate(() => [...document.querySelectorAll('input, select, textarea')]
    .filter(e => !['hidden', 'checkbox', 'radio', 'range', 'color', 'file', 'button', 'submit', 'reset', 'image'].includes(e.type))
    .filter(e => e.getClientRects().length > 0 && parseFloat(getComputedStyle(e).fontSize) < 16)
    .map(e => `${e.tagName.toLowerCase()}.${e.className} ${getComputedStyle(e).fontSize}`));

test('no text field on the boulder edit flow is small enough for iOS to zoom the page on focus', async ({ page, world }) => {
    await openRevise(page, world);
    expect(await smallFields(page)).toEqual([]);
    await page.locator('.panel-selector button').nth(1).click();
    expect(await smallFields(page)).toEqual([]);

    const mp = world.multiPanel;
    await page.goto(`/walls/${mp.wallId}/boulders/${mp.boulder}`);
    await page.locator('.boulder-edit-btn').click();
    const editor = page.locator('.boulder-meta-edit');
    await expect(editor).toBeVisible();
    expect(await smallFields(page)).toEqual([]);
    await editor.getByRole('button', { name: 'Cancel' }).click();
    await expect(editor).toHaveCount(0);
});
