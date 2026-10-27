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

// Regression guard, not a reproduction: the emulator cannot zoom on focus, so this passed before the fix as well. It
// pins that nothing in the multi-panel revise flow itself moves the bar (overflow, a trapping ancestor, a scroll jump).
test('guard: revising a multi-panel boulder keeps the tab bar on the bottom edge through every panel', async ({ page, world }) => {
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

test('the tab bar steps aside only while the keyboard is up, and a page left zoomed keeps it reachable', async ({ page, world }) => {
    await openRevise(page, world);
    const html = page.locator('html');
    const name = page.locator('.boulder-create input.form-input').first();

    // Retried: the circuit coming up re-renders the form and can take the focus away from a field focused before it.
    await expect(async () => {
        await name.focus();
        await fakeViewport(page, { height: 300 });
        await expect(html).toHaveClass(/\bbw-vv-away\b/, { timeout: 1000 });
    }).toPass();
    expect((await tabbar(page)).visible).toBe(false);

    await name.blur();
    await fakeViewport(page, {});
    await expect(html).not.toHaveClass(/\bbw-vv-away\b/);
    await expectPinned(page);

    // A shrunken viewport with nothing being typed (the URL bar, a toolbar) is not the keyboard.
    await fakeViewport(page, { height: 300 });
    await page.evaluate(() => window.bwTabbarGuard.refresh());
    await expect(html).not.toHaveClass(/\bbw-vv-away\b/);

    // Focus zoom that outlives the keyboard: the field is blurred, the page stays zoomed (scale 2, a visual viewport
    // half the size). The bar must stay visible and tappable, never hidden until the user happens to pinch out.
    await name.focus();
    await fakeViewport(page, { height: 332, scale: 2 });
    await name.blur();
    await fakeViewport(page, { height: 332, scale: 2 });
    await page.evaluate(() => window.bwTabbarGuard.refresh());
    await expect(html).not.toHaveClass(/\bbw-vv-away\b/);
    const bar = await tabbar(page);
    expect(bar.visible).toBe(true);
    expect(await page.evaluate(() => getComputedStyle(document.querySelector('.bw-tabbar')).pointerEvents)).not.toBe('none');
    await fakeViewport(page, {});
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

test('no text field on the boulder edit flow or the settings pages is small enough for iOS to zoom the page on focus', async ({ page, world }) => {
    await openRevise(page, world);
    expect(await smallFields(page)).toEqual([]);
    await page.locator('.panel-selector button').nth(1).click();
    expect(await smallFields(page)).toEqual([]);

    // Every move below is an in-app navigation, never page.goto: a full page load only DISCONNECTS the revise
    // page's circuit, and a disconnected circuit keeps its edit lease (and so the deploy gate busy) for its whole
    // reconnect window (EditActivityPolicy.DisconnectedCircuitRetention, 12 min), by design. Leaving in-app disposes
    // the editor and releases the lease at once.
    const mp = world.multiPanel;
    const go = url => page.evaluate(u => window.Blazor.navigateTo(u), url);
    await go(`/walls/${mp.wallId}/boulders/${mp.boulder}`);
    await page.locator('.boulder-edit-btn').click();
    const editor = page.locator('.boulder-meta-edit');
    await expect(editor).toBeVisible();
    expect(await smallFields(page)).toEqual([]);
    await editor.getByRole('button', { name: 'Cancel' }).click();
    await expect(editor).toHaveCount(0);

    // The settings surfaces: the wall's settings slide (every card opened) and the profile page.
    await go(`/walls/${mp.wallId}`);
    await expect(page.locator('.wall-carousel:not(.carousel-pending)')).toBeVisible();
    // The cookie banner sits over the dots and cards on a phone: accept it, as a visitor would.
    const consent = page.locator('.bw-consent');
    if (await consent.isVisible()) {
        await consent.getByRole('button', { name: 'OK' }).click();
        await expect(consent).toBeHidden();
    }
    await page.getByRole('button', { name: 'Page 1', exact: true }).click();
    // Open every card that is still closed (some start open, and a click on those would close them).
    const closed = page.locator('.wall-carousel > .wall-page').nth(0).locator('details.settings-card:not([open]) > summary');
    for (let guard = 0; guard < 20 && await closed.count() > 0; guard++) {
        const before = await closed.count();
        await closed.first().click();
        await expect(closed).toHaveCount(before - 1);
    }
    await expect(closed).toHaveCount(0);
    await page.waitForTimeout(500);
    expect(await smallFields(page)).toEqual([]);
    await go('/profile');
    await expect(page).toHaveURL(/\/profile$/);
    await expect(page.locator('main')).toBeVisible();
    await page.waitForTimeout(500);
    expect(await smallFields(page)).toEqual([]);
});
