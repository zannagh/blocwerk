// Clear-Site-Data: "cache" and the browser's HTTP cache (PR 2.5 W1), with the worker blocked so the HTTP cache is the
// only cache in play.
//
// WebKit ignores the header on a 200 as well as on a 302, so nothing may rely on it: the /a/ routes answer
// `private, no-cache` (fix/asset-http-cache-isolation), and every reuse of an HTTP-cache copy revalidates through the
// access gate. The probes below record what each engine does; the identity tests check that it no longer matters.
import { test, expect, signIn, assetPath, pageFetch } from '../harness/fixtures.mjs';

test.use({ serviceWorkers: 'block', workerOnly: true });

const PROBE = '/__pw/cacheable?n=1';

async function servedWithoutServer(page, server, path) {
    await server.unreachable();
    try {
        return (await pageFetch(page, path)).status === 200;
    } finally {
        await server.faults([]);
    }
}

async function cacheProbe(page, server) {
    await page.goto('/__pw/blank');
    expect((await pageFetch(page, PROBE)).status).toBe(200);
    expect(await servedWithoutServer(page, server, PROBE)).toBe(true);
}

test('a 200 page with Clear-Site-Data: "cache" clears the HTTP cache in Chromium; WebKit ignores it', async ({ page, server, browserName }) => {
    await cacheProbe(page, server);
    await page.goto('/__pw/csd?status=200');
    expect(await servedWithoutServer(page, server, PROBE)).toBe(browserName === 'webkit');
});

test('a 302 with Clear-Site-Data: "cache" clears it in Chromium; WebKit ignores it', async ({ page, server, browserName }) => {
    await cacheProbe(page, server);
    await page.goto('/__pw/csd?status=302');
    await expect(page).toHaveURL(/\/__pw\/blank$/);
    expect(await servedWithoutServer(page, server, PROBE)).toBe(browserName === 'webkit');
});

test('an /a/ asset is private, no-cache under its sha, so the HTTP cache never answers it without the server', async ({ page, server, world }) => {
    await signIn(page, world.users.alice, '/__pw/blank');
    const wall = world.walls.alpha;
    const path = assetPath(wall.id, wall.panels[0], 640);
    const first = await pageFetch(page, path);
    expect(first).toMatchObject({ status: 200, cacheControl: 'private, no-cache', etag: `"${wall.panels[0]}-640"` });
    // A reuse revalidates: with the server unreachable it fails instead of answering from the HTTP cache.
    expect(await servedWithoutServer(page, server, path)).toBe(false);
});

for (const via of ['the sign-out page', 'a sign-in over the session']) {
    test(`with no worker, after ${via} the next identity's request for the previous one's asset reaches the server and is refused`, async ({ page, server, world }) => {
        const wall = world.walls.alpha;
        const path = assetPath(wall.id, wall.panels[0], 640);
        await signIn(page, world.users.alice);
        expect((await pageFetch(page, path)).status).toBe(200);
        if (via === 'the sign-out page') {
            await page.goto('/account/logout');
            await expect(page.locator('meta[name="bw-partition"]')).toHaveAttribute('content', 'anon');
        }
        await signIn(page, world.users.bob);

        const mark = await server.mark();
        expect((await pageFetch(page, path)).status).toBe(404);
        expect(await server.hits(mark, path)).toBe(1);
    });
}

test('sign-out answers a 200 page with Clear-Site-Data: "cache" and no-store, not a redirect', async ({ baseURL }) => {
    // Straight from the test process: Chromium hides the header from Playwright's view of the response.
    const response = await fetch(`${baseURL}/account/logout`, { redirect: 'manual' });
    expect(response.status).toBe(200);
    expect(response.headers.get('clear-site-data')).toBe('"cache"');
    expect(response.headers.get('cache-control')).toBe('no-store');
    const html = await response.text();
    expect(html).toContain("dispatchEvent(new Event('bw:identity-changed'))");
    expect(html).toContain('location.replace("/")');
});

test('the signed-out page loads its scripts by their fingerprinted URLs, and each one is served', async ({ baseURL }) => {
    const html = await (await fetch(`${baseURL}/account/logout`, { redirect: 'manual' })).text();
    const scripts = [...html.matchAll(/<script src="([^"]+)"/g)].map(m => m[1]);
    expect(scripts.length).toBeGreaterThan(0);
    for (const src of scripts) {
        // `/js/name.{fingerprint}.js`, the name `@Assets` hands out, not the plain path.
        expect(src).toMatch(/^\/js\/[a-z-]+\.[a-z0-9]{6,}\.js$/);
        const response = await fetch(baseURL + src);
        expect(response.status, src).toBe(200);
        expect(response.headers.get('content-type'), src).toContain('javascript');
    }
});
