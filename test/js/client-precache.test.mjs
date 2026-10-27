// The page's half of the offline client (Phase 3 PR 3.9): client-precache.js asks the worker to precache the client
// once the asset cache's first fill is done, and only when it may (Save-Data, partition, offline walls, worker version,
// retry marker); sw-guard.js reloads a page whose controller no longer answers, once the server is reachable. Both run
// in a vm against hand-made fakes of the page.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const js = name => readFileSync(new URL(`../../src/Blocwerk.Web/wwwroot/js/${name}`, import.meta.url), 'utf8');
const VERSION = 'abcdef0123456789';
const ALICE = 'u-' + 'a'.repeat(20);

// `ua` loads asset-plan.js (bwAssetPlan.isMobile) with that user agent; without it the page has no plan (a phone's rule).
function precachePage({ version = VERSION, saveData = false, active = ALICE, walls = 1, degraded = false, worker = { client: null }, answer, storage = new Map(), fetchedAt = [], ua = null, keep3d = true } = {}) {
    const asked = [];
    const fields = [];
    const state = new Map();
    const caches = { open: async name => ({ put: async (key, response) => state.set(`${name} ${key}`, await response.text()) }) };
    const win = {
        __bwAssetCacheManual: true,
        document: { querySelector: () => (version === null ? null : { getAttribute: () => version }) },
        navigator: { connection: { saveData }, userAgent: ua || '', maxTouchPoints: 0 },
        localStorage: { getItem: k => storage.get(k) ?? null, setItem: (k, v) => storage.set(k, v) },
        bwAssetCache: {
            ready: Promise.resolve(),
            filled: () => Promise.resolve(),
            keep3d: () => keep3d,
            state: () => ({ active, env: { ledger: { degraded, wallsOf: async () => Array.from({ length: walls }, (_, i) => ({ wallId: 'w' + i, fetchedAt: fetchedAt[i] })) } } }),
        },
        bwAssetStore: {
            askWorker: async (w, type = 'sw-info', reply = type, ms = 0, extra = {}) => {
                asked.push(type);
                fields.push({ type, ...extra });
                if (type === 'sw-info') {
                    return worker ? { proto: 1, type, shell: 'blocwerk-shell-v5', ...worker } : null;
                }
                return answer === undefined ? { proto: 1, type: 'client-precached', version: VERSION, complete: true } : answer;
            },
        },
    };
    win.caches = caches;
    const context = vm.createContext({ window: win, document: win.document, Date, JSON, Number, Response });
    if (ua !== null) {
        vm.runInContext(js('asset-plan.js'), context);
    }
    vm.runInContext(js('client-precache.js'), context);
    return { win, asked, fields, storage, state, run: () => win.bwClientPrecache.boot() };
}

test('the page asks for the precache once its fill is done, and records the complete version', async () => {
    const page = precachePage();
    assert.equal(await page.run(), 'complete');
    assert.deepEqual(page.asked, ['sw-info', 'client-precache']);
    assert.equal(JSON.parse(page.storage.get('bw-client-precache')).complete, true);
});

test('Save-Data, no version, no partition, a degraded ledger or no offline wall: no precache', async () => {
    const cases = {
        'Save-Data': { saveData: true }, 'no version meta': { version: null },
        none: { active: 'none' }, degraded: { degraded: true }, 'no offline wall': { walls: 0 },
        'a kiosk with no wall': { active: 'k-' + 'k'.repeat(20), walls: 0 },
    };
    for (const [name, options] of Object.entries(cases)) {
        const page = precachePage(options);
        assert.equal(await page.run(), 'skipped', name);
        assert.deepEqual(page.asked.filter(t => t === 'client-precache'), [], name);
    }
    assert.equal(await precachePage({ active: 'anon' }).run(), 'complete', 'a share viewer with a wall precaches');
    assert.equal(await precachePage({ active: 'k-' + 'k'.repeat(20) }).run(), 'complete', 'a kiosk tablet with its wall precaches (PR 3.15)');
});

test('an old worker (no client field) or none is left alone; a complete one is not asked again', async () => {
    const old = precachePage({ worker: {} });
    assert.equal(await old.run(), 'unsupported');
    assert.equal(await precachePage({ worker: null }).run(), 'unsupported');
    const done = precachePage({ worker: { client: VERSION } });
    assert.equal(await done.run(), 'complete');
    assert.deepEqual(done.asked, ['sw-info']);
});

