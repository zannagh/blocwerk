// The companion app's sign-in, as its browser sheet sees it (Core: DeviceLinkService, Web: DeviceLink.razor and the
// DeviceLink*Controller). Dina, who owns nothing else, opens the device-link page signed out, goes through sign-in and
// comes back to the consent page; Link redirects to blocwerk-companion://auth with a one-time code, which the "app"
// (this spec, with its PKCE verifier) exchanges for a device key that reads her account. A request with a foreign
// redirect shows an error and offers no Link button. Screenshots land in test/browser/.out/device-link-*.png.
import { createHash, randomBytes } from 'node:crypto';
import { test, expect, signIn } from '../harness/fixtures.mjs';

const PHONE = { width: 402, height: 874 };
const DESKTOP = { width: 1440, height: 900 };
const DEVICE = 'Dina’s iPhone';
// The match code the app shows (DeviceLinkPkce.MatchCode): top 30 bits of SHA-256 over the challenge, Crockford base32.
function matchCode(challenge) {
    const alphabet = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';
    const bits = createHash('sha256').update(challenge).digest().readUInt32BE(0) >>> 2;
    const chars = [0, 1, 2, 3, 4, 5].map(i => alphabet[(bits >>> (25 - 5 * i)) & 31]).join('');
    return `${chars.slice(0, 3)}-${chars.slice(3)}`;
}

const b64url = buf => buf.toString('base64').replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_');

test.beforeEach(({ context, baseURL }) =>
    context.addCookies([{ name: 'cookie_consent', value: 'accepted', url: baseURL }]));

function linkRequest(redirect = 'blocwerk-companion://auth') {
    const verifier = b64url(randomBytes(32));
    const state = b64url(randomBytes(32));
    const challenge = b64url(createHash('sha256').update(verifier).digest());
    const query = new URLSearchParams({
        state,
        code_challenge: challenge,
        code_challenge_method: 'S256',
        device: DEVICE,
        redirect_uri: redirect,
    });
    return { verifier, state, code: matchCode(challenge), path: `/device-link?${query}` };
}

// Presses a consent button and returns where the server sent the browser. The post goes through route.fetch without
// following the redirect, because a browser handed blocwerk-companion:// has nowhere to go (WebKit then reports no
// response at all); the page is answered with a stub instead.
async function decide(page, button) {
    let location = null;
    let status = 0;
    await page.route('**/device-link/confirm', async route => {
        const response = await route.fetch({ maxRedirects: 0 });
        status = response.status();
        location = response.headers()['location'] ?? null;
        await route.fulfill({ status: 200, contentType: 'text/html', body: '<p>back to the app</p>' });
    });
    await page.getByRole('button', { name: button, exact: true }).click();
    await expect.poll(() => status).not.toBe(0);
    expect(status).toBe(302);
    return new URL(location);
}

async function shoot(page, name) {
    for (const [size, viewport] of [['phone', PHONE], ['desktop', DESKTOP]]) {
        await page.setViewportSize(viewport);
        for (const scheme of ['light', 'dark']) {
            await page.emulateMedia({ colorScheme: scheme });
            // The buttons fade their colours over 0.18 s; a shot taken at once catches them half-way.
            await page.waitForTimeout(400);
            await page.screenshot({ path: `.out/device-link-${name}-${size}-${scheme}.png` });
        }
    }
    await page.setViewportSize(PHONE);
    await page.emulateMedia({ colorScheme: 'light' });
}

test('a signed-out phone signs in, links and gets a device key', async ({ page, world, browserName }) => {
    const link = linkRequest();
    await page.setViewportSize(PHONE);

    await page.goto(link.path);
    await expect(page).toHaveURL(/\/oauth-select\?/);
    const returnUrl = new URL(page.url()).searchParams.get('returnUrl');
    expect(returnUrl).toBe(link.path);

    // The provider round-trip is the dev sign-in here; it honours the same local return URL.
    await signIn(page, world.users.dina, returnUrl);
    await expect(page.getByRole('heading', { name: 'Link an iPhone to your Blocwerk account?' })).toBeVisible();
    // The code the app shows, and the device's own name only as quoted, untrusted text.
    expect(matchCode('E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM')).toBe('1MM-SP7');
    await expect(page.locator('.device-link-match-code')).toHaveText(link.code);
    await expect(page.locator('.device-link-match')).toContainText('Only continue if the Blocwerk app on your iPhone shows this code');
    await expect(page.locator('q.device-link-device-name')).toHaveText(DEVICE);
    if (browserName === 'chromium') {
        await shoot(page, 'consent');
    }

    const callback = await decide(page, 'Link');
    expect(`${callback.protocol}//${callback.host}`).toBe('blocwerk-companion://auth');
    expect(callback.searchParams.get('state')).toBe(link.state);
    const code = callback.searchParams.get('code');
    expect(code).toMatch(/^[A-Za-z0-9_-]{43}$/);

    const exchange = await page.request.post('/api/device-link/token', {
        data: { code, code_verifier: link.verifier, device: DEVICE },
    });
    expect(exchange.status()).toBe(200);
    const { token, name } = await exchange.json();
    expect(name).toBe(`iPhone: ${DEVICE}`);

    const again = await page.request.post('/api/device-link/token', {
        data: { code, code_verifier: link.verifier, device: DEVICE },
    });
    expect(again.status()).toBe(400);

    const account = await page.request.get('/api/v1/me/account', { headers: { Authorization: `Bearer ${token}` } });
    expect(account.status()).toBe(200);
    expect((await account.json()).displayName).toBe('Dina');
    const revoke = await page.request.delete('/api/v1/me/api-key', { headers: { Authorization: `Bearer ${token}` } });
    expect(revoke.status()).toBe(204);
});

test('cancel goes back to the app with access_denied', async ({ page, world }) => {
    const link = linkRequest();
    await signIn(page, world.users.dina, link.path);
    const callback = await decide(page, 'Cancel');
    expect(callback.searchParams.get('error')).toBe('access_denied');
    expect(callback.searchParams.get('state')).toBe(link.state);
});

test('a foreign redirect shows an error and nothing to link', async ({ page, world, browserName }) => {
    const link = linkRequest('https://evil.example/cb');
    await signIn(page, world.users.dina, link.path);
    await expect(page.getByRole('heading', { name: "This link request doesn't work" })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Link', exact: true })).toHaveCount(0);
    if (browserName === 'chromium') {
        await shoot(page, 'refused');
    }
});
