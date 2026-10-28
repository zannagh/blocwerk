// The ledger's IndexedDB schema v1 → v2 (Phase 3 PR 3.3) on a fake IndexedDB: the in-place upgrade keeps every v1 row,
// a fresh install, a tab that closes on `versionchange`, a Release 1/2 tab that blocks the upgrade (memory now, the
// upgrade later, never "storage refused"), an open queued behind it, and a Release 1/2 page meeting a v2 database.
// Run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import { fakeIndexedDB } from './fixtures/fake-idb.mjs';
import { moduleUrl } from './fixtures/page-modules.mjs';

// The ledger and its IndexedDB half (asset-ledger-idb.js, split out in PR 3.5), as a page loads them.
const CURRENT = ['asset-ledger-idb.js', 'asset-ledger.js']
    .map(name => readFileSync(moduleUrl(name), 'utf8')).join('\n');
const RELEASED_V1 = readFileSync(new URL('./fixtures/asset-ledger-v1.js', import.meta.url), 'utf8');
const DB = 'blocwerk-assets';
const USER = 'u-aaaaaaaaaaaaaaaaaaaa';
const W1 = '11111111-1111-1111-1111-111111111111';
const sha = c => c.repeat(64);
const turns = async (n = 20) => { for (let i = 0; i < n; i++) { await new Promise(r => setImmediate(r)); } };

// One page load: a fresh copy of the module (its open() is memoized per page). Timers run 100x faster; `frozen`
// timers never fire, so only the database's own events can settle an open.
function page(idb, source = CURRENT, { frozen = false } = {}) {
    const setTimer = frozen ? () => 0 : (fn, ms) => setTimeout(fn, ms / 100);
    const win = { indexedDB: idb, setTimeout: setTimer, clearTimeout: frozen ? () => undefined : clearTimeout };
    const context = vm.createContext({ window: win, JSON, Promise, Object, Array, Error, Map });
    vm.runInContext(source, context);
    return win.bwAssetLedger;
}

// A hang is a failure here, not a test that never ends.
const within = (promise, what) => Promise.race([promise, new Promise((_, reject) => setTimeout(() => reject(new Error(what)), 2000))]);
const plain = value => JSON.parse(JSON.stringify(value));

// What a Release 1/2 page leaves behind: meta, a wall row from the manifest round plus a view3d doc, and its assets.
async function seedV1(idb) {
    const v1 = page(idb, RELEASED_V1);
    const ledger = await v1.open();
    assert.equal(ledger.kind, 'idb');
    await ledger.setMeta({ activePartition: USER, userTag: 'abc', proto: 1 });
    await ledger.updateWall(USER, W1, () => ({
        partition: USER, wallId: W1, etag: 'W/"e1"', cursor: '1.2.3', pending: 0, member: true, lastOpened: 5, fetchedAt: 6,
        keepOffline: true, save3d: true, entries: [{ sha: sha('1'), kind: 'panel', tier: 'core', bytes: 100 }],
        docs: [{ sha: sha('d'), kind: 'view3d', tier: 'view3d', bytes: 9 }]
    }));
    await ledger.putAsset({ partition: USER, key: `${sha('1')}/640`, sha: sha('1'), width: 640, bytes: 10, cachedAt: 1, verifiedAt: 1 });
    await ledger.putAsset({ partition: USER, key: sha('d'), sha: sha('d'), width: null, bytes: 9, cachedAt: 1, verifiedAt: 1 });
    return ledger;
}

test('a fresh install opens v2 with all four stores', async () => {
    const idb = fakeIndexedDB();
    const bw = page(idb);
    const ledger = await bw.open();
    assert.equal(ledger.kind, 'idb');
    assert.equal(bw.upgradePending(), false);
    assert.equal(idb.version(DB), 2);
    assert.deepEqual(idb.storeNames(DB), ['assets', 'meta', 'mine', 'walls']);
    assert.equal((await ledger.getMeta()).schema, 2);
});

test('v1 → v2 keeps every row in place and stamps the schema', async () => {
    const idb = fakeIndexedDB();
    await seedV1(idb);
    const before = { walls: idb.rows(DB, 'walls'), assets: idb.rows(DB, 'assets') };
    idb.closeAll(DB); // the Release 1/2 tab is gone: the next page load runs the new code

    const bw = page(idb);
    const ledger = await bw.open();
    assert.equal(ledger.kind, 'idb');
    assert.equal(bw.upgradePending(), false);
    assert.equal(idb.version(DB), 2);
    assert.deepEqual(idb.storeNames(DB), ['assets', 'meta', 'mine', 'walls']);
    assert.deepEqual(idb.rows(DB, 'walls'), before.walls);
    assert.deepEqual(idb.rows(DB, 'assets'), before.assets);
    const meta = await ledger.getMeta();
    assert.equal(meta.schema, 2);
    assert.equal(meta.activePartition, USER, 'the active label survives, so the next start decides no wipe');
    assert.equal(meta.userTag, 'abc');
    const wall = await ledger.getWall(USER, W1);
    assert.equal(wall.data, undefined, 'a v1 row simply has no data yet');
    assert.deepEqual(plain(bw.listed(wall)).map(e => e.sha), [sha('1'), sha('d')]);
    assert.equal((await ledger.assetsOf(USER)).length, 2);
});