test('a failed attempt is not retried for 10 minutes, then is', async () => {
    const storage = new Map();
    assert.equal(await precachePage({ storage, answer: { complete: false, version: null } }).run(), 'failed');
    const again = precachePage({ storage });
    assert.equal(await again.run(), 'waiting');
    assert.deepEqual(again.asked, ['sw-info']);
    storage.set('bw-client-precache', JSON.stringify({ version: VERSION, at: Date.now() - 11 * 60 * 1000, complete: false }));
    assert.equal(await precachePage({ storage }).run(), 'complete');
    assert.equal(await precachePage({ storage, worker: { client: null } }).run(), 'complete',
        'a marker never stops a worker that lost its copy from being asked again');
});

const DESKTOP = 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36';
const IPHONE = 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1';
const ANDROID = 'Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Mobile Safari/537.36';
const group3dOf = page => page.fields.find(f => f.type === 'client-precache')?.group3d;

test("the 3D code: a desktop precaches it, a phone or tablet only once 3D was opened there, the opt-out never", async () => {
    const cases = { desktop: [{ ua: DESKTOP }, 'precache'], iPhone: [{ ua: IPHONE }, 'opened'], Android: [{ ua: ANDROID }, 'opened'],
        'desktop, 3D opted out': [{ ua: DESKTOP, keep3d: false }, 'none'], 'phone, 3D opted out': [{ ua: IPHONE, keep3d: false }, 'none'],
        'no asset plan': [{}, 'opened'] };
    for (const [name, [options, group]] of Object.entries(cases)) {
        const page = precachePage(options);
        assert.equal(await page.run(), 'complete', name);
        assert.equal(group3dOf(page), group, name);
    }
});

test('a complete version is asked once more on a desktop that went without the 3D code, never on a phone', async () => {
    const storage = new Map([['bw-client-precache', JSON.stringify({ version: VERSION, at: Date.now(), complete: true })]]);
    const desktop = precachePage({ ua: DESKTOP, storage, worker: { client: VERSION } });
    assert.equal(await desktop.run(), 'complete');
    assert.equal(group3dOf(desktop), 'precache');
    assert.equal(JSON.parse(storage.get('bw-client-precache')).with3d, true);
    const again = precachePage({ ua: DESKTOP, storage, worker: { client: VERSION } });
    await again.run();
    assert.deepEqual(again.asked, ['sw-info'], 'not again once it has the 3D code');
    const left = new Map([['bw-client-precache', JSON.stringify({ version: VERSION, at: Date.now(), complete: false })]]);
    const reloaded = precachePage({ ua: DESKTOP, storage: left, worker: { client: VERSION } });
    assert.equal(await reloaded.run(), 'complete', 'the page that asked was gone before the answer: no retry wait');
    assert.equal(group3dOf(reloaded), 'precache');
    const phone = precachePage({ ua: IPHONE, storage: new Map(), worker: { client: VERSION } });
    await phone.run();
    assert.deepEqual(phone.asked, ['sw-info']);
});

test('store3d() asks the worker to store the 3D code', async () => {
    const page = precachePage();
    await page.win.bwClientPrecache.store3d();
    assert.deepEqual(page.asked, ['client-store-3d']);
});

// sw-guard.js: `answers` is how the controller answers each sw-info in turn (true answers, false stays silent).
function guardPage({ controller = true, answers = [true], alive = true, editing = false, lastReload = null, active = null } = {}) {
    const timers = [];
    const session = new Map(lastReload === null ? [] : [['bw-sw-guard-reload', String(lastReload)]]);
    const page = { reloads: 0, events: [], attr: null, pings: 0 };
    class MessageChannel {
        constructor() {
            this.port1 = {};
            this.port2 = { postMessage: m => this.port1.onmessage({ data: m }) };
        }
    }
    const worker = {
        postMessage: (message, [port]) => {
            const answer = answers[Math.min(page.pings++, answers.length - 1)];
            if (answer) {
                queueMicrotask(() => port.postMessage({ proto: 1, type: 'sw-info' }));
            }
        },
    };
    const win = {
        __bwSwGuardManual: true,
        navigator: { serviceWorker: { controller: controller ? worker : null } },
        MessageChannel,
        setTimeout: (fn, ms) => { const t = { fn, ms }; timers.push(t); setTimeout(() => t.fn(), 0); return timers.length; },
        clearTimeout: id => { timers[id - 1].fn = () => undefined; },
        fetch: async url => { page.fetched = url; if (!alive) { throw new TypeError('down'); } return { ok: true }; },
        sessionStorage: { getItem: k => session.get(k) ?? null, setItem: (k, v) => session.set(k, v) },
        location: { reload: () => { page.reloads++; } },
        document: { visibilityState: 'visible', activeElement: active, addEventListener: () => undefined, documentElement: { setAttribute: (k, v) => { page.attr = v; } } },
        dispatchEvent: e => page.events.push(e.type),
        CustomEvent: class { constructor(type) { this.type = type; } },
        bwEditGuard: { isEditing: () => editing },
    };
    vm.runInNewContext(js('sw-guard.js'), { window: win, Date, Number, String, Promise, queueMicrotask, setTimeout });
    return { page, session, check: () => win.bwSwGuard.boot() };
}

