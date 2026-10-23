// The offline client's skeleton (Phase 3 PR 3.8): /v/ boots the standalone WebAssembly client from its static shell,
// with no circuit, no edit presence and the shared cache modules loaded (their globals only). Nothing on the Server
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
    await expect(page.locator('h1')).toHaveText('Not available offline');
    expect(requests.some(p => p.startsWith('/v/_framework/') && p.endsWith('.wasm'))).toBe(true);
    expect(requests.filter(p => p.startsWith('/_blazor') || /blazor\.(web|server)\./.test(p) || p.includes('edit-presence'))).toEqual([]);
    // The shared library's modules come from Web's single copy at /_content/, never from under /v/.
    expect(requests.filter(p => p.startsWith('/v/_content/'))).toEqual([]);
    const globals = await page.evaluate(() => ['bwAssetLedgerIdb', 'bwAssetLedger', 'bwAssetWipe', 'bwAssetLock', 'bwAssetSession', 'bwAssetClear']
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

test('/v redirects to /v/, and a missing client file is a 404, never the shell', async ({ page, request }) => {
    const bare = await request.get('/v?x=1', { maxRedirects: 0 });
    expect(bare.status()).toBe(302);
    expect(bare.headers().location).toBe('/v/?x=1');

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
