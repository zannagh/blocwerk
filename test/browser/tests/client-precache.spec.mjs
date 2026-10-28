// When the offline client is precached (Phase 3 PR 3.9): never with Save-Data, never from a shell that fails, one
// version at a time with the old one kept until the new one is complete, and only once the page's worker can do it.
// The released worker (main, frozen in ../released/) answers sw-info without a `client` field: a page of this build
// leaves it alone, and after the worker's update the next page precaches.
import fs from 'node:fs';
import { test, expect, controlled, settled } from '../harness/fixtures.mjs';
import { declareRoutes, precacheAsAlice, precacheOutcome, clientRecord, clientCaches, pageVersion, expectClient, expectOfflinePage } from '../harness/client.mjs';

const wallPath = world => `/walls/${world.walls.alpha.id}`;
const released = name => fs.readFileSync(new URL(`../released/${name}`, import.meta.url), 'utf8');
const releasedWorker = () => [
    { path: '^/js/service-worker\\.js$', body: released('service-worker.js'), headers: { 'content-type': 'text/javascript', 'service-worker-allowed': '/' } },
    { path: '^/js/sw-asset-cache\\.js$', body: released('sw-asset-cache.js'), headers: { 'content-type': 'text/javascript' } }
];

// The shell this page's controller reports (sw-info), or null when it does not answer in time.
const controllerShell = page => page.evaluate(() => new Promise(resolve => {
    const controller = navigator.serviceWorker.controller;
    if (!controller) {
        resolve(null);
        return;
    }
    const channel = new MessageChannel();
    channel.port1.onmessage = e => resolve(e.data && e.data.shell);
    controller.postMessage({ proto: 1, type: 'sw-info' }, [channel.port2]);
    setTimeout(() => resolve(null), 2000);
}));

// Asks for an update and waits until this build's worker controls the page. Not a controllerchange listener: a
// navigation before it may already have run the update (a navigation checks for one), and then no event comes.
//
// Chromium (macOS and Linux) now and then keeps the new worker `installed` behind its skipWaiting for minutes: the
// released worker still has work in flight that this test could not pin down (no request is open at the server, and
// instrumenting the released worker made it go away). A browser ends that by stopping the old worker (an event's
// 5 min limit, or the app's next start); the test stops it through DevTools after 10 s instead.
async function updateWorker(page, browserName) {
    await page.evaluate(() => navigator.serviceWorker.getRegistration('/').then(reg => reg.update()).catch(() => undefined));
    try {
        await expect.poll(() => controllerShell(page), { timeout: 10000 }).toBe('blocwerk-shell-v5');
        return;
    } catch (e) {
        if (browserName !== 'chromium') {
            throw e;
        }
    }
    await stopActiveWorker(page);
    await expect.poll(() => controllerShell(page), { timeout: 20000 }).toBe('blocwerk-shell-v5');
}

// Chromium only: stops the registration's active (released) worker, so the waiting one activates.
async function stopActiveWorker(page) {
    const cdp = await page.context().newCDPSession(page);
    const versions = new Map();
    cdp.on('ServiceWorker.workerVersionUpdated', ({ versions: list }) => list.forEach(v => versions.set(v.versionId, v)));
    await cdp.send('ServiceWorker.enable');
    await expect.poll(() => [...versions.values()].some(v => v.status === 'activated')).toBe(true);
    for (const v of versions.values()) {
        if (v.status === 'activated') {
            await cdp.send('ServiceWorker.stopWorker', { versionId: v.versionId });
        }
    }
    await cdp.detach();
}

// Pretends a later attempt: the page waits 10 minutes after a failed one.
const forgetAttempt = page => page.evaluate(() => localStorage.removeItem('bw-client-precache'));

test('with Save-Data on, nothing is precached and a failed navigation gets offline.html', async ({ page, world, server }) => {
    await page.addInitScript(() => {
        Object.defineProperty(navigator, 'connection', { configurable: true, value: { saveData: true, type: 'wifi', effectiveType: '4g', addEventListener() {} } });
    });
    expect(await precacheAsAlice(page, world, server)).toBe('skipped');
    expect(await clientCaches(page)).toEqual([]);
    expect(await clientRecord(page)).toBeNull();
    await server.unreachable();
    await expectOfflinePage(page, wallPath(world));
});