test('a live controller, or none, is left alone', async () => {
    assert.equal(await guardPage().check(), 'alive');
    assert.equal(await guardPage({ answers: [false, true] }).check(), 'alive', 'one silent ask is not a loss');
    const none = guardPage({ controller: false });
    assert.equal(await none.check(), 'uncontrolled');
    assert.equal(none.page.reloads, 0);
});

test('a controller that answers nothing twice reloads the page once the server is reachable', async () => {
    const lost = guardPage({ answers: [false] });
    assert.equal(await lost.check(), 'reloading');
    assert.equal(lost.page.reloads, 1);
    assert.equal(lost.page.fetched, '/alive');
    assert.ok(Number(lost.session.get('bw-sw-guard-reload')) > 0);
});

test('a lost worker falls back instead of reloading: server down, an editor open, or a reload within 5 minutes', async () => {
    const cases = {
        'server down': { alive: false }, editing: { editing: true }, 'reloaded just now': { lastReload: Date.now() - 1000 },
        'text typed in an input': { active: { tagName: 'INPUT', value: 'a comment' } },
        'text typed in a textarea': { active: { tagName: 'TEXTAREA', value: 'beta notes' } },
        'text in a contenteditable': { active: { tagName: 'DIV', isContentEditable: true, textContent: ' draft ' } },
    };
    for (const [name, options] of Object.entries(cases)) {
        const guard = guardPage({ answers: [false], ...options });
        assert.equal(await guard.check(), 'lost', name);
        assert.equal(guard.page.reloads, 0, name);
        assert.equal(guard.page.attr, 'lost', name);
        assert.deepEqual(guard.page.events, ['bw-worker-lost'], name);
    }
    assert.equal(await guardPage({ answers: [false], lastReload: Date.now() - 6 * 60 * 1000 }).check(), 'reloading');
    assert.equal(await guardPage({ answers: [false], active: { tagName: 'INPUT', value: '' } }).check(), 'reloading', 'an empty focused field');
});

test('deploy() posts deploy-pending or deploy-done to the controller, and does nothing without one', async () => {
    const posted = [];
    const win = {
        __bwSwGuardManual: true,
        navigator: { serviceWorker: { controller: { postMessage: m => posted.push(m) } } },
    };
    vm.runInNewContext(js('sw-guard.js'), { window: win, Date, Number, String, Promise });
    assert.equal(win.bwSwGuard.deploy(true), true);
    assert.equal(win.bwSwGuard.deploy(false), true);
    assert.deepEqual(posted.map(m => ({ ...m })), [{ proto: 1, type: 'deploy-pending' }, { proto: 1, type: 'deploy-done' }]);
    win.navigator.serviceWorker.controller = null;
    assert.equal(win.bwSwGuard.deploy(true), false);
});

test('a kiosk page stamps its last online fill for the worker; no other label, and no successful round, stamps', async () => {
    const KIOSK = 'k-' + 'k'.repeat(20);
    const tablet = precachePage({ active: KIOSK, walls: 1, fetchedAt: [1234] });
    await tablet.run();
    assert.deepEqual(JSON.parse(JSON.stringify(await tablet.win.bwClientPrecache.stamped)), { label: KIOSK, at: 1234 });
    assert.equal(tablet.state.get('bw-sw-state /__bw/kiosk-fresh'), JSON.stringify({ label: KIOSK, at: 1234 }));

    for (const options of [{ active: ALICE, fetchedAt: [1234] }, { active: KIOSK, fetchedAt: [undefined] }, { active: KIOSK, degraded: true, fetchedAt: [1] }]) {
        const page = precachePage(options);
        await page.run();
        assert.equal(await page.win.bwClientPrecache.stamped, null, JSON.stringify(options));
        assert.equal(page.state.size, 0, JSON.stringify(options));
    }
});
