// One-time visitor links, on the Visit wall (seed/SeedVisit.cs). Mia, a plain member, makes a link from the wall page
// (Invite | Visit | 3D View); a fresh browser opens it, starts the visit and browses the wall read-only: no invite, edit,
// log or comment controls, no draft, nothing kept on the device. The same browser opening the link again goes back to
// the wall; a second fresh browser is told the link was used and cannot see the wall. A seeded link that expired an
// hour ago shows the expired page. On a wall with a 3D model the three actions share one tidy row. Screenshots land
// in test/browser/.out/visit-*.png.
import { test, expect, signIn } from '../harness/fixtures.mjs';

const PHONE = { width: 402, height: 874 };
const ready = page => expect(page.locator('.wall-carousel:not(.carousel-pending)')).toBeVisible();

// Every browser here has accepted the cookie notice already. The notice renders only once the circuit is live, at no
// fixed time: arriving under an open dialog, its OK button cannot be tapped.
const consented = (context, baseURL) =>
    context.addCookies([{ name: 'cookie_consent', value: 'accepted', url: baseURL }]);

test.beforeEach(({ context, baseURL }) => consented(context, baseURL));

async function shoot(page, name) {
    for (const scheme of ['light', 'dark']) {
        await page.emulateMedia({ colorScheme: scheme });
        await page.screenshot({ path: `.out/visit-${name}-${scheme}.png` });
    }
    await page.emulateMedia({ colorScheme: 'light' });
}

// Mia makes a link on her phone and reads it out of the dialog. A tap before the circuit is live does nothing, so it
// taps until the dialog is there. An open dialog covers the button, and it may open later than one round's wait: no tap
// then, and a tap it overtakes gives up and goes round again.
async function makeLink(page, world, browserName) {
    const wall = world.walls.visit;
    await page.setViewportSize(PHONE);
    await signIn(page, world.users.mia, `/walls/${wall.id}`);
    await ready(page);
    const actions = page.locator('.wall-actions');
    await expect(actions.locator('.btn-secondary')).toHaveText([/Invite/, /Visit/]);
    if (browserName === 'chromium') {
        await shoot(page, 'buttons-phone');
    }

    const dialog = page.getByRole('dialog', { name: 'Visitor link' });
    const input = page.locator('.visit-link-input');
    await expect(async () => {
        if (!await dialog.isVisible()) {
            await actions.getByRole('button', { name: 'Visit', exact: true }).click({ timeout: 2000 });
        }
        await expect(input).toBeVisible({ timeout: 2000 });
    }).toPass();
    await expect(dialog).toContainText('It works once');
    if (browserName === 'chromium') {
        await shoot(page, 'dialog-phone');
    }

    const link = await input.inputValue();
    expect(link).toMatch(/\/visit\/[A-Za-z0-9_-]{43}$/);
    return new URL(link).pathname;
}

test('a visitor link opens the wall read-only once, for the browser that used it', async ({ page, browser, baseURL, world, browserName }) => {
    const wall = world.walls.visit;
    const path = await makeLink(page, world, browserName);

    const guestContext = await browser.newContext({ baseURL, viewport: PHONE });
    const otherContext = await browser.newContext({ baseURL, viewport: PHONE });
    await consented(guestContext, baseURL);
    await consented(otherContext, baseURL);
    try {
        const guest = await guestContext.newPage();
        await guest.goto(path);
        await expect(guest.getByRole('heading', { name: 'You\'re invited to visit Visit' })).toBeVisible();
        if (browserName === 'chromium') {
            await shoot(guest, 'landing-phone');
        }
        await guest.getByRole('button', { name: 'Start visit' }).click();
        await guest.waitForURL(new RegExp(`/walls/${wall.id}$`));
        await ready(guest);

        await expect(guest.locator('.bw-anon-notice')).toContainText('visiting as a guest');
        await expect(guest.locator('.wall-invite, .wall-visit')).toHaveCount(0);
        await expect(guest.locator('meta[name="bw-partition"]')).toHaveAttribute('content', 'none');
        if (browserName === 'chromium') {
            await shoot(guest, 'visitor-wall-phone');
        }

        await guest.goto(`/walls/${wall.id}?view=boulders&style=all`);
        await ready(guest);
        await expect(guest.getByText('Visit boulder').first()).toBeVisible();
        await expect(guest.getByText('Visit draft')).toHaveCount(0);

        await guest.getByText('Visit boulder').first().click();
        await guest.waitForURL(/\/boulders\/[0-9a-f-]{36}/);
        await expect(guest.locator('.bw-anon-notice')).toContainText('nothing can be logged');
        await expect(guest.locator('.attempt-buttons, .comment-input, .backdate-card')).toHaveCount(0);

        // Mia agreed to be named on the gym's tablet, not to remote guests: her comment reads as "A member".
        const comment = guest.locator('.comments-section .comment-row', { hasText: 'Mantle at the top' });
        await expect(comment.locator('.comment-author')).toHaveText('A member');
        await expect(guest.locator('body')).not.toContainText('Mia');

        // No way in to setting a boulder, and the server refuses the offline write queue outright.
        await guest.goto(`/walls/${wall.id}/boulders/create`);
        await guest.waitForURL(new RegExp(`/walls/${wall.id}$`));
        const write = await guest.request.post('/api/offline/attempts', { data: {}, failOnStatusCode: false, maxRedirects: 0 });
        expect([401, 403, 302]).toContain(write.status());

        // Same browser, same link: back to the wall.
        await guest.goto(path);
        await guest.waitForURL(new RegExp(`/walls/${wall.id}$`));

        // Any other browser: used, and no wall.
        const other = await otherContext.newPage();
        await other.goto(path);
        await expect(other.getByRole('heading', { name: 'This invitation was already used' })).toBeVisible();
        await expect(other.getByText('Ask a member of the wall for a new one')).toBeVisible();
        if (browserName === 'chromium') {
            await shoot(other, 'used-phone');
        }
        await other.goto(`/walls/${wall.id}`);
        await expect(other).not.toHaveURL(new RegExp(`/walls/${wall.id}$`));
    } finally {
        await guestContext.close();
        await otherContext.close();
    }
});

test('an expired visitor link says so and starts nothing', async ({ page, world, browserName }) => {
    await page.setViewportSize(PHONE);
    await page.goto(`/visit/${world.expiredVisitToken}`);

    await expect(page.getByRole('heading', { name: 'This invitation has expired' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Start visit' })).toHaveCount(0);
    if (browserName === 'chromium') {
        await shoot(page, 'expired-phone');
    }
});

test('the wall overview shows Invite, Visit and 3D View in one even row, on a phone and a desktop', async ({ page, world, browserName }) => {
    const wall = world.walls.alpha;
    for (const [name, viewport] of [['phone', PHONE], ['desktop', { width: 1440, height: 900 }]]) {
        await page.setViewportSize(viewport);
        await signIn(page, world.users.alice, `/walls/${wall.id}`);
        await ready(page);
        const buttons = page.locator('.wall-actions > .btn-secondary');
        await expect(buttons).toHaveText([/Invite/, /Visit/, /^\s*3D View\s*$/]);

        const boxes = await buttons.evaluateAll(els => els.map(el => el.getBoundingClientRect().toJSON()));
        expect(new Set(boxes.map(b => Math.round(b.top))).size).toBe(1);
        expect(Math.max(...boxes.map(b => b.width)) - Math.min(...boxes.map(b => b.width))).toBeLessThan(2);
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
        if (browserName === 'chromium') {
            await shoot(page, `buttons-3d-${name}`);
        }
    }
});
