// The production 400 of 2026-10-23: a kiosk tablet acting as a member sat idle, kiosk-idle.js posted the release form
// long after the page (and its antiforgery token, bound to the member) had rendered, the acting session had expired in
// the meantime, and the antiforgery filter answered a bare 400 that nobody could tap past. Release now needs no token,
// and any error page a tablet lands on goes back to its wall by itself. Seeded wall Alpha, its kiosk key and Carol.
import { test, expect } from '../harness/fixtures.mjs';

async function registerKiosk(page, token) {
    await page.goto('/oauth-select');
    await page.getByText('Register this device as a wall kiosk').click();
    await page.getByLabel('Kiosk key').fill(token);
    await page.getByRole('button', { name: 'Register kiosk' }).click();
    await page.waitForURL(/\/walls\/[0-9a-f-]{36}/);
}

// The act-as form POST, as in sw-kiosk.spec.mjs (a member without a PIN gets a confirm form).
async function actAs(page, user) {
    await page.goto(`/kiosk/users/${user.id}`);
    const form = page.locator('form[action="/kiosk/act-as"]').first();
    await expect(form).toBeAttached();
    const antiforgery = await form.locator('input[name="__RequestVerificationToken"]').inputValue();
    const response = await page.request.post('/kiosk/act-as', {
        form: { userId: user.id, pin: '', __RequestVerificationToken: antiforgery }
    });
    expect(response.ok()).toBe(true);
}

// What the server does when the 30-minute window lapses: the session cookie stops counting, and the browser drops it.
const expireTheSession = context => context.clearCookies({ name: /^\.AspNetCore\.Cookies/ });

test('the idle release after the session expired lands the tablet on its wall, not on a 400', async ({ page, world }) => {
    const wall = `/walls/${world.walls.alpha.id}`;
    await registerKiosk(page, world.kioskToken);
    await actAs(page, world.users.carol);

    await page.clock.install();
    await page.goto(wall);
    await expect(page.locator('body')).toHaveAttribute('data-bw-user', world.users.carol.id);
    await expect(page.locator('form#bw-kiosk-release')).toBeAttached();

    await expireTheSession(page.context());
    const release = page.waitForResponse(r => r.url().endsWith('/kiosk/release'));
    await page.clock.fastForward('29:30');

    expect((await release).status()).toBe(302);
    await page.waitForURL(url => url.pathname === wall);
    await expect(page.locator('body')).not.toHaveAttribute('data-bw-user', world.users.carol.id);
    await expect(page.locator('form#bw-kiosk-release')).toHaveCount(0);
});

test('an error page on a tablet goes back to the wall by itself', async ({ page, world }) => {
    const wall = `/walls/${world.walls.alpha.id}`;
    await registerKiosk(page, world.kioskToken);

    // A form post the antiforgery filter refuses, submitted as a real navigation (act-as stays protected).
    const refused = page.waitForResponse(r => r.url().endsWith('/kiosk/act-as'));
    await page.evaluate(userId => {
        const form = document.createElement('form');
        form.method = 'post';
        form.action = '/kiosk/act-as';
        for (const [name, value] of [['userId', userId], ['pin', ''], ['__RequestVerificationToken', 'stale']]) {
            const input = document.createElement('input');
            input.name = name;
            input.value = value;
            form.appendChild(input);
        }
        document.body.appendChild(form);
        form.submit();
    }, world.users.carol.id);

    expect((await refused).status()).toBe(400);
    await expect(page.getByRole('link', { name: 'Back to the wall' })).toBeVisible();
    await page.waitForURL(url => url.pathname === wall, { timeout: 15000 });
    await expect(page.locator('body')).not.toHaveAttribute('data-bw-user', world.users.carol.id);
});
