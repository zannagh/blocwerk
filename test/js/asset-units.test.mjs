// The asset-cache page modules' pure parts: ledger, budgets, fill order, eviction, the wipe decision and the notice
// threshold. Run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { loadPage, sha, USER_A, USER_B, KIOSK } from './fixtures/page-sandbox.mjs';

const { win } = loadPage();
const { bwAssetLedger: Ledger, bwAssetBudget: Budget, bwAssetPlan: Plan, bwAssetFill: Fill, bwAssetWipe: Wipe, bwAssetNotice: Notice } = win;
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

const plan = (walls, more = {}) => Plan.planFill(Object.assign({ walls, cachedKeys: new Set(), openWallId: null, keep3d: true, metered: false, budget: { images: 1e9, view3d: 1e9 }, usedImages: 0, used3d: 0, width: 1280, tokens: new Map() }, more));
const gallery = (s, at, bytes = 1000) => ({ sha: s, kind: 'gallery', tier: 'lazy', bytes, at, widths: [640, 1280, 1920, 2560] });

test('the fill goes tier by tier: every wall\'s core, then the gallery newest first, then 3D; the open wall first; other lazy never', () => {
    const walls = [
        wall('old', 1, [photo(sha('a'), 'core'), photo(sha('b'), 'lazy'), { sha: sha('c'), kind: 'texture', tier: 'view3d', bytes: 10 }, gallery(sha('g'), '2026-01-01T00:00:00+00:00'), gallery(sha('h'), '2026-06-01T00:00:00+00:00')]),
        wall('new', 5, [photo(sha('d'), 'core'), { sha: sha('e'), kind: 'texture', tier: 'view3d', bytes: 10 }]),
        wall('open', 0, [photo(sha('f'), 'core')]),
    ];
    const tasks = plan(walls, { openWallId: 'open' });
    assert.deepEqual(plain(tasks.map(t => [t.wallId, t.tier, t.sha[0], t.width])), [
        ['open', 'core', 'f', 1280], ['open', 'core', 'f', 640], ['new', 'core', 'd', 1280], ['new', 'core', 'd', 640],
        ['old', 'core', 'a', 1280], ['old', 'core', 'a', 640],
        ['old', 'gallery', 'h', 640], ['old', 'gallery', 'h', null], ['old', 'gallery', 'g', 640], ['old', 'gallery', 'g', null],
        ['new', 'view3d', 'e', null], ['old', 'view3d', 'c', null],
    ]);
    assert.ok(!tasks.some(t => t.sha === sha('b')));
});

test('the gallery fills only while the image budget has room; core fills whatever the budget', () => {
    const walls = [wall('w', 1, [photo(sha('a'), 'core', 500), gallery(sha('g'), '2026-06-01T00:00:00+00:00', 300), gallery(sha('h'), '2026-01-01T00:00:00+00:00', 300)])];
    const tasks = plan(walls, { usedImages: 500, budget: { images: 900, view3d: 1e9 } });
    assert.deepEqual(plain([...new Set(tasks.map(t => t.sha[0]))]), ['a', 'g']);
    assert.deepEqual(plain([...new Set(plan(walls, { usedImages: 5000, budget: { images: 1, view3d: 1 } }).map(t => t.sha[0]))]), ['a']);
});

test('3D fills only while kept, unmetered and within its budget', () => {
    const walls = [wall('w', 1, [photo(sha('a'), 'core'), { sha: sha('c'), kind: 'texture', tier: 'view3d', bytes: 10 }])];
    assert.ok(plan(walls, { keep3d: false }).every(t => t.tier === 'core'));
    assert.ok(plan(walls, { metered: true }).every(t => t.tier === 'core'));
    assert.ok(plan(walls, { budget: { images: 1e9, view3d: 5 } }).every(t => t.tier === 'core'));
    assert.ok(plan(walls).some(t => t.tier === 'view3d'));
    assert.equal(Plan.isMetered({ connection: { type: 'cellular' } }), true);
    assert.equal(Plan.isMetered({ connection: { saveData: true } }), true);
    assert.equal(Plan.isMetered({ connection: { effectiveType: '4g', type: 'wifi' } }), false);
    assert.equal(Plan.isMetered({}), false, 'no Network Information API counts as unmetered');
});