test('an open tab closes on versionchange: a later upgrade is never blocked, and the tab carries on on memory', async () => {
    const idb = fakeIndexedDB();
    const tab = await page(idb).open();
    await tab.setMeta({ activePartition: USER });
    let blocked = false;
    const later = idb.open(DB, 3);
    later.onblocked = () => { blocked = true; };
    const upgraded = await within(new Promise(resolve => { later.onsuccess = () => resolve(later.result); }), 'the open tab held the upgrade back');
    assert.equal(blocked, false);
    assert.equal(upgraded.version, 3);

    // The closed connection fails every call; the resilient wrapper moves to memory without throwing into the page.
    assert.equal((await tab.getMeta()).activePartition, null);
    await tab.updateWall(USER, W1, () => ({ partition: USER, wallId: W1, entries: [] }));
    assert.equal(tab.kind, 'memory');
    assert.equal(idb.rows(DB, 'walls').length, 0, 'nothing of the old tab reaches the upgraded database');
});

test('a Release 1/2 tab blocks the upgrade: this page runs on memory as pending, and the upgrade finishes once it closes', async () => {
    const idb = fakeIndexedDB();
    await seedV1(idb); // the v1 tab stays open, with no versionchange handler

    const bw = page(idb, CURRENT, { frozen: true }); // `blocked` itself settles the open, not the wait
    const ledger = await within(bw.open(), 'a blocked open never settled');
    assert.equal(ledger.kind, 'memory');
    assert.equal(bw.upgradePending(), true, 'pending, not refused');
    assert.equal(idb.version(DB), 1);
    await ledger.setMeta({ activePartition: 'u-bbbbbbbbbbbbbbbbbbbb' });
    assert.equal(idb.rows(DB, 'meta')[0].activePartition, USER, 'the memory page writes nothing to the database');

    idb.closeAll(DB); // the old tab closes (or reloads into the new code)
    await turns();
    assert.equal(idb.version(DB), 2, 'the open left running upgraded the database');
    assert.equal(idb.connections(DB), 0, 'and closed its late connection');

    const next = page(idb); // the next start
    const reopened = await next.open();
    assert.equal(reopened.kind, 'idb');
    assert.equal(next.upgradePending(), false);
    assert.equal((await reopened.getWall(USER, W1)).entries[0].sha, sha('1'));
    assert.equal((await reopened.getMeta()).schema, 2);
});

test('an open queued behind a blocked upgrade is pending after the wait, then the next start opens v2', async () => {
    const idb = fakeIndexedDB();
    await seedV1(idb);
    const first = page(idb);
    await first.open();
    assert.equal(first.upgradePending(), true);

    const second = page(idb); // gets no `blocked` of its own: it waits behind the first page's open
    const ledger = await second.open();
    assert.equal(ledger.kind, 'memory');
    assert.equal(second.upgradePending(), true);

    idb.closeAll(DB);
    await turns();
    assert.equal(idb.version(DB), 2);
    assert.equal(idb.connections(DB), 0, 'both late connections closed');
    assert.equal((await page(idb).open()).kind, 'idb');
});

test('a Release 1/2 page meeting a v2 database falls back to memory and leaves it untouched', async () => {
    const idb = fakeIndexedDB();
    const current = await page(idb).open();
    await current.setMeta({ activePartition: USER });

    const v1 = page(idb, RELEASED_V1);
    const old = await v1.open();
    assert.equal(old.kind, 'memory', 'VersionError: the released fallback');
    await old.setMeta({ activePartition: 'anon' });
    assert.equal(idb.version(DB), 2);
    assert.equal((await current.getMeta()).activePartition, USER);
});

test('refused storage is not a pending upgrade', async () => {
    const refusing = { open: () => { throw Object.assign(new Error('refused'), { name: 'SecurityError' }); } };
    const bw = page(refusing);
    assert.equal((await bw.open()).kind, 'memory');
    assert.equal(bw.upgradePending(), false);

    const failing = { open: () => {
        const req = {};
        setImmediate(() => { req.error = new Error('UnknownError'); req.onerror({}); });
        return req;
    } };
    const other = page(failing);
    assert.equal((await other.open()).kind, 'memory');
    assert.equal(other.upgradePending(), false);
});

test('listed() reads the manifest entries, the view3d docs and the data documents', async () => {
    const bw = page(fakeIndexedDB());
    const ledger = await bw.open();
    const wall = {
        partition: USER, wallId: W1, entries: [{ sha: sha('1'), kind: 'panel', tier: 'core', bytes: 1 }],
        data: { holds: { sha: sha('2'), bytes: 30, cursor: '1.1.1' }, comments: { sha: sha('3'), bytes: 4, cursor: '1.1.2' } }
    };
    assert.deepEqual(plain(bw.listed(wall)).slice(1), [
        { sha: sha('2'), kind: 'holds', tier: 'data', bytes: 30 },
        { sha: sha('3'), kind: 'comments', tier: 'data', bytes: 4 }
    ]);
    assert.deepEqual(plain(bw.listed({})), []);
});
