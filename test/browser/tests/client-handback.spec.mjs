// The hand-off between the Server pages and the offline client (Phase 3 PR 3.14): a dropped circuit offers "Continue
// offline" (the client's twin of the page, from the device); in the client, back online, the next tap opens the Server
// page, and a slow one is waited for instead of bouncing back to /v/; a raised clientSchemaMin reloads the client into the
// deploy's version once, and never again for the same floor.
import { test, expect } from '../harness/fixtures.mjs';
import { declareRoutes, precacheAsAlice, clientRecord, pageVersion } from '../harness/client.mjs';

const wallPath = world => `/walls/${world.walls.alpha.id}`;
const clientReady = page => expect.poll(() => page.evaluate(() => performance.getEntriesByName('bw-client-ready').length), { timeout: 30000 }).toBe(1);
const pill = page => page.locator('#bw-client-pill .bw-client-pill-label');

// A tap on a client link, as a view renders one (an anchor under the /v/ base, which the client's router takes).
async function tap(page, href) {
    await page.evaluate(h => {
        const a = document.createElement('a');
        a.href = h;
        a.id = 'bw-test-tap';
        a.textContent = 'tap';
        document.querySelector('.bw-client-main').prepend(a);
    }, href);
    await page.locator('#bw-test-tap').click();
}

// Consent answered up front, as a returning user's is, so the banner does not cover the connection pill.
const consented = (page, baseURL) => page.context().addCookies([{ name: 'cookie_consent', value: 'accepted', url: baseURL }]);

// The pill is openable only once the page has noticed the server is gone (slower on WebKit), and a click on it before
// then does nothing: click until the panel shows.
const openPill = page => expect(async () => {
    if (!(await page.getByRole('button', { name: 'Retry now' }).isVisible())) {
        await page.locator('.bw-conn-pill').click({ timeout: 2000 });
    }
    await expect(page.getByRole('button', { name: 'Retry now' })).toBeVisible({ timeout: 2000 });
}).toPass({ timeout: 30000 });

async function continueOffline(page, world, server, baseURL) {
    await consented(page, baseURL);
    expect(await precacheAsAlice(page, world, server)).toBe('complete');
    await server.unreachable();
    await openPill(page);
    await page.getByRole('button', { name: 'Continue offline' }).click();
    await expect(page).toHaveURL(new RegExp(`/v${wallPath(world)}$`));
    await clientReady(page);
}

test('a dropped circuit offers the client, which opens the same page from the device', async ({ page, world, server, baseURL }) => {
    await continueOffline(page, world, server, baseURL);

    await expect(page.locator('meta[name="bw-partition"]')).toHaveAttribute('content', 'offline');
    await expect(pill(page)).toHaveText(/^Offline · read-only · saved \d{1,2}[:.]\d{2}/);
    // Offline, a tap stays in the client: its router moves, the document stays (no full load).
    await page.evaluate(() => { window.__bwSameDocument = true; });
    await tap(page, `walls/${world.walls.alpha.id}?view=boulders`);
    await expect(page).toHaveURL(new RegExp(`/v${wallPath(world)}\\?view=boulders$`));
    expect(await page.evaluate(() => window.__bwSameDocument === true)).toBe(true);
    await expect(pill(page)).toHaveText(/^Offline/);
});

