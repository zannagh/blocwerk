// The offline client's sync loop (Phase 3 PR 3.12) on the real engines and the real feed: on a wall's /v/ page the
// client reloads the docs index and the marks, opens the wall's EventSource and stays live; a comment written on
// another device reaches this device's comments document (the debounced index refetch, the new document fetched and
// cached under the partition); the server going away closes the stream, and the client probes, backs off, and is live
// again once the server is back.
import { test, expect, signIn, ledger, partitionOf } from '../harness/fixtures.mjs';
import { precacheAsAlice } from '../harness/client.mjs';

const syncState = page => page.locator('.bw-client-sync');

// Opens the client on Alpha's /v/ page (served by the worker from the precached version) and waits for its first render.
async function openClient(page, world) {
    await page.goto(`/v/walls/${world.walls.alpha.id}`);
    await expect.poll(() => page.evaluate(() => performance.getEntriesByName('bw-client-ready').length), { timeout: 30000 }).toBe(1);
}

// Same as kiosk-readonly-social.spec.mjs: type only into the interactive boulder page.
async function trackInteractive(context) {
    await context.addInitScript(() => {
        let actions;
        Object.defineProperty(window, 'blocwerkOfflineActions', {
            configurable: true,
            get: () => actions,
            set: value => {
                const register = value.registerRefresh;
                value.registerRefresh = function (...args) {
                    window.__bwBoulderInteractive = true;
                    return register.apply(this, args);
                };
                actions = value;
            },
        });
    });
}

test('the client keeps its wall live, and a comment from another device reaches this device\'s comments document', async ({ page, browser, baseURL, world, server }) => {
    test.setTimeout(90000);
    expect(await precacheAsAlice(page, world, server)).toBe('complete');
    const alice = await partitionOf(page);
    const wallId = world.walls.alpha.id;
    const streams = [];
    page.on('request', request => { if (request.url().includes('/feed/stream')) { streams.push(request.url()); } });

    await openClient(page, world);
    await expect(syncState(page)).toHaveAttribute('data-bw-sync', 'live', { timeout: 20000 });
    expect(streams.length).toBeGreaterThan(0);
    expect(streams[0]).toContain(`/api/walls/${wallId}/feed/stream`);
    const commentsBefore = (await ledger(page, alice)).walls.find(w => w.wallId === wallId).data.comments.sha;

    const phone = await browser.newContext({ baseURL });
    try {
        await trackInteractive(phone);
        const other = await phone.newPage();
        const boulderPage = `/walls/${wallId}/boulders/${world.walls.alpha.boulder}`;
        await signIn(other, world.users.alice, boulderPage);
        await other.waitForFunction(() => window.__bwBoulderInteractive === true);
        const text = `Synced comment ${Date.now()}`;
        await other.locator('.comment-input').fill(text);
        await other.locator('.comment-submit').click();
        await expect(other.locator('.comments-section')).toContainText(text);

        // comment.added → the index 10 s after the burst began → the new comments document, read back from the device.
        await expect.poll(async () => (await ledger(page, alice)).walls.find(w => w.wallId === wallId).data.comments.sha, { timeout: 30000 })
            .not.toBe(commentsBefore);
        await expect.poll(async () => {
            const read = await page.evaluate(([p, w]) => window.bwClientStore.docs(p, w), [alice, wallId]);
            return read && read.docs.comments ? read.docs.comments.text : '';
        }, { timeout: 15000 }).toContain(text);
        await expect(syncState(page)).toHaveAttribute('data-bw-sync', 'live');
    } finally {
        await phone.close();
    }
});

test('the server going away closes the stream; the client probes, backs off, and is live again once it is back', async ({ page, world, server }) => {
    test.setTimeout(120000);
    expect(await precacheAsAlice(page, world, server)).toBe('complete');
    await openClient(page, world);
    await expect(syncState(page)).toHaveAttribute('data-bw-sync', 'live', { timeout: 20000 });

    await server.unreachable();
    await expect(syncState(page)).toHaveAttribute('data-bw-sync', 'catching-up', { timeout: 20000 });
    // Still the client, still showing the device's data.
    await expect(page.locator('.bw-client-store')).toHaveAttribute('data-bw-store', 'ready');

    // Back up: the next network retry lands within the backoff (5 s, then 30 s, each ±50 %), then the stream reopens.
    await server.faults([]);
    await expect(syncState(page)).toHaveAttribute('data-bw-sync', 'live', { timeout: 60000 });
});

test('a wall the device holds nothing of opens no stream', async ({ page, world, server }) => {
    expect(await precacheAsAlice(page, world, server)).toBe('complete');
    const fetched = [];
    page.on('request', request => { if (request.url().includes(world.walls.bobs.id)) { fetched.push(request.url()); } });

    await page.goto(`/v/walls/${world.walls.bobs.id}`);
    await expect.poll(() => page.evaluate(() => performance.getEntriesByName('bw-client-ready').length), { timeout: 30000 }).toBe(1);
    await expect(page.locator('.bw-client-store')).toHaveAttribute('data-bw-store', 'ready');

    await expect(syncState(page)).toHaveAttribute('data-bw-sync', 'idle');
    expect(fetched.filter(url => !url.includes('/v/'))).toEqual([]);
});
