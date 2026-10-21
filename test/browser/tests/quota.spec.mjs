// A refused write (§6.10, PR 2.6b "storage refused", PR 2.7 fallback): the page still gets the bytes from the network,
// nothing is stored or announced, and the 2.7 fill stops and streams. Chromium only: the quota is forced through the
// DevTools protocol (Storage.overrideQuotaForOrigin), which WebKit does not offer.
import { test, expect, signIn, installWorker, assetKeys, allKeys, assetPath, pageFetch, hasPageModules, settled } from '../harness/fixtures.mjs';

test.beforeEach(async ({ browserName }) => {
    test.skip(browserName !== 'chromium', 'the quota override is a Chromium DevTools command');
});

// A quota of one byte: the origin already uses more (the shell cache), so every further write is refused.
async function refuseStorage(context, page, baseURL) {
    const cdp = await context.newCDPSession(page);
    await cdp.send('Storage.overrideQuotaForOrigin', { origin: baseURL, quotaSize: 1 });
}

function collectAnnouncements(page) {
    return page.evaluate(() => {
        window.__bwAnnounced = [];
        navigator.serviceWorker.addEventListener('message', e => {
            if (e.data && e.data.type === 'asset-cached') {
                window.__bwAnnounced.push(e.data.sha);
            }
        });
    });
}

test.describe('the worker alone', () => {
    test.use({ workerOnly: true });

    test('a refused put still serves the network response and stores and announces nothing', async ({ page, context, world, baseURL }) => {
        await signIn(page, world.users.alice);
        await installWorker(page);
        await refuseStorage(context, page, baseURL);
        await collectAnnouncements(page);

        const wall = world.walls.alpha;
        const response = await pageFetch(page, assetPath(wall.id, wall.panels[0], 1280));
        expect(response).toMatchObject({ status: 200, type: 'image/jpeg' });
        expect(response.bytes).toBeGreaterThan(1000);
        // Give the worker's waitUntil(put) time to fail, then check nothing landed.
        await page.waitForTimeout(1000);
        expect(allKeys(await assetKeys(page))).not.toContain(`${wall.panels[0]}/1280`);
        expect(await page.evaluate(() => window.__bwAnnounced)).toEqual([]);
    });
});

test('with the page modules, a refused fill retries once, then streams for the page\'s life', async ({ page, context, world, baseURL }) => {
    await signIn(page, world.users.alice);
    await installWorker(page);
    test.skip(!(await hasPageModules(page)), 'needs the 2.7 page modules');
    await settled(page);
    // Start from an empty device, then refuse every write and load again.
    await page.evaluate(async () => {
        for (const name of (await caches.keys()).filter(n => n.startsWith('bw-a1-'))) {
            await caches.delete(name);
        }
    });
    await refuseStorage(context, page, baseURL);
    await page.reload();
    await settled(page);

    const state = await page.evaluate(() => ({
        streaming: !!(window.bwAssetCache.state().runner && window.bwAssetCache.state().runner.streaming),
        capable: window.bwAssetCache.state().capable
    }));
    expect(state).toEqual({ streaming: true, capable: true });
    expect(allKeys(await assetKeys(page))).toEqual([]);
    // The page itself is unaffected: its own asset requests still load from the network.
    const wall = world.walls.beta;
    expect((await pageFetch(page, assetPath(wall.id, wall.panels[0], 640))).status).toBe(200);
});
