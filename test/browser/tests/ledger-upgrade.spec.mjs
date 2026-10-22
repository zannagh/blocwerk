// The ledger's IndexedDB v1 → v2 upgrade (Phase 3 PR 3.3) on the real engines: a Release 1/2 ledger upgrades in place
// on the first new page and keeps its rows, the new page fills the data documents, a Release 1/2 tab holding the
// database open makes the new page run on memory as a pending upgrade until it closes, and a new tab never blocks a
// later upgrade. Skipped on a build without the page modules.
import { test, expect, signIn, installWorker, partitionOf, assetKeys, hasPageModules, settled, ledger } from '../harness/fixtures.mjs';

const DB = 'blocwerk-assets';
const KEPT = 'f'.repeat(64);

// What a Release 1/2 page leaves behind, written with the v1 schema: meta, a kept wall row, one cached asset. The v1
// connection stays open in `window.__v1` (a Release 1/2 tab never closes it on versionchange).
function seedV1(page, partition, wallId) {
    return page.evaluate(async ({ db, partition, wallId, kept }) => {
        const conn = await new Promise((resolve, reject) => {
            const req = indexedDB.open(db, 1);
            req.onupgradeneeded = () => {
                req.result.createObjectStore('walls', { keyPath: ['partition', 'wallId'] });
                req.result.createObjectStore('assets', { keyPath: ['partition', 'key'] });
                req.result.createObjectStore('meta', { keyPath: 'id' });
            };
            req.onsuccess = () => resolve(req.result);
            req.onerror = () => reject(req.error);
        });
        await new Promise((resolve, reject) => {
            const tx = conn.transaction(['walls', 'assets', 'meta'], 'readwrite');
            tx.objectStore('meta').put({ id: 'meta', activePartition: partition, schema: 1, proto: 1 });
            tx.objectStore('walls').put({ partition, wallId, entries: [], etag: null, cursor: null, pending: 0, member: true, lastOpened: 1, fetchedAt: 1, keepOffline: true });
            tx.objectStore('assets').put({ partition, key: kept, sha: kept, width: null, cache: null, bytes: 7, cachedAt: Date.now(), verifiedAt: Date.now() });
            tx.oncomplete = resolve;
            tx.onerror = () => reject(tx.error);
        });
        // The cached copy the reconcile looks for, so the row is not a ledger row without an entry.
        await (await caches.open(`bw-a1-${partition}-img`)).put(`/__bw/${kept}`, new Response('x'));
        window.__v1 = conn;
    }, { db: DB, partition, wallId, kept: KEPT });
}

// The database's version as a plain open (no version) sees it; that connection closes at once.
const versionOf = page => page.evaluate(db => new Promise(resolve => {
    const req = indexedDB.open(db);
    req.onsuccess = () => { const v = req.result.version; req.result.close(); resolve(v); };
    req.onerror = () => resolve(null);
}), DB);

const ledgerState = page => page.evaluate(async () => {
    await window.bwAssetCache.ready;
    return { kind: (await window.bwAssetLedger.open()).kind, pending: window.bwAssetLedger.upgradePending() };
});

async function seededAsAlice(page, world) {
    await signIn(page, world.users.alice, '/__pw/blank');
    const partition = await partitionOf(page);
    await seedV1(page, partition, world.walls.alpha.id);
    return partition;
}

test('a Release 1/2 ledger upgrades in place on the first new page, and the data documents fill', async ({ page, world }) => {
    const partition = await seededAsAlice(page, world);
    await page.evaluate(() => window.__v1.close()); // the old tab is gone
    await installWorker(page);
    test.skip(!(await hasPageModules(page)), 'needs the page modules');
    await settled(page);

    expect(await versionOf(page)).toBe(2);
    const book = await ledger(page, partition);
    expect(book.kind).toBe('idb');
    expect(book.meta.schema).toBe(2);
    expect(book.meta.activePartition).toBe(partition);
    expect(book.assets.map(a => a.key)).toContain(KEPT);
    const alpha = book.walls.find(w => w.wallId === world.walls.alpha.id);
    expect(alpha.keepOffline).toBe(true);
    expect(Object.keys(alpha.data)).toEqual(expect.arrayContaining(['wall', 'holds', 'boulders']));
    const docs = (await assetKeys(page))[`bw-a1-${partition}-3d`] || [];
    expect(docs).toEqual(expect.arrayContaining(Object.values(alpha.data).map(d => d.sha)));
});

test('an open Release 1/2 tab blocks the upgrade: the new page is pending on memory, and the next load opens v2', async ({ page, context, world }) => {
    const partition = await seededAsAlice(page, world);
    const fresh = await context.newPage();
    await fresh.goto('/');
    test.skip(!(await hasPageModules(fresh)), 'needs the page modules');
    expect(await ledgerState(fresh)).toEqual({ kind: 'memory', pending: true });
    expect(await page.evaluate(() => window.__v1.version)).toBe(1);

    await page.evaluate(() => window.__v1.close()); // the old tab closes: the new page's open finishes the upgrade
    await expect.poll(() => versionOf(fresh)).toBe(2);
    await fresh.reload();
    expect(await ledgerState(fresh)).toEqual({ kind: 'idb', pending: false });
    const book = await ledger(fresh, partition);
    expect(book.walls.find(w => w.wallId === world.walls.alpha.id).keepOffline).toBe(true);
    expect(book.assets.map(a => a.key)).toContain(KEPT);
});

test('an open new tab closes on versionchange: a later version upgrades without being blocked', async ({ page, context, world }) => {
    await signIn(page, world.users.alice);
    test.skip(!(await hasPageModules(page)), 'needs the page modules');
    expect(await ledgerState(page)).toEqual({ kind: 'idb', pending: false });

    const later = await context.newPage();
    await later.goto('/__pw/blank');
    const outcome = await later.evaluate(db => new Promise(resolve => {
        let blocked = false;
        const req = indexedDB.open(db, 3);
        req.onblocked = () => { blocked = true; };
        req.onsuccess = () => { const v = req.result.version; req.result.close(); resolve({ version: v, blocked }); };
        req.onerror = () => resolve({ version: null, blocked });
        setTimeout(() => resolve({ version: 'timeout', blocked }), 10000);
    }), DB);
    expect(outcome).toEqual({ version: 3, blocked: false });

    // The tab whose connection closed carries on: its ledger calls answer (from memory), nothing throws.
    const after = await page.evaluate(async () => {
        const l = await window.bwAssetLedger.open();
        await l.getMeta();
        return l.kind;
    });
    expect(after).toBe('memory');
});
