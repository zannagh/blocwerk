// Sign-out on the device: the signed-out page announces bw:identity-changed (PR 2.5 W1), and with the 2.7 page
// modules the header's Logout link wipes the asset cache before it leaves (rule 2), under a live Blazor page.
import { test, expect, signIn, installWorker, partitionOf, cacheNames, hasPageModules, settled, ledger, assetKeys } from '../harness/fixtures.mjs';

// Every bw:identity-changed, with the path of the document that fired it, kept across this tab's navigations.
async function recordIdentityEvents(page) {
    await page.addInitScript(() => {
        window.addEventListener('bw:identity-changed', () => {
            const seen = JSON.parse(sessionStorage.getItem('bw-test-identity') || '[]');
            seen.push(location.pathname);
            sessionStorage.setItem('bw-test-identity', JSON.stringify(seen));
        });
    });
}

const identityEvents = page => page.evaluate(() => JSON.parse(sessionStorage.getItem('bw-test-identity') || '[]'));

async function logOutByLink(page) {
    const signedOut = page.waitForResponse(r => new URL(r.url()).pathname === '/account/logout');
    await page.getByRole('link', { name: 'Logout' }).click();
    expect((await signedOut).status()).toBe(200);
    // The signed-out page moves on by location.replace; the page it lands on is anonymous.
    await expect(page.locator('meta[name="bw-partition"]')).toHaveAttribute('content', 'anon');
    await expect(page.getByRole('link', { name: 'Logout' })).toHaveCount(0);
    // Settled on the landing page with its modules, not on a document still on its way there.
    await page.waitForLoadState('load');
    await page.waitForFunction(() => location.pathname !== '/account/logout' && !!window.bwAssetCache && !!window.bwAssetLedger);
    await page.evaluate(() => window.bwAssetCache.ready);
}

test('the signed-out page fires bw:identity-changed and moves on signed out', async ({ page, world }) => {
    await recordIdentityEvents(page);
    await signIn(page, world.users.alice);
    await installWorker(page);
    await logOutByLink(page);
    expect(await identityEvents(page)).toContain('/account/logout');
    expect(await partitionOf(page)).toBe('anon');
});

test('the Logout link wipes the signed-in partition first, and the navigation still happens (every later wipe live)', async ({ page, world }) => {
    await recordIdentityEvents(page);
    await signIn(page, world.users.alice);
    await installWorker(page);
    test.skip(!(await hasPageModules(page)), 'needs the 2.7 page modules');
    await settled(page);
    const alice = await partitionOf(page);
    expect((await assetKeys(page))[`bw-a1-${alice}-img`] || []).not.toHaveLength(0);
    const before = await ledger(page, alice);
    expect(before.kind).toBe('idb');
    expect(before.walls.length).toBeGreaterThan(0);
    const from = new URL(page.url()).pathname;

    await logOutByLink(page);

    // Fired twice: by the click interception on the page that was left, and by the signed-out page.
    expect(await identityEvents(page)).toEqual(expect.arrayContaining([from, '/account/logout']));
    expect((await cacheNames(page)).filter(n => n.startsWith(`bw-a1-${alice}-`))).toEqual([]);
    const after = await ledger(page, alice);
    expect(after.walls).toEqual([]);
    expect(after.assets).toEqual([]);
    expect(after.meta.activePartition).toBe('anon');
});

// The documents after the click (the signed-out page, the anonymous landing page) with their wipes made inert: the asset
// modules never boot and never get their globals, so rule 1, rule 6 and the signed-out page's own wipe cannot run.
// Armed by a sessionStorage flag the test sets just before the click, so the page that is left keeps its modules.
async function inertAfterClick(page) {
    await page.addInitScript(() => {
        if (sessionStorage.getItem('bw-test-inert') !== '1') {
            return;
        }
        window.__bwAssetCacheManual = true;
        for (const name of ['bwAssetWipe', 'bwAssetLedger', 'bwAssetCache']) {
            Object.defineProperty(window, name, { configurable: false, get: () => undefined, set: () => undefined });
        }
    });
}

// The ledger read straight from IndexedDB, since the inert pages have no bwAssetLedger.
function rawLedger(page) {
    return page.evaluate(() => new Promise((resolve, reject) => {
        const open = indexedDB.open('blocwerk-assets');
        open.onerror = () => reject(open.error);
        open.onsuccess = () => {
            const db = open.result;
            const all = store => new Promise((res, rej) => {
                const req = db.transaction(store, 'readonly').objectStore(store).getAll();
                req.onsuccess = () => res(req.result);
                req.onerror = () => rej(req.error);
            });
            Promise.all([all('walls'), all('assets'), all('meta')]).then(([walls, assets, meta]) => {
                db.close();
                resolve({ walls, assets, meta });
            }, reject);
        };
    }));
}

test('the Logout link itself wipes before it leaves, with every later wipe made inert', async ({ page, world }) => {
    await inertAfterClick(page);
    await signIn(page, world.users.alice);
    await installWorker(page);
    test.skip(!(await hasPageModules(page)), 'needs the 2.7 page modules');
    await settled(page);
    const alice = await partitionOf(page);
    expect((await assetKeys(page))[`bw-a1-${alice}-img`] || []).not.toHaveLength(0);
    expect((await rawLedger(page)).walls.length).toBeGreaterThan(0);

    await page.evaluate(() => sessionStorage.setItem('bw-test-inert', '1'));
    const signedOut = page.waitForResponse(r => new URL(r.url()).pathname === '/account/logout');
    await page.getByRole('link', { name: 'Logout' }).click();
    expect((await signedOut).status()).toBe(200);
    await expect(page.locator('meta[name="bw-partition"]')).toHaveAttribute('content', 'anon');
    await page.waitForLoadState('load');
    expect(await page.evaluate(() => window.bwAssetWipe)).toBeUndefined();

    // Only the click's wipe could have done this, and it ran before the browser left the page.
    expect((await cacheNames(page)).filter(n => n.startsWith('bw-a1-'))).toEqual([]);
    const after = await rawLedger(page);
    expect(after.walls).toEqual([]);
    expect(after.assets).toEqual([]);
    expect(after.meta.find(m => m.id === 'meta')?.activePartition ?? null).toBeNull();
});

