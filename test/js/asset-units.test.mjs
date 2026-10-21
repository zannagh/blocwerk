// The asset-cache page modules' pure parts: ledger, budgets, fill order, eviction, the wipe decision and the notice
// threshold. Run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { loadPage, sha, USER_A, KIOSK } from './fixtures/page-sandbox.mjs';

const { win } = loadPage();
const { bwAssetLedger: Ledger, bwAssetBudget: Budget, bwAssetFill: Fill, bwAssetWipe: Wipe, bwAssetNotice: Notice } = win;
const plain = value => JSON.parse(JSON.stringify(value));
const MB = 1024 * 1024;

test('the ledger keeps walls and assets per partition and wipes all but one', async () => {
    const ledger = Ledger.memory();
    await ledger.putWall({ partition: 'a', wallId: 'w1', entries: [] });
    await ledger.putWall({ partition: 'b', wallId: 'w1', entries: [] });
    await ledger.putAsset({ partition: 'a', key: 'k1', sha: 'k1' });
    await ledger.putAsset({ partition: 'b', key: 'k1', sha: 'k1' });
    await ledger.setMeta({ activePartition: 'a' });

    assert.equal((await ledger.wallsOf('a')).length, 1);
    await ledger.deleteAllExcept('a');
    assert.deepEqual(plain((await ledger.assetsOf('b'))), []);
    assert.equal((await ledger.assetsOf('a')).length, 1);
    assert.equal((await ledger.getMeta()).activePartition, 'a');
    await ledger.deleteAllExcept(null);
    assert.equal((await ledger.wallsOf('a')).length, 0);
});

test('a ledger whose storage refuses a write moves to memory and keeps working', async () => {
    const broken = { kind: 'idb', get: async () => { throw new Error('x'); }, put: async () => { throw new Error('QuotaExceededError'); }, delete: async () => { throw new Error('x'); }, all: async () => { throw new Error('x'); }, clear: async () => { throw new Error('x'); } };
    const ledger = Ledger.create(broken);
    await ledger.putAsset({ partition: 'a', key: 'k', sha: 'k' });
    assert.equal(ledger.kind, 'memory');
    assert.equal((await ledger.assetsOf('a')).length, 1);
});

const wall = (wallId, lastOpened, entries, more = {}) => Object.assign({ partition: 'p', wallId, lastOpened, entries }, more);
const photo = (s, tier, bytes = 1000) => ({ sha: s, kind: 'panel', tier, bytes, widths: [640, 1280, 1920, 2560] });

test('the fill goes tier by tier: every wall\'s core before any 3D, the open wall first, lazy never', () => {
    const walls = [
        wall('old', 1, [photo(sha('a'), 'core'), photo(sha('b'), 'lazy'), { sha: sha('c'), kind: 'texture', tier: 'view3d', bytes: 10 }]),
        wall('new', 5, [photo(sha('d'), 'core'), { sha: sha('e'), kind: 'texture', tier: 'view3d', bytes: 10 }]),
        wall('open', 0, [photo(sha('f'), 'core')]),
    ];
    const tasks = Fill.planFill({ walls, cachedKeys: new Set(), openWallId: 'open', keep3d: true, budget3d: 1e9, used3d: 0, width: 1280, tokens: new Map() });
    assert.deepEqual(plain(tasks.map(t => [t.wallId, t.tier, t.width])), [
        ['open', 'core', 1280], ['open', 'core', 640], ['new', 'core', 1280], ['new', 'core', 640],
        ['old', 'core', 1280], ['old', 'core', 640], ['new', 'view3d', null], ['old', 'view3d', null],
    ]);
    assert.ok(!tasks.some(t => t.sha === sha('b')));
});

test('the 3D opt-out plans no view3d, and core is planned whatever the 3D budget', () => {
    const walls = [wall('w', 1, [photo(sha('a'), 'core'), { sha: sha('c'), kind: 'texture', tier: 'view3d', bytes: 10 }])];
    const off = Fill.planFill({ walls, cachedKeys: new Set(), openWallId: null, keep3d: false, budget3d: 1e9, used3d: 0, width: 1920 });
    assert.ok(off.every(t => t.tier === 'core'));
    const full = Fill.planFill({ walls, cachedKeys: new Set(), openWallId: null, keep3d: true, budget3d: 5, used3d: 0, width: 1920 });
    assert.ok(full.every(t => t.tier === 'core') && full.length === 2);
});