test('a shell that fails leaves the worker working and the client unrecorded', async ({ page, world, server }) => {
    // 'waiting' when the install's first document, already controlled, made the failed attempt.
    expect(await precacheAsAlice(page, world, server, [{ path: '^/v/$', status: 500 }])).toMatch(/^(failed|waiting)$/);
    expect(await clientRecord(page)).toBeNull();
    const info = await page.evaluate(() => new Promise(resolve => {
        const channel = new MessageChannel();
        channel.port1.onmessage = e => resolve(e.data);
        navigator.serviceWorker.controller.postMessage({ proto: 1, type: 'sw-info' }, [channel.port2]);
    }));
    expect(info).toMatchObject({ shell: 'blocwerk-shell-v5', client: null });
    await server.unreachable();
    await expectOfflinePage(page, wallPath(world));
});

test('a new version replaces the old one only once it is complete, and the old one serves until then', async ({ page, world, server }) => {
    expect(await precacheAsAlice(page, world, server)).toBe('complete');
    const v1 = await pageVersion(page);
    const v2 = v1.split('').reverse().join('');

    // The next deploy: a new version on the Server pages and in the shell, whose shell first names a file that is not
    // there yet (the others are the same immutable files, which the HTTP cache answers).
    const bump = [v1, v2];
    const missing = ['</body>', '<script src="/v/js/next-deploy.0000000000.js"></script></body>'];
    const deploy = failing => server.faults([
        declareRoutes(undefined, failing ? [bump, missing] : [bump]),
        { path: '^/walls/', rewrite: bump }
    ]);
    await deploy(true);
    await forgetAttempt(page);
    await page.reload();
    await controlled(page);
    await settled(page);
    expect(await pageVersion(page)).toBe(v2);
    expect(await precacheOutcome(page)).toBe('failed');
    expect((await clientRecord(page)).version).toBe(v1);
    expect(await clientCaches(page)).toEqual([`blocwerk-client-${v1}`, `blocwerk-client-${v2}`].sort());

    await server.unreachable();
    await expectClient(page, wallPath(world));
    expect(await page.locator('meta[name="bw-client-version"]').getAttribute('content')).toBe(v1);

    await deploy(false);
    await forgetAttempt(page);
    await page.goto(wallPath(world));
    await controlled(page);
    await settled(page);
    expect(await precacheOutcome(page)).toBe('complete');
    expect((await clientRecord(page)).version).toBe(v2);
    expect(await clientCaches(page)).toEqual([`blocwerk-client-${v2}`]);
});

test('under the released worker nothing is precached, and a failed navigation gets offline.html', async ({ page, world, server }) => {
    expect(await precacheAsAlice(page, world, server, [...releasedWorker(), declareRoutes()])).toBe('unsupported');
    expect(await clientCaches(page)).toEqual([]);
    expect(await page.evaluate(() => window.bwSwGuard.first)).toBe('alive');
    await server.unreachable();
    await expectOfflinePage(page, wallPath(world));
});

// A separate test from the outage above: on Linux Chromium, a released worker that went through it can keep work in
// flight that never settles, and the new worker then waits behind skipWaiting for minutes (a reload does not end it).
// An installed app is updated once that work ends or the app restarts; the update itself is what this test covers.
test('after the released worker updates, the next page precaches and hands off', async ({ page, world, server, browserName }) => {
    expect(await precacheAsAlice(page, world, server, [...releasedWorker(), declareRoutes()])).toBe('unsupported');

    // The deploy reaches the installed app: this build's worker replaces the released one in the open tab.
    await server.faults([declareRoutes()]);
    await updateWorker(page, browserName);
    await page.reload();
    await controlled(page);
    await settled(page);
    expect(await precacheOutcome(page)).toBe('complete');
    expect((await page.evaluate(() => caches.keys())).filter(n => n.startsWith('blocwerk-shell-'))).toEqual(['blocwerk-shell-v5']);

    await server.unreachable();
    await expectClient(page, wallPath(world));
});

test('a record whose cache is gone hands nothing off, and the next page precaches again', async ({ page, world, server }) => {
    expect(await precacheAsAlice(page, world, server)).toBe('complete');
    const version = await pageVersion(page);
    // What a worker update racing a fill, or the browser's eviction, leaves: the record without its cache.
    await page.evaluate(v => caches.delete(`blocwerk-client-${v}`), version);
    expect((await clientRecord(page)).version).toBe(version);

    await server.unreachable();
    await expectOfflinePage(page, wallPath(world));

    await server.faults([declareRoutes()]);
    await page.goto(wallPath(world));
    await controlled(page);
    await settled(page);
    expect(await precacheOutcome(page)).toBe('complete');
    expect(await clientCaches(page)).toEqual([`blocwerk-client-${version}`]);
    await server.unreachable();
    await expectClient(page, wallPath(world));
});