test('mobile browsers never background-fill 3D, whatever the connection; desktop does on unmetered', () => {
    const walls = [wall('w', 1, [photo(sha('a'), 'core'), { sha: sha('c'), kind: 'texture', tier: 'view3d', bytes: 10 }])];
    assert.ok(plan(walls, { mobile: true }).every(t => t.tier === 'core'));
    assert.ok(plan(walls, { mobile: false }).some(t => t.tier === 'view3d'));
    const nav = (ua, more = {}) => Object.assign({ userAgent: ua, maxTouchPoints: 0 }, more);
    assert.equal(Plan.isMobile(nav('Mozilla/5.0 (iPhone; CPU iPhone OS 18_7 like Mac OS X) AppleWebKit/605.1.15')), true);
    assert.equal(Plan.isMobile(nav('Mozilla/5.0 (Linux; Android 15; Pixel 9) Chrome/130.0 Mobile Safari/537.36')), true);
    assert.equal(Plan.isMobile(nav('Mozilla/5.0 (Linux; Android 15; SM-X910) Chrome/130.0 Safari/537.36')), true, 'an Android tablet');
    assert.equal(Plan.isMobile(nav('Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 Version/18.0 Safari/605.1.15', { maxTouchPoints: 5 })), true, 'iPadOS as a Mac');
    assert.equal(Plan.isMobile(nav('Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 Version/18.0 Safari/605.1.15')), false);
    assert.equal(Plan.isMobile(nav('Mozilla/5.0 (X11; Linux x86_64) Chrome/130.0', { userAgentData: { mobile: true } })), true);
    assert.equal(Plan.isMobile(nav('Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/130.0', { userAgentData: { mobile: false } })), false);
    const coarse = q => ({ matches: q === '(pointer: coarse)' });
    const fine = () => ({ matches: false });
    const desktopLinux = 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 Chrome/130.0 Safari/537.36';
    assert.equal(Plan.isMobile(nav(desktopLinux, { userAgentData: { mobile: false, platform: 'Android' } })), true, 'Chrome desktop-site mode on an Android tablet');
    assert.equal(Plan.isMobile(nav('Mozilla/5.0 (X11; Linux x86_64; rv:131.0) Gecko/20100101 Firefox/131.0', { maxTouchPoints: 5 }), coarse), true, 'Firefox Android in desktop mode');
    assert.equal(Plan.isMobile(nav(desktopLinux, { maxTouchPoints: 0 }), coarse), false, 'a desktop Linux has no touch');
    assert.equal(Plan.isMobile(nav(desktopLinux, { maxTouchPoints: 5 }), fine), false, 'a Linux touch screen with a mouse');
    assert.equal(Plan.isMobile(nav('Mozilla/5.0 (X11; CrOS x86_64 14541.0.0) Chrome/130.0', { maxTouchPoints: 10 }), coarse), false, 'a Chromebook');
    const windows = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/130.0 Safari/537.36';
    assert.equal(Plan.isMobile(nav(windows, { maxTouchPoints: 10, userAgentData: { mobile: false, platform: 'Windows' } }), coarse), false, 'a Windows touch laptop');
    assert.equal(Plan.isMobile(nav(windows, { maxTouchPoints: 0 }), fine), false, 'a Windows desktop');
});

test('the gallery\'s room starts after the core this plan adds, and each rendition is charged on its own', () => {
    const core = photo(sha('a'), 'core', 4000);
    const g = gallery(sha('g'), '2026-06-01T00:00:00+00:00', 4000);
    const coreCost = Plan.bytesOf(core, 1280) + Plan.bytesOf(core, 640);
    const galleryCost = Plan.bytesOf(g, 640) + Plan.bytesOf(g, null);
    const shas = budget => [...new Set(plan([wall('w', 1, [core, g])], { budget: { images: budget, view3d: 0 } }).map(t => t.sha[0]))];
    assert.deepEqual(plain(shas(coreCost + galleryCost)), ['a', 'g']);
    assert.deepEqual(plain(shas(coreCost + galleryCost - 1)), ['a'], 'core\'s planned bytes count, and the 640 is charged beside the original');
    assert.deepEqual(plain(shas(galleryCost)), ['a'], 'core alone would overshoot');
});

test('the fill takes one splat level (the smallest), none if the wall has one, and skips what is cached', () => {
    const levels = [{ sha: sha('1'), kind: 'splat', tier: 'view3d', bytes: 5, splats: 250000 }, { sha: sha('2'), kind: 'splat', tier: 'view3d', bytes: 2, splats: 40000 }];
    const w = [wall('w', 1, levels)];
    assert.deepEqual(plain(plan(w).map(t => t.sha)), [sha('2')]);
    assert.deepEqual(plain(plan(w, { cachedKeys: new Set([sha('1')]) })), []);
});