test('the fill takes one splat level (the smallest), none if the wall has one, and skips what is cached', () => {
    const levels = [{ sha: sha('1'), kind: 'splat', tier: 'view3d', bytes: 5, splats: 250000 }, { sha: sha('2'), kind: 'splat', tier: 'view3d', bytes: 2, splats: 40000 }];
    const w = [wall('w', 1, levels)];
    const plan = cached => Fill.planFill({ walls: w, cachedKeys: new Set(cached), openWallId: 'w', keep3d: true, budget3d: 1e9, used3d: 0, width: 1920 });
    assert.deepEqual(plain(plan([]).map(t => t.sha)), [sha('2')]);
    assert.deepEqual(plain(plan([sha('1')])), []);
});

test('a share token rides on the fill URL; the fit width follows image-res.js FIT_CAP', () => {
    const tasks = Fill.planFill({ walls: [wall('w', 1, [{ sha: sha('a'), kind: 'texture', tier: 'core', bytes: 1 }])], cachedKeys: new Set(), openWallId: 'w', keep3d: true, budget3d: 0, used3d: 0, width: 1920, tokens: new Map([['w', 't k']]) });
    assert.equal(tasks[0].url, `/api/walls/w/a/${sha('a')}?token=t%20k`);
    assert.equal(Fill.fitWidth(390, 3), 1280);
    assert.equal(Fill.fitWidth(1440, 2), 1920);
    assert.equal(Fill.fitWidth(300, 1), 640);
});

test('fill concurrency is 1 on cellular or Save-Data, else 2', () => {
    assert.equal(Fill.concurrencyOf({}), 2);
    assert.equal(Fill.concurrencyOf({ connection: { saveData: true } }), 1);
    assert.equal(Fill.concurrencyOf({ connection: { type: 'cellular' } }), 1);
    assert.equal(Fill.concurrencyOf({ connection: { effectiveType: '4g' } }), 2);
});

const asset = (s, bytes, cachedAt = 0, width = null) => ({ partition: 'p', key: width ? `${s}/${width}` : s, sha: s, width, bytes, cachedAt });

test('eviction: lazy over the image budget goes oldest wall first; core and the open wall never', () => {
    const walls = [
        wall('old', 1, [photo(sha('a'), 'core'), photo(sha('b'), 'lazy')]),
        wall('mid', 2, [photo(sha('c'), 'lazy')]),
        wall('open', 0, [photo(sha('d'), 'lazy')]),
    ];
    const assets = [asset(sha('a'), 60 * MB), asset(sha('b'), 30 * MB), asset(sha('c'), 30 * MB), asset(sha('d'), 30 * MB)];
    const keys = Budget.planEviction({ walls, assets, budget: { images: 100 * MB, view3d: 1e9 }, openWallId: 'open', keep3d: true });
    assert.deepEqual(plain(keys), [sha('b'), sha('c')]);
    const tight = Budget.planEviction({ walls, assets, budget: { images: 1, view3d: 1e9 }, openWallId: 'open', keep3d: true });
    assert.ok(!tight.includes(sha('a')) && !tight.includes(sha('d')));
});

test('eviction: 3D over its budget, superseded splat levels, every 3D asset on opt-out, and orphans', () => {
    const levels = [40000, 120000, 250000].map((n, i) => ({ sha: sha(String(i + 1)), kind: 'splat', tier: 'view3d', splats: n }));
    const walls = [wall('w', 1, levels.concat([{ sha: sha('t'), kind: 'texture', tier: 'view3d' }])), wall('x', 0, [{ sha: sha('u'), kind: 'texture', tier: 'view3d' }])];
    const assets = [asset(sha('1'), 1), asset(sha('2'), 1), asset(sha('3'), 1), asset(sha('t'), 10 * MB), asset(sha('u'), 10 * MB), asset(sha('z'), 1)];
    const kept = Budget.planEviction({ walls, assets, budget: { images: 1e9, view3d: 15 * MB }, openWallId: 'w', keep3d: true, orphans: true });
    assert.deepEqual(plain(kept.sort()), [sha('2'), sha('u'), sha('z')].sort());
    const optOut = Budget.planEviction({ walls, assets, budget: { images: 1e9, view3d: 1e9 }, openWallId: 'w', keep3d: false, orphans: true });
    assert.equal(optOut.length, 6);
});

test('an asset that one wall lists as core is never another wall\'s lazy victim', () => {
    const walls = [wall('b', 0, [photo(sha('s'), 'lazy')]), wall('a', 1, [photo(sha('s'), 'core')])];
    assert.deepEqual(plain(Budget.planEviction({ walls, assets: [asset(sha('s'), 500 * MB)], budget: { images: 1, view3d: 1 }, openWallId: null, keep3d: true })), []);
});

