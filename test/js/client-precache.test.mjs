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

function precachePage({ version = VERSION, saveData = false, active = ALICE, walls = 1, degraded = false, worker = { client: null }, answer, storage = new Map() } = {}) {
    const asked = [];
    const win = {
        __bwAssetCacheManual: true,
        document: { querySelector: () => (version === null ? null : { getAttribute: () => version }) },
        navigator: { connection: { saveData } },
        localStorage: { getItem: k => storage.get(k) ?? null, setItem: (k, v) => storage.set(k, v) },
        bwAssetCache: {
            ready: Promise.resolve(),
            filled: () => Promise.resolve(),
            state: () => ({ active, env: { ledger: { degraded, wallsOf: async () => Array.from({ length: walls }, (_, i) => ({ wallId: 'w' + i })) } } }),
        },
        bwAssetStore: {
            askWorker: async (w, type = 'sw-info') => {
                asked.push(type);
                if (type === 'sw-info') {
                    return worker ? { proto: 1, type, shell: 'blocwerk-shell-v5', ...worker } : null;
                }
                return answer === undefined ? { proto: 1, type: 'client-precached', version: VERSION, complete: true } : answer;
            },
        },
    };
    vm.runInNewContext(js('client-precache.js'), { window: win, document: win.document, Date, JSON, Number });
    return { win, asked, storage, run: () => win.bwClientPrecache.boot() };
}

test('the page asks for the precache once its fill is done, and records the complete version', async () => {
    const page = precachePage();
    assert.equal(await page.run(), 'complete');
    assert.deepEqual(page.asked, ['sw-info', 'client-precache']);
    assert.equal(JSON.parse(page.storage.get('bw-client-precache')).complete, true);
});

test('Save-Data, no version, a kiosk, no partition, a degraded ledger or no offline wall: no precache', async () => {
    const cases = {
        'Save-Data': { saveData: true }, 'no version meta': { version: null }, kiosk: { active: 'k-' + 'k'.repeat(20) },
        none: { active: 'none' }, degraded: { degraded: true }, 'no offline wall': { walls: 0 },
    };
    for (const [name, options] of Object.entries(cases)) {
        const page = precachePage(options);
        assert.equal(await page.run(), 'skipped', name);
        assert.deepEqual(page.asked.filter(t => t === 'client-precache'), [], name);
    }
    assert.equal(await precachePage({ active: 'anon' }).run(), 'complete', 'a share viewer with a wall precaches');
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
