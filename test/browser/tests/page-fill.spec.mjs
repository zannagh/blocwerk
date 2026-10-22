// The 2.7 page modules in a real browser: the background fill, the 3D opt-out, and how manifest failures treat a
// wall (rule 4 drops on 403/404; anything else keeps the stored copy). Skipped on a build without the modules.
import { test, expect, signIn, installWorker, partitionOf, assetKeys, allKeys, hasPageModules, settled, ledger } from '../harness/fixtures.mjs';

// The keys the fill must have stored for one wall's core tier, computed from the server's own manifest the way the
// fill plans them: a photo at this device's fit width plus 640, anything else as its original.
function expectedCore(page, wallId) {
    return page.evaluate(async id => {
        const manifest = await (await fetch(`/api/walls/${id}/manifest`, { cache: 'no-store' })).json();
        const plan = window.bwAssetPlan || window.bwAssetFill;
        const width = plan.fitWidth(window.innerWidth, window.devicePixelRatio);
        return manifest.assets.filter(a => a.tier === 'core' && a.sha).flatMap(a => (Array.isArray(a.widths) && a.widths.length
            ? [...new Set([width, 640])].filter(w => a.widths.includes(w)).map(w => `${a.sha}/${w}`)
            : [a.sha]));
    }, wallId);
}

async function filledAsAlice(page, world) {
    await signIn(page, world.users.alice);
    // The first visit installs the worker; only a controlled page answers sw-info, so the fill starts on the reload.
    await installWorker(page);
    test.skip(!(await hasPageModules(page)), 'needs the 2.7 page modules');
    await settled(page);
    return partitionOf(page);
}

const view3dShas = wall => [wall.texture, wall.mask, wall.splat];

test('the fill stores the core tier of every wall the viewer is a member of', async ({ page, world }) => {
    const partition = await filledAsAlice(page, world);
    const keys = allKeys(await assetKeys(page));
    for (const wall of [world.walls.alpha, world.walls.beta]) {
        const core = await expectedCore(page, wall.id);
        expect(core.length).toBeGreaterThan(0);
        expect(keys).toEqual(expect.arrayContaining(core));
    }
    expect(keys).not.toEqual(expect.arrayContaining([`${world.walls.bobs.panels[0]}/640`]));

    const book = await ledger(page, partition);
    expect(book.kind).toBe('idb');
    expect(book.walls.map(w => w.wallId).sort()).toEqual([world.walls.alpha.id, world.walls.beta.id].sort());
    expect(book.meta.activePartition).toBe(partition);
});

test('3D is filled by default: textures and masks as images, one splat level in -3d', async ({ page, world }) => {
    const partition = await filledAsAlice(page, world);
    const byCache = await assetKeys(page);
    const alpha = world.walls.alpha;
    expect(byCache[`bw-a1-${partition}-img`]).toEqual(expect.arrayContaining([alpha.texture, alpha.mask]));
    expect(byCache[`bw-a1-${partition}-3d`]).toEqual([alpha.splat]);
});

test('the 3D opt-out fills core but no view3d asset', async ({ page, context, world, baseURL }) => {
    await context.addCookies([{ name: 'blocwerk-cache-3d', value: 'off', url: baseURL }]);
    await filledAsAlice(page, world);
    const keys = allKeys(await assetKeys(page));
    expect(keys).toEqual(expect.arrayContaining(await expectedCore(page, world.walls.alpha.id)));
    for (const sha of view3dShas(world.walls.alpha)) {
        expect(keys).not.toContain(sha);
    }
});

test('a 404 manifest drops the wall and its own assets; the other wall stays', async ({ page, world, server }) => {
    const partition = await filledAsAlice(page, world);
    const { alpha, beta } = world.walls;
    const betaCore = await expectedCore(page, beta.id);
    expect(allKeys(await assetKeys(page))).toEqual(expect.arrayContaining(betaCore));

    await server.faults([{ path: `^/api/walls/${beta.id}/manifest$`, status: 404 }]);
    await page.reload();
    await settled(page);

    const keys = allKeys(await assetKeys(page));
    for (const key of betaCore) {
        expect(keys).not.toContain(key);
    }
    expect(keys).toEqual(expect.arrayContaining(await expectedCore(page, alpha.id)));
    expect((await ledger(page, partition)).walls.map(w => w.wallId)).toEqual([alpha.id]);
});

test('a transient 5xx manifest keeps the wall and its assets', async ({ page, world, server }) => {
    const partition = await filledAsAlice(page, world);
    const beta = world.walls.beta;
    const betaCore = await expectedCore(page, beta.id);

    await server.faults([{ path: `^/api/walls/${beta.id}/manifest$`, status: 503 }]);
    await page.reload();
    await settled(page);

    expect(allKeys(await assetKeys(page))).toEqual(expect.arrayContaining(betaCore));
    const row = (await ledger(page, partition)).walls.find(w => w.wallId === beta.id);
    expect(row).toBeTruthy();
    expect(row.entries.length).toBeGreaterThan(0);
});

async function aliceThenBob(page, world) {
    const alice = await filledAsAlice(page, world);
    // A sign-out the page modules never saw (DeleteAccount's forced navigation, or a server-forced sign-out).
    await page.goto('/account/logout');
    await expect(page.locator('meta[name="bw-partition"]')).toHaveAttribute('content', 'anon');
    await signIn(page, world.users.bob);
    await page.evaluate(() => window.bwAssetCache.ready);
    return { alice, bob: await partitionOf(page) };
}

test('a page in another partition wipes the previous one, without the Logout link', async ({ page, world }) => {
    const { alice } = await aliceThenBob(page, world);
    expect(Object.keys(await assetKeys(page)).filter(n => n.startsWith(`bw-a1-${alice}-`))).toEqual([]);
    expect((await ledger(page, alice)).walls).toEqual([]);
});

test('after an identity switch, the next identity\'s first page fills even with no asset image on it', async ({ page, world }) => {
    // Bob lands on /walls, whose cards here carry no photo, so no /a/ request tells the worker Bob's label. The first
    // run sees the worker on Alice's label (`mismatch`), asks it to look again (sw-refresh-partition) and waits for its
    // `partition-refreshed`: Bob's label, so the same run fills, with no 30 s backoff.
    const { bob } = await aliceThenBob(page, world);
    const core = await expectedCore(page, world.walls.bobs.id);
    await expect.poll(async () => (await assetKeys(page))[`bw-a1-${bob}-img`] || [], { timeout: 10000 })
        .toEqual(expect.arrayContaining(core));
});

test('a server page without the partition meta wipes every partition (rule 1: missing means wipe)', async ({ page, world, server }) => {
    const alice = await filledAsAlice(page, world);
    expect(Object.keys(await assetKeys(page))).toContain(`bw-a1-${alice}-img`);

    await server.faults([{ path: '^/walls$', rewrite: ['name="bw-partition"', 'name="bw-partition-removed"'] }]);
    await page.goto('/walls');
    await expect(page.locator('meta[name="bw-partition-removed"]')).toHaveCount(1);
    await page.evaluate(() => window.bwAssetCache.ready);

    expect(Object.keys(await assetKeys(page))).toEqual([]);
    expect((await ledger(page, alice)).meta.activePartition).toBe('none');
});
