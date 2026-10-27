// The offline client's 3D view (Phase 3 PR 3.13) on the real engines. A Server page's 3D visit is planted as it leaves
// the device: a view3d document of Alpha in the partition's -3d cache and listed in the wall row's `docs`, its texture,
// mask and splat level fetched through the worker (which caches them). Then: offline, the 3D route opens in the client
// from the device alone, in Schematic, and photo-real waits for the client's first ~20 s (shortened here); a desktop
// precaches the 3D code, a phone and the 3D opt-out do not; on a phone nothing 3D loads until the viewer opens 3D, its
// first online open fetches the 3D code (never a 3D asset) and stores it, and 3D then opens offline; the 3D opt-out
// shows no 3D; a model not cached in full says so and loads no 3D code.
import { test, expect, assetPath, partitionOf } from '../harness/fixtures.mjs';
import { precacheAsAlice, expectClient } from '../harness/client.mjs';

const DOC = '3d'.repeat(32);
const HOLD_MS = 6000;
const IPHONE = 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1';
const THREE_D = /\/js\/wall3d|\/lib\/(three|spark)\/|\/d\/view3d\//;

// A small Wall3DView of Alpha as View3DDocument serialises it: one facet, two holds, its texture and mask, one splat level.
function view3d(wall) {
    const extent = { aMin: 0, aMax: 3000, bMin: 0, bMax: 2500 };
    const facet = { id: 'F1', segment: 0, name: 'main wall', origin: [0, 0, 0], u: [1, 0, 0], v: [0, 0, 1], normal: [0, -1, 0], extent, angleDeg: 0,
        corners: [[0, 0, 0], [3000, 0, 0], [3000, 0, 2500], [0, 0, 2500]] };
    const hold = (id, a, b) => ({ id, facetId: 'F1', position: [a, -12, b], planeA: a, planeB: b, widthMm: 80, heightMm: 60, sizeMeasured: true,
        color: 'red', colorName: 'Red', hex: '#cc3333', isFoot: false, usageCount: 1, role: null, shape: null });
    return {
        wallId: wall.id, wallName: 'Alpha', boulderId: null, boulderName: null, markerSizeMm: 125, facets: [facet], markers: [],
        holds: [hold('h1', 800, 900), hold('h2', 1500, 1600)], unplacedHoldCount: 0,
        textures: [{ facetId: 'F1', url: assetPath(wall.id, wall.texture), maskUrl: assetPath(wall.id, wall.mask), bounds: extent }],
        splatUrl: null, splatMobileUrl: null, splatLevels: [{ url: assetPath(wall.id, wall.splat), splats: 1000, sizeBytes: 4096 }],
        splatMatrix: [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1],
    };
}

/** What a Server page's 3D visit leaves on the device; `skip` leaves those asset shas out. Returns the partition. */
async function plant3d(page, wall, skip = []) {
    const partition = await partitionOf(page);
    await page.evaluate(async ([p, wallId, doc, sha, urls]) => {
        for (const url of urls) {
            await (await fetch(url)).arrayBuffer();
        }
        const cache = await caches.open(`bw-a1-${p}-3d`);
        await cache.put(`${location.origin}/__bw/${sha}`, new Response(JSON.stringify(doc), { headers: { 'content-type': 'application/json' } }));
        const ledger = await window.bwAssetLedger.open();
        await ledger.updateWall(p, wallId, row => Object.assign(row, { docs: [{ sha, kind: 'view3d', tier: 'view3d', bytes: 1 }] }));
    }, [partition, wall.id, view3d(wall), DOC, [wall.texture, wall.mask, wall.splat].filter(s => !skip.includes(s)).map(s => assetPath(wall.id, s))]);
    for (const sha of skip) {
        await page.evaluate(async ([p, s]) => {
            for (const name of [`bw-a1-${p}-img`, `bw-a1-${p}-3d`]) {
                await (await caches.open(name)).delete(`${location.origin}/__bw/${s}`);
            }
        }, [partition, sha]);
    }
    return partition;
}

const loaded3d = page => page.evaluate(re => performance.getEntriesByType('resource').map(e => e.name).filter(n => new RegExp(re).test(n)), THREE_D.source);
// The 3D code in the complete client version: the import map's modules outside /v/ (the worker's 3D group), and how many
// of them its cache holds.
const held3d = page => page.evaluate(async () => {
    const record = await (await caches.match('/__bw/client', { cacheName: 'bw-sw-state' })).json();
    const cache = await caches.open(`blocwerk-client-${record.version}`);
    const html = await (await cache.match('/v/')).text();
    const imports = JSON.parse(/<script type="importmap"[^>]*>([\s\S]*?)<\/script>/.exec(html)[1]).imports;
    const group = Object.entries(imports).filter(([name]) => !name.startsWith('/v/')).map(([, url]) => url);
    return { group: group.length, held: (await Promise.all(group.map(url => cache.match(url)))).filter(Boolean).length };
});

const mode = page => page.evaluate(() => document.querySelector('.wall3d-stage-el')?.__wall3d?.state().mode ?? null);