test('a share token rides on the fill URL; the fit width follows image-res.js FIT_CAP', () => {
    const tasks = plan([wall('w', 1, [{ sha: sha('a'), kind: 'texture', tier: 'core', bytes: 1 }])], { tokens: new Map([['w', 't k']]) });
    assert.equal(tasks[0].url, `/api/walls/w/a/${sha('a')}?token=t%20k`);
    assert.equal(Plan.fitWidth(390, 3), 1280);
    assert.equal(Plan.fitWidth(1440, 2), 1920);
    assert.equal(Plan.fitWidth(300, 1), 640);
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

test('eviction: other lazy assets go before the gallery, the gallery after 3D, core never', () => {
    const walls = [wall('w', 1, [photo(sha('a'), 'core'), photo(sha('b'), 'lazy'), gallery(sha('g'), null), { sha: sha('t'), kind: 'texture', tier: 'view3d' }])];
    const assets = [asset(sha('a'), 50 * MB), asset(sha('b'), 30 * MB), asset(sha('g'), 30 * MB), asset(sha('t'), 30 * MB)];
    const evict = images => plain(Budget.planEviction({ walls, assets, budget: { images, view3d: 0 }, openWallId: null, keep3d: true }));
    assert.deepEqual(evict(85 * MB), [sha('t'), sha('b')]);
    assert.deepEqual(evict(60 * MB), [sha('t'), sha('b'), sha('g')]);
});

test('an orphan cached in the last 7 days is kept; an older one goes', () => {
    const now = Date.parse('2026-10-21T12:00:00Z');
    const assets = [asset(sha('n'), 1, now - 24 * 3600 * 1000), asset(sha('o'), 1, now - 8 * 24 * 3600 * 1000)];
    const keys = Budget.planEviction({ walls: [], assets, budget: { images: 1e9, view3d: 1e9 }, openWallId: null, keep3d: true, orphans: true, now });
    assert.deepEqual(plain(keys), [sha('o')]);
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

test('the wipe decision: rules 1 and 6, offline and unknown', () => {
    const d = (prev, label, token = false, userChanged = false) => {
        const r = plain(Wipe.decide(prev, label, token, userChanged));
        return [r.wipe, r.keep, r.active, r.page, r.identityChanged];
    };
    assert.deepEqual(d(USER_A, USER_A), [false, USER_A, USER_A, USER_A, false]);
    assert.deepEqual(d(USER_A, KIOSK), [true, KIOSK, KIOSK, KIOSK, true]);
    assert.deepEqual(d(null, USER_A), [true, USER_A, USER_A, USER_A, false]);
    assert.deepEqual(d(USER_B, USER_A), [true, USER_A, USER_A, USER_A, false], 'a new label alone (key rotation) is no identity change');
    assert.deepEqual(d(USER_B, USER_A, false, true), [true, USER_A, USER_A, USER_A, true]);
    assert.deepEqual(d(USER_A, 'anon'), [true, null, 'anon', 'anon', true]);
    assert.deepEqual(d(USER_A, null), [true, null, 'none', 'none', true]);
    assert.deepEqual(d(USER_A, 'none'), [true, null, 'none', 'none', true]);
    assert.deepEqual(d(USER_A, 'garbage'), [true, null, 'none', 'none', true]);
    assert.deepEqual(d(USER_A, 'offline'), [false, USER_A, USER_A, 'offline', false]);
    assert.deepEqual(d(USER_A, 'offline', false, true), [false, USER_A, USER_A, 'offline', false]);
    assert.deepEqual(d(USER_A, 'unknown'), [false, USER_A, USER_A, 'unknown', false]);
    assert.deepEqual(d(USER_A, 'unknown', false, true), [true, null, 'none', 'unknown', true]);
    assert.deepEqual(d(KIOSK, KIOSK, false, true), [false, KIOSK, KIOSK, KIOSK, true]);
    assert.deepEqual(d('anon', 'anon'), [true, null, 'anon', 'anon', false], 'rule 6 announces only when the device was not anonymous');
    assert.deepEqual(d(null, 'anon'), [true, null, 'anon', 'anon', false]);
    assert.deepEqual(d('anon', 'anon', true), [false, 'anon', 'anon', 'anon', false]);
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
