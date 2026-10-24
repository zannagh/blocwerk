// The worker hands a failed Server-page navigation to the precached offline client (Phase 3 PR 3.9): with the server
// down, answering a 502/503/504, or silent for 4 s, a route the client declares opens at its /v/ twin, from the device alone.
// A healthy or merely slow-but-answering server is never cut off for a route the client cannot take, and the client is
// never shown for a locked partition, an undeclared route or an incomplete version: those get offline.html. A kiosk
// tablet is handed over since PR 3.15 (client-kiosk.spec.mjs has the rest of its offline mode).
import { test, expect, partitionOf, settled, ledger } from '../harness/fixtures.mjs';
import { registerKiosk } from '../harness/kiosk.mjs';
import { declareRoutes, precacheAsAlice, clientRecord, clientCaches, pageVersion, expectClient, expectOfflinePage, missingDocs } from '../harness/client.mjs';

const wallPath = world => `/walls/${world.walls.alpha.id}`;

test('a Server page precaches the client once its fill is done, and online navigations stay on the Server', async ({ page, world, server }) => {
    expect(await precacheAsAlice(page, world, server)).toBe('complete');
    const version = await pageVersion(page);
    expect(await clientRecord(page)).toEqual({ version, routes: ['walls/{w}', 'walls/{w}/boulders/{b}'] });
    expect(await clientCaches(page)).toEqual([`blocwerk-client-${version}`]);

    // Online, and slow but under 4 s: the Server page, never the client.
    await server.faults([{ path: '^/walls/', delay: 2500 }]);
    for (let i = 0; i < 2; i++) {
        const response = await page.goto(`${wallPath(world)}?view=boulders`);
        expect(response.status()).toBe(200);
        await expect(page).toHaveURL(new RegExp(`${wallPath(world)}\\?view=boulders$`));
        await expect(page.locator('meta[name="bw-partition"]')).toHaveAttribute('content', /^u-/);
        await server.faults([]);
    }
});

test('with the server down, a declared route opens in the client, its shell and files from the device', async ({ page, world, server }) => {
    expect(await precacheAsAlice(page, world, server)).toBe('complete');
    await server.unreachable();

    await expectClient(page, `${wallPath(world)}?view=boulders`);
    await expect(page.locator('meta[name="bw-partition"]')).toHaveAttribute('content', 'offline');
    await expect(page.locator('h1')).toHaveText('Not available offline');
});

test('a 502 hands over without asking the server for the shell, and a hanging server hands over after 4 s', async ({ page, world, server }) => {
    expect(await precacheAsAlice(page, world, server)).toBe('complete');
    const boulder = `${wallPath(world)}/boulders/00000000-0000-0000-0000-000000000001`;

    await server.faults([{ path: '^/walls/', status: 502 }]);
    const start = await server.mark();
    await expectClient(page, boulder);
    expect((await server.log(start)).filter(e => e.url.startsWith('/v/'))).toEqual([]);

    await server.faults([{ path: '^/walls/', delay: 12000 }]);
    const began = Date.now();
    await expectClient(page, wallPath(world));
    const took = Date.now() - began;
    expect(took).toBeGreaterThanOrEqual(3900);
    expect(took).toBeLessThan(11000);
});

test('an undeclared route, and a client that declares none, get offline.html', async ({ page, world, server }) => {
    expect(await precacheAsAlice(page, world, server, [])).toBe('complete');
    expect((await clientRecord(page)).routes).toEqual([]);
    await server.unreachable();
    await expectOfflinePage(page, wallPath(world));
});

test('a route outside the declared ones gets offline.html, a 5xx on it the server answer', async ({ page, world, server }) => {
    expect(await precacheAsAlice(page, world, server)).toBe('complete');
    await server.faults([{ path: '^/walls/[^/]+/gallery$', status: 503 }]);
    const response = await page.goto(`${wallPath(world)}/gallery`);
    expect(response.status()).toBe(503);
    await server.unreachable();
    await expectOfflinePage(page, `${wallPath(world)}/gallery`);
});

test('a locked partition is never handed to the client', async ({ page, world, server }) => {
    expect(await precacheAsAlice(page, world, server)).toBe('complete');
    const label = await page.locator('meta[name="bw-partition"]').getAttribute('content');
    // The page side of the 401 lock (asset-lock.js) writes this marker; the worker reads it fresh on every hand-off.
    await page.evaluate(async l => (await caches.open('bw-sw-state')).put('/__bw/locked', new Response(l)), label);
    await server.unreachable();
    await expectOfflinePage(page, wallPath(world));
});

test('a device turned kiosk is handed to the client, which shows the tablet\'s wall and nothing of the earlier user', async ({ page, world, server }) => {
    expect(await precacheAsAlice(page, world, server)).toBe('complete');
    const alice = await partitionOf(page);
    await registerKiosk(page, world.kioskToken);
    await expect(page.locator('meta[name="bw-partition"]')).toHaveAttribute('content', /^k-/);
    await settled(page);
    const kiosk = await partitionOf(page);
    await expect.poll(() => missingDocs(page, kiosk, world.walls.alpha.id), { timeout: 20000 }).toEqual([]);
    // The worker learns the tablet's label from the page's asset start (sw-refresh-partition) or its first asset.
    const served = () => page.evaluate(() => new Promise(resolve => {
        const channel = new MessageChannel();
        channel.port1.onmessage = e => resolve(e.data.partition);
        navigator.serviceWorker.controller.postMessage({ proto: 1, type: 'sw-refresh-partition' }, [channel.port2]);
        setTimeout(() => resolve(null), 5000);
    }));
    await expect.poll(served).toBe(kiosk);
    expect((await clientCaches(page)).length).toBe(1);
    expect((await ledger(page, alice)).walls, 'the new identity wiped Alice\'s partition').toEqual([]);

    await server.unreachable();
    await expectClient(page, wallPath(world));
    await expect(page.locator('.bw-client-store')).toHaveAttribute('data-bw-store', 'ready', { timeout: 15000 });
    const snap = await page.evaluate(() => window.bwClientStore.snapshot());
    expect(snap.partition).toBe(kiosk);
    expect(snap.walls.map(w => w.wallId)).toEqual([world.walls.alpha.id]);
    expect(snap.mine).toEqual([]);
});

test("a 500 is the server's own answer, never the client", async ({ page, world, server }) => {
    expect(await precacheAsAlice(page, world, server)).toBe('complete');
    await server.faults([{ path: '^/walls/', status: 500 }]);
    const response = await page.goto(wallPath(world));
    expect(response.status()).toBe(500);
    await expect(page).toHaveURL(new RegExp(`${wallPath(world)}$`));
});

test('an announced deploy keeps offline.html while the server is down; the hold is the page telling the worker', async ({ page, world, server }) => {
    expect(await precacheAsAlice(page, world, server)).toBe('complete');
    // What MaintenanceWatcher does when the deploy hook announces (about 5 s before the restart).
    await page.evaluate(() => window.bwMaintenance.begin({ message: 'Updating Blocwerk' }));
    const hold = () => page.evaluate(async () => {
        const hit = await caches.match('/__bw/deploy', { cacheName: 'bw-sw-state' });
        return hit ? Number(await hit.text()) - Date.now() : null;
    });
    await expect.poll(hold).toBeGreaterThan(170 * 1000);
    expect(await hold()).toBeLessThanOrEqual(180 * 1000);

    await server.unreachable();
    await expectOfflinePage(page, wallPath(world));
    await server.faults([{ path: '^/walls/', status: 502 }]);
    const response = await page.goto(wallPath(world));
    expect(response.status()).toBe(502);
});