test('offline, the 3D route opens from the device in Schematic, and photo-real waits for the hold', async ({ page, context, world, server }) => {
    const alpha = world.walls.alpha;
    await context.addInitScript(ms => { window.__bwClient3dHoldMs = ms; }, HOLD_MS);
    expect(await precacheAsAlice(page, world, server)).toBe('complete');
    await plant3d(page, alpha);
    await server.unreachable();
    const mark = await server.mark();

    await expectClient(page, `/walls/${alpha.id}/3d?mode=photoreal`);
    await expect(page.locator('.bw-client-3d')).toHaveAttribute('data-bw-3d', 'ready');
    await expect(page.locator('.wall3d-stage-el canvas.w3d-canvas')).toHaveCount(1);
    await expect.poll(() => mode(page)).toBe('schematic');

    // The splat is never asked for before the hold ends: the first render plus HOLD_MS.
    const readyAt = await page.evaluate(() => performance.getEntriesByName('bw-client-ready')[0].startTime);
    const splats = () => page.evaluate(sha => performance.getEntriesByType('resource').filter(e => e.name.includes(sha)).map(e => e.startTime), alpha.splat);
    while (await page.evaluate(() => performance.now()) < readyAt + HOLD_MS - 500) {
        expect(await splats()).toEqual([]);
        await page.waitForTimeout(500);
    }
    await expect.poll(splats, { timeout: 20000 }).not.toEqual([]);
    expect(Math.min(...(await splats()))).toBeGreaterThanOrEqual(readyAt + HOLD_MS);
    expect((await server.log(mark)).filter(e => !e.fault), 'nothing reached the server').toEqual([]);
});

test('a desktop precaches the 3D code with the client', async ({ page, world, server }) => {
    expect(await precacheAsAlice(page, world, server)).toBe('complete');
    const { group, held } = await held3d(page);
    expect(group).toBeGreaterThan(40);
    expect(held).toBe(group);
});

test('with the 3D opt-out the precache leaves the 3D code out', async ({ page, context, world, server, baseURL }) => {
    await context.addCookies([{ name: 'blocwerk-cache-3d', value: 'off', url: baseURL }]);
    expect(await precacheAsAlice(page, world, server)).toBe('complete');
    expect((await held3d(page)).held).toBe(0);
});

test('on a phone nothing 3D is precached or loads until 3D opens; the first online open stores the 3D code, then 3D opens offline', async ({ page, context, world, server }) => {
    const alpha = world.walls.alpha;
    await context.addInitScript(ua => Object.defineProperty(navigator, 'userAgent', { get: () => ua }), IPHONE);
    expect(await precacheAsAlice(page, world, server)).toBe('complete');
    expect((await held3d(page)).held, 'no 3D code in the precache').toBe(0);
    await plant3d(page, alpha);
    const mark = await server.mark();

    // Online: the client's wall view and boulder detail load no 3D file and ask the server for none.
    await page.goto(`/v/walls/${alpha.id}`);
    await expect.poll(() => page.evaluate(() => performance.getEntriesByName('bw-client-ready').length), { timeout: 30000 }).toBe(1);
    await expect(page.locator('.bw-client-3d-switch')).toBeVisible();
    await page.locator(`.boulder-card[data-bw-boulder="${alpha.boulder}"]`).click();
    await expect(page.locator('.boulder-detail .bw-client-3d-switch')).toBeVisible();
    expect(await loaded3d(page)).toEqual([]);

    // Opened online: the 3D code comes from the network this once, the model from the device only; then the worker
    // holds the whole 3D group.
    await page.locator('.bw-client-3d-switch').click();
    await expect(page.locator('.wall3d-stage-el canvas.w3d-canvas')).toHaveCount(1);
    expect((await loaded3d(page)).length).toBeGreaterThan(0);
    const hits = (await server.log(mark)).map(e => e.url).filter(p => /\/d\/view3d\//.test(p) || [alpha.texture, alpha.mask, alpha.splat].some(s => p.includes(s)));
    expect(hits, 'no 3D asset from the network').toEqual([]);
    await expect.poll(async () => { const h = await held3d(page); return h.held === h.group; }, { timeout: 30000 }).toBe(true);

    await page.locator('.bw-client-3d-switch').click();
    await expect(page.locator('.boulder-detail .panel-viewer')).toBeVisible();

    // Offline later: the 3D route opens from the device, code included.
    await server.unreachable();
    const offline = await server.mark();
    await expectClient(page, `/walls/${alpha.id}/3d`);
    await expect(page.locator('.bw-client-3d')).toHaveAttribute('data-bw-3d', 'ready');
    await expect(page.locator('.wall3d-stage-el canvas.w3d-canvas')).toHaveCount(1);
    expect((await server.log(offline)).filter(e => !e.fault), 'nothing reached the server').toEqual([]);
});

test('with the 3D opt-out, the client offers no 3D and the 3D route says it is off', async ({ page, context, world, server, baseURL }) => {
    const alpha = world.walls.alpha;
    expect(await precacheAsAlice(page, world, server)).toBe('complete');
    await plant3d(page, alpha);
    await context.addCookies([{ name: 'blocwerk-cache-3d', value: 'off', url: baseURL }]);
    await server.unreachable();

    await expectClient(page, `/walls/${alpha.id}`);
    await expect(page.locator('h1')).toHaveText('Alpha');
    await expect(page.locator('.bw-client-3d-switch')).toHaveCount(0);

    await expectClient(page, `/walls/${alpha.id}/3d`);
    await expect(page.locator('.bw-client-3d')).toHaveAttribute('data-bw-3d', 'off');
    await expect(page.locator('.bw-client-3d')).toContainText('3D is turned off on this device');
    expect(await loaded3d(page)).toEqual([]);
});

test('a model not cached in full is "not saved", with the Server page, and loads no 3D code', async ({ page, world, server }) => {
    const alpha = world.walls.alpha;
    expect(await precacheAsAlice(page, world, server)).toBe('complete');
    await plant3d(page, alpha, [alpha.mask]);
    await server.unreachable();

    await expectClient(page, `/walls/${alpha.id}/3d`);
    await expect(page.locator('.bw-client-3d')).toHaveAttribute('data-bw-3d', 'missing');
    await expect(page.locator('.bw-client-3d a.bw-client-online')).toHaveAttribute('href', `/walls/${alpha.id}/3d`);
    expect(await loaded3d(page)).toEqual([]);
});