test('no "Continue offline" while connected, nor for a page the client does not have', async ({ page, world, server, baseURL }) => {
    await consented(page, baseURL);
    // The device's client covers boulder pages only, not the wall page this test stays on.
    expect(await precacheAsAlice(page, world, server, [declareRoutes('walls/{w}/boulders/{b}')])).toBe('complete');
    expect(await page.evaluate(() => window.bwClientContinue.target())).toBeNull();

    await server.unreachable();
    await openPill(page);
    await expect(page.getByRole('button', { name: 'Retry now' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Continue offline' })).toHaveCount(0);
    expect(await page.evaluate(() => window.bwClientContinue.refresh())).toBeNull();
});

test('back online, the next tap opens the Server page, waiting for a slow one instead of bouncing back', async ({ page, world, server, baseURL }) => {
    await continueOffline(page, world, server, baseURL);

    // The server is back, slower than the worker's 4 s hand-off timer.
    await server.faults([declareRoutes(), { path: '^/walls/', delay: 6000 }]);
    await expect.poll(() => page.evaluate(() => window.bwClientHandback.probe()), { timeout: 20000 }).toBe(true);
    await expect(pill(page)).toHaveText('Back online · open live');

    const began = Date.now();
    await tap(page, `walls/${world.walls.alpha.id}?view=boulders`);
    await expect(page).toHaveURL(new RegExp(`${wallPath(world)}\\?view=boulders$`), { timeout: 20000 });
    await expect(page.locator('meta[name="bw-partition"]')).toHaveAttribute('content', /^u-/);
    expect(Date.now() - began).toBeGreaterThanOrEqual(5500);
    expect(new URL(page.url()).pathname.startsWith('/v/')).toBe(false);
});

test('the pill itself opens the live page once the server answers', async ({ page, world, server, baseURL }) => {
    await continueOffline(page, world, server, baseURL);
    await server.faults([declareRoutes()]);
    await expect.poll(() => page.evaluate(() => window.bwClientHandback.probe()), { timeout: 20000 }).toBe(true);
    await page.locator('#bw-client-pill button').click();
    await expect(page).toHaveURL(new RegExp(`${wallPath(world)}$`));
    await expect(page.locator('meta[name="bw-partition"]')).toHaveAttribute('content', /^u-/);
});

test('back online, a programmatic router navigation goes to the Server page too', async ({ page, world, server, baseURL }) => {
    await continueOffline(page, world, server, baseURL);
    await server.faults([declareRoutes()]);
    await expect.poll(() => page.evaluate(() => window.bwClientHandback.probe()), { timeout: 20000 }).toBe(true);
    // What a view's NavigationManager.NavigateTo does: the router's own navigation, no anchor involved.
    await page.evaluate(path => window.Blazor.navigateTo(path), `/v${wallPath(world)}?view=boulders`);
    await expect(page).toHaveURL(new RegExp(`${wallPath(world)}\\?view=boulders$`));
    await expect(page.locator('meta[name="bw-partition"]')).toHaveAttribute('content', /^u-/);
});

test('a raised clientSchemaMin reloads the client into the new version once, and never loops', async ({ page, world, server }) => {
    // The device holds an older client: its shell says another version and schema 0.
    const old = 'aaaaaaaaaaaaaaaa';
    const aged = declareRoutes(undefined, [
        ['name="bw-client-version" content="', `name="bw-client-version" content="${old}" data-real="`],
        ['name="bw-client-schema" content="1"', 'name="bw-client-schema" content="0"']
    ]);
    await precacheAsAlice(page, world, server, [aged]);
    expect((await clientRecord(page)).version).toBe(old);
    const current = await pageVersion(page);

    // This deploy serves the real client (schema 1) but asks for 7: one update and reload, then held.
    await server.faults([declareRoutes(), { path: '^/alive$', rewrite: ['"clientSchemaMin":1', '"clientSchemaMin":7'] }]);
    let loads = 0;
    page.on('framenavigated', frame => { if (frame === page.mainFrame()) { loads++; } });
    await page.goto(`/v${wallPath(world)}`);
    // Reads that a reload may interrupt count as "not yet".
    const settledRead = read => () => read().catch(() => null);
    await expect.poll(settledRead(() => clientRecord(page).then(r => r && r.version)), { timeout: 60000 }).toBe(current);
    await expect.poll(settledRead(() => page.locator('meta[name="bw-client-version"]').getAttribute('content', { timeout: 1000 })), { timeout: 30000 }).toBe(current);
    await clientReady(page);
    expect(await page.evaluate(() => sessionStorage.getItem('bw-client-schema-reload'))).toBe('7');
    expect(await page.evaluate(() => window.bwClientHandback.probe())).toBe(true);
    await page.waitForTimeout(3000);
    expect(loads).toBe(2);
    await expect(page.locator('meta[name="bw-client-schema"]')).toHaveAttribute('content', '1');
});
