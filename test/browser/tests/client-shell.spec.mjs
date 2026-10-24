// The offline client's skeleton (Phase 3 PR 3.8): /v/ boots the standalone WebAssembly client from its shell (rendered
// by Web, every URL fingerprinted), with no circuit, no edit presence and the shared cache modules loaded (their
// globals only). A failed start shows an error with Retry and Open online instead of hanging. Nothing on the Server
// pages points at it yet, and its files never fall back to the shell.
import { test, expect, signIn } from '../harness/fixtures.mjs';

const READY = 'bw-client-ready';

async function bootClient(page, path) {
    const requests = [];
    page.on('request', r => requests.push(new URL(r.url()).pathname));
    const response = await page.goto(path);
    await expect.poll(() => page.evaluate(m => performance.getEntriesByName(m).length, READY), { timeout: 30000 }).toBe(1);
    return { response, requests };
}

test('/v/ boots the client from its shell, with no circuit and no edit presence', async ({ page }) => {
    const { response, requests } = await bootClient(page, '/v/');

    expect(response.status()).toBe(200);
    expect(response.headers()['cache-control']).toContain('no-cache');
    await expect(page.locator('meta[name="bw-partition"]')).toHaveAttribute('content', 'offline');
    // /v/ shows the wall list (PR 3.11): nothing saved on a fresh browser.
    await expect(page.locator('h1')).toHaveText('My Walls');
    expect(requests.some(p => p.startsWith('/v/_framework/') && p.endsWith('.wasm'))).toBe(true);
    expect(requests.filter(p => p.startsWith('/_blazor') || /blazor\.(web|server)\./.test(p) || p.includes('edit-presence'))).toEqual([]);
    // The shared library's modules come from Web's single copy at /_content/, never from under /v/.
    expect(requests.filter(p => p.startsWith('/v/_content/'))).toEqual([]);
    // Every file the shell names is fingerprinted, the runtime's modules through the import map.
    const named = await page.evaluate(() => [...document.querySelectorAll('script[src], link[rel="stylesheet"]')].map(e => e.getAttribute('src') || e.getAttribute('href')));
    expect(named.length).toBe(19);
    expect(named.filter(u => !/^\/[^?#]+\.[a-z0-9]{10}\.(js|css)$/.test(u))).toEqual([]);
    const imports = await page.evaluate(() => JSON.parse(document.querySelector('script[type="importmap"]').textContent).imports);
    expect(imports['/v/_framework/dotnet.js']).toMatch(/^\/v\/_framework\/dotnet\.[a-z0-9]{10}\.js$/);
    expect(requests.filter(p => /^\/v\/_framework\/(dotnet|blazor\.webassembly)\.js$/.test(p))).toEqual([]);
    const globals = await page.evaluate(() => ['bwAssetLedgerIdb', 'bwAssetLedger', 'bwAssetWipe', 'bwAssetLock', 'bwAssetSession', 'bwAssetClear', 'bwClientStoreRows', 'bwClientStore', 'bwClientView']
        .filter(name => typeof window[name] !== 'object'));
    expect(globals).toEqual([]);
});

test('a deep /v/ path opens the client, and its link leaves for the Server page with a full navigation', async ({ page, world }) => {
    const wall = world.walls.alpha;
    await bootClient(page, `/v/walls/${wall.id}?view=boulders`);

    const link = page.getByRole('link', { name: 'Open it online' });
    await expect(link).toHaveAttribute('href', `/walls/${wall.id}?view=boulders`);
    await expect(page).toHaveURL(new RegExp(`/v/walls/${wall.id}\\?view=boulders$`));
});

test('/v and a wrong-case /V/ path redirect to /v/, and a missing client file is a 404, never the shell', async ({ page, request }) => {
    const redirects = { '/v?x=1': '/v/?x=1', '/V': '/v/', '/V/walls/Ab12?view=x': '/v/walls/Ab12?view=x' };
    for (const [from, to] of Object.entries(redirects)) {
        const bare = await request.get(from, { maxRedirects: 0 });
        expect(bare.status(), from).toBe(302);
        expect(bare.headers().location, from).toBe(to);
    }
    await bootClient(page, '/V/walls/x');
    await expect(page).toHaveURL(/\/v\/walls\/x$/);

    for (const path of ['/v/_framework/nothing.wasm', '/v/css/nothing.css', '/index.html']) {
        const missing = await request.get(path);
        expect(missing.status(), path).toBe(404);
    }
    const framework = await request.get('/v/_framework/blazor.webassembly.js');
    expect(framework.status()).toBe(200);
    expect(framework.headers()['content-type']).toContain('javascript');
});

test('the Server pages load nothing of the client', async ({ page, world }) => {
    const requests = [];
    page.on('request', r => requests.push(new URL(r.url()).pathname));
    await signIn(page, world.users.alice, `/walls/${world.walls.alpha.id}`);
    await page.goto(`/walls/${world.walls.alpha.id}`);
    await expect(page.locator('.wall-carousel')).toBeVisible();
    await page.waitForTimeout(1000);

    expect(requests.filter(p => p === '/v' || p.startsWith('/v/'))).toEqual([]);
    expect(await page.locator('a[href^="/v/"]').count()).toBe(0);
});

test('a runtime file that fails to load shows the error, Retry and Open online, not the loading text', async ({ page, server, world }) => {
    const wall = world.walls.alpha;
    await server.faults([{ path: '^/v/_framework/System\\.Private\\.CoreLib\\.[a-z0-9]+\\.wasm$', status: 404 }]);
    await page.goto(`/v/walls/${wall.id}?view=boulders`);

    // Well inside the 20 s watchdog: the failed download itself shows the error.
    await expect(page.locator('h1')).toHaveText("Couldn't open the offline view", { timeout: 10000 });
    await expect(page.getByText('Opening the offline view')).toHaveCount(0);
    await expect(page.getByRole('link', { name: 'Open online' })).toHaveAttribute('href', `/walls/${wall.id}?view=boulders`);
    await server.faults([]);
    await page.getByRole('button', { name: 'Retry' }).click();
    await expect.poll(() => page.evaluate(m => performance.getEntriesByName(m).length, READY), { timeout: 30000 }).toBe(1);
});

test('a start that never renders shows the error after the watchdog', async ({ page, server }) => {
    test.setTimeout(120000);
    await server.faults([{ path: '^/v/_framework/System\\.Private\\.CoreLib\\.[a-z0-9]+\\.wasm$', delay: 90000 }]);
    await page.goto('/v/');

    await expect(page.locator('h1')).toHaveText("Couldn't open the offline view", { timeout: 40000 });
    await expect(page.getByRole('link', { name: 'Open online' })).toHaveAttribute('href', '/');
});

test('a slow first boot that lands after the error replaces the error with the client', async ({ page, server }) => {
    test.setTimeout(120000);
    await server.faults([{ path: '^/v/_framework/System\\.Private\\.CoreLib\\.[a-z0-9]+\\.wasm$', delay: 38000 }]);
    await page.goto('/v/');

    await expect(page.locator('h1')).toHaveText("Couldn't open the offline view", { timeout: 40000 });
    await expect(page.locator('h1')).toHaveText('My Walls', { timeout: 40000 });
    await expect(page.getByRole('button', { name: 'Retry' })).toHaveCount(0);
    expect(await page.evaluate(m => performance.getEntriesByName(m).length, READY)).toBe(1);
});