test('budget contexts, from the quota only: estimate().usage is never read', async () => {
    const ctx = (ua, more = {}) => Budget.detect({ navigator: Object.assign({ userAgent: ua, maxTouchPoints: 5, standalone: false, storage: { estimate: async () => ({ quota: more.quota ?? 40e9, get usage() { throw new Error('usage read'); } }), persisted: async () => !!more.persisted } }), matchMedia: () => ({ matches: !!more.standalone }) });
    const iphone = 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_7 like Mac OS X) AppleWebKit/605.1.15 Version/18.0 Mobile/15E148 Safari/604.1';
    assert.equal((await ctx(iphone)).name, 'safariTab');
    assert.equal((await ctx(iphone, { standalone: true })).name, 'appleApp');
    assert.equal((await ctx('Mozilla/5.0 Firefox/131.0')).name, 'firefox');
    assert.equal((await ctx('Mozilla/5.0 Chrome/130.0 Safari/537.36')).name, 'chromium');
    assert.equal((await ctx('Mozilla/5.0 Chrome/130.0', { quota: 100 * MB })).name, 'constrained');
    assert.deepEqual(plain(Budget.budgetFor({ name: 'chromium', quota: 1000 * MB })), { images: 500 * MB, view3d: 500 * MB });
    assert.deepEqual(plain(Budget.budgetFor({ name: 'safariTab', quota: 40e9 })), { images: 250 * MB, view3d: 250 * MB });
});

test('the wipe decision: rules 1 and 6', () => {
    const d = (prev, label, token = false) => plain(Wipe.decide(prev, label, token));
    assert.deepEqual(d(USER_A, USER_A), { wipe: false, keep: USER_A, active: USER_A, identityChanged: false });
    assert.deepEqual(d(USER_A, KIOSK), { wipe: true, keep: KIOSK, active: KIOSK, identityChanged: true });
    assert.deepEqual(d(null, USER_A), { wipe: true, keep: USER_A, active: USER_A, identityChanged: false });
    assert.deepEqual(d(USER_A, null), { wipe: true, keep: null, active: 'none', identityChanged: true });
    assert.deepEqual(d(USER_A, 'none'), { wipe: true, keep: null, active: 'none', identityChanged: true });
    assert.deepEqual(d(USER_A, 'garbage'), { wipe: true, keep: null, active: 'none', identityChanged: true });
    assert.deepEqual(d(USER_A, 'offline'), { wipe: false, keep: USER_A, active: USER_A, identityChanged: false });
    assert.deepEqual(d('anon', 'anon'), { wipe: true, keep: null, active: 'anon', identityChanged: true });
    assert.deepEqual(d(null, 'anon'), { wipe: true, keep: null, active: 'anon', identityChanged: false });
    assert.deepEqual(d('anon', 'anon', true), { wipe: false, keep: 'anon', active: 'anon', identityChanged: false });
});

test('the share token comes from the /shared/ segment or ?token=', () => {
    assert.equal(Wipe.tokenOf({ pathname: '/walls/w/shared/abc/3d', search: '' }), 'abc');
    assert.equal(Wipe.tokenOf({ pathname: '/walls/w', search: '?token=xyz' }), 'xyz');
    assert.equal(Wipe.tokenOf({ pathname: '/walls/w', search: '' }), null);
});

test('the high-storage notice shows from 3 walls with 3D, for signed-in users who keep 3D and did not dismiss it', () => {
    const facts = (more = {}) => Object.assign({ partition: USER_A, summary: { wallsWith3d: 3 }, keep3d: true, ios: false, standalone: false, cachedWalls: 1, installShowing: false, dismissed: () => false }, more);
    assert.equal(Notice.decide(facts()).kind, 'high-storage');
    assert.equal(Notice.decide(facts({ summary: { wallsWith3d: 2 } })), null);
    assert.equal(Notice.decide(facts({ keep3d: false })), null);
    assert.equal(Notice.decide(facts({ dismissed: k => k === 'high-storage' })), null);
    assert.equal(Notice.decide(facts({ partition: KIOSK })), null);
    assert.equal(Notice.decide(facts({ partition: 'anon' })), null);
    assert.equal(Notice.decide(facts({ summary: { wallsWith3d: 0 }, ios: true })).kind, 'install-ios');
    assert.equal(Notice.decide(facts({ summary: { wallsWith3d: 0 }, ios: true, standalone: true })), null);
    assert.equal(Notice.decide(facts({ summary: { wallsWith3d: 0 }, ios: true, installShowing: true })), null);
});
