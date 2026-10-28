// The offline client's hand-back (client-handback.js, Phase 3 PR 3.14): the /alive probe and the pill, the next tap's
// full navigation to the Server page once online (never before), and the schema update that reloads at most once.
// Runs in a vm against hand-made fakes of the page.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../../src/Blocwerk.Client/wwwroot/js/client-handback.js', import.meta.url), 'utf8');
const ORIGIN = 'https://blocwerk.test';
const OLD = '1111111111111111';
const NEW = '2222222222222222';

class FakeElement {
    constructor(tag) {
        this.tagName = tag.toUpperCase();
        this.children = [];
        this.attributes = {};
        this.listeners = {};
        this.textContent = '';
        this.className = '';
    }
    setAttribute(k, v) { this.attributes[k] = String(v); }
    getAttribute(k) { return this.attributes[k] ?? null; }
    addEventListener(type, fn) { this.listeners[type] = fn; }
    append(...nodes) { this.children.push(...nodes); }
    replaceChildren(...nodes) { this.children = nodes; }
    get text() { return this.children.map(c => c.textContent).join(''); }
}

function page({ path = '/v/walls/W1', alive = { clientSchemaMin: 1 }, schema = '1', version = OLD, worker = { version: NEW, complete: true },
    session = new Map(), onLine = true, walls = [{ savedAt: Date.now() }], storageThrows = false, localMarker = null } = {}) {
    const assigned = [];
    const reloads = [];
    const shown = [];
    const asked = [];
    const messages = [];
    const winListeners = {};
    const docListeners = {};
    const slot = new FakeElement('span');
    const metas = { 'bw-client-schema': schema, 'bw-client-version': version };
    const state = { alive };
    const doc = {
        visibilityState: 'visible',
        activeElement: null,
        querySelector: sel => {
            const name = /name="([^"]+)"/.exec(sel)[1];
            return metas[name] === undefined ? null : { getAttribute: () => metas[name] };
        },
        getElementById: id => (id === 'bw-client-pill' ? slot : null),
        createElement: tag => new FakeElement(tag),
        addEventListener: (type, fn) => { docListeners[type] = fn; },
    };
    const storage = {
        getItem: k => { if (storageThrows) { throw new Error('denied'); } return session.get(k) ?? null; },
        setItem: (k, v) => { if (storageThrows) { throw new Error('denied'); } session.set(k, v); },
    };
    const win = {
        location: { href: ORIGIN + path, origin: ORIGIN, assign: url => assigned.push(url), reload: () => reloads.push(1) },
        navigator: {
            onLine,
            serviceWorker: { controller: { postMessage: (message, ports) => { asked.push(message.type); messages.push(message); ports[0].onmessage({ data: worker ? { proto: 1, type: 'client-precached', ...worker } : null }); } } },
        },
        sessionStorage: storage,
        localStorage: { getItem: k => (k === 'bw-client-precache' ? localMarker : null) },
        setTimeout: () => 0,
        clearTimeout: () => undefined,
        MessageChannel: class { constructor() { this.port1 = {}; this.port2 = this.port1; } },
        AbortController,
        addEventListener: (type, fn) => { winListeners[type] = fn; },
        fetch: async url => {
            assert.equal(url, '/alive');
            if (state.alive === null) {
                throw new TypeError('offline');
            }
            return { ok: true, json: async () => state.alive };
        },
        bwStatus: { show: kind => shown.push(kind) },
        bwClientStore: { snapshot: async () => ({ state: 'ready', walls }) },
    };
    vm.runInNewContext(source, { window: win, document: doc, URL, Date, Number, String, Math, Promise, Object, Array });
    const api = win.bwClientHandback;
    return { api, win, doc, slot, assigned, reloads, shown, asked, messages, session, state, winListeners, docListeners };
}

const pillText = p => p.slot.children[0].children[1].textContent;
const settle = () => new Promise(resolve => setTimeout(resolve, 0));

test('the Server twin of a client URL keeps the query and fragment, and never leaves the origin', () => {
    const { api } = page();
    assert.equal(api.serverUrl('/v/walls/W1/boulders/B2?view=x#c'), '/walls/W1/boulders/B2?view=x#c');
    assert.equal(api.serverUrl(`${ORIGIN}/v/`), '/');
    assert.equal(api.serverUrl('/v'), '/');
    assert.equal(api.serverUrl('/V/walls'), '/walls');
    assert.equal(api.serverUrl('/v//evil.example/x'), '/evil.example/x');
    assert.equal(api.serverUrl('/vx/walls'), null);
    assert.equal(api.serverUrl('https://elsewhere.test/v/walls'), null);
});

test('offline, the pill says read-only with the saved time, and a tap stays in the client', async () => {
    const p = page({ alive: null });
    assert.equal(await p.api.start(), false);
    await settle();
    assert.match(pillText(p), /^Offline · read-only · saved \d{1,2}[:.]\d{2}/);
    assert.equal(p.slot.children[0].tagName, 'SPAN');
    assert.deepEqual(p.shown, ['offline'], 'opening offline tells "Switched to offline mode"');
    assert.equal(p.api.take(`${ORIGIN}/v/walls/W1/boulders/B2`), false);
    assert.deepEqual(p.assigned, []);

    const none = page({ alive: null, walls: [] });
    await none.api.start();
    await settle();
    assert.equal(pillText(none), 'Offline · read-only');
});

test('back online, the next tap goes to the Server page and the pill opens the live page', async () => {
    const p = page({ alive: null });
    await p.api.start();
    p.state.alive = { clientSchemaMin: 1 };
    assert.equal(await p.api.probe(), true);
    assert.deepEqual(p.shown, ['offline', 'online']);
    assert.equal(pillText(p), 'Back online · open live');

    assert.equal(p.api.take(`${ORIGIN}/v/walls/W1/boulders/B2?x=1`), true);
    assert.deepEqual(p.assigned, ['/walls/W1/boulders/B2?x=1']);
    assert.ok(Number(p.session.get('bw-handback')) > 0, 'the Server page learns it was opened by the hand-back');

    p.slot.children[0].listeners.click();
    assert.deepEqual(p.assigned.at(-1), '/walls/W1');
    assert.equal(p.api.take('https://elsewhere.test/x'), false, 'a URL outside the client is not ours');
});

test('the browser going offline is believed at once, and a failed probe takes the online state back', async () => {
    const p = page();
    assert.equal(await p.api.start(), true);
    assert.deepEqual(p.shown, [], 'opening online says nothing');
    p.winListeners.offline();
    assert.equal(p.api.take(`${ORIGIN}/v/walls/W1`), false);
    p.state.alive = { clientSchemaMin: 1 };
    await p.api.probe();
    p.state.alive = null;
    await p.api.probe();
    assert.equal(p.api.take(`${ORIGIN}/v/walls/W1`), false);
    assert.deepEqual(p.assigned, []);
});

test('a schema bump precaches the new version and reloads once; the next page with the same floor never reloads', async () => {
    const session = new Map();
    const first = page({ alive: { clientSchemaMin: 2 }, session });
    await first.api.start();
    assert.deepEqual(first.asked, ['client-precache']);
    assert.equal(first.reloads.length, 1);
    assert.equal(session.get('bw-client-schema-reload'), '2');

    // The reloaded page is still below the floor (the deploy's own client is old): no second reload, no precache.
    const second = page({ alive: { clientSchemaMin: 2 }, session, version: NEW });
    await second.api.start();
    assert.equal(await second.api.checkSchema(2), 'held');
    assert.equal(second.reloads.length, 0);
    assert.deepEqual(second.asked, []);

    // A higher floor later is a new update.
    const third = page({ alive: { clientSchemaMin: 3 }, session, version: NEW, worker: { version: '3333333333333333', complete: true } });
    await third.api.start();
    assert.equal(third.reloads.length, 1);
});

test('no reload without a newer complete version, without storage, or at the current schema', async () => {
    const cases = {
        'same version': { alive: { clientSchemaMin: 2 }, worker: { version: OLD, complete: true } },
        'fill failed': { alive: { clientSchemaMin: 2 }, worker: { version: NEW, complete: false } },
        'no worker answer': { alive: { clientSchemaMin: 2 }, worker: null },
        'no session storage': { alive: { clientSchemaMin: 2 }, storageThrows: true },
        'at the floor': { alive: { clientSchemaMin: 1 } },
        'below the floor of an older server': { alive: { clientSchemaMin: 0 } },
        'no floor (an older server)': { alive: {} },
    };
    for (const [name, options] of Object.entries(cases)) {
        const p = page(options);
        await p.api.start();
        assert.equal(p.reloads.length, 0, name);
    }
});

test('the schema reload waits while someone types, and happens when the field is left', async () => {
    const p = page({ alive: { clientSchemaMin: 2 } });
    p.doc.activeElement = { tagName: 'INPUT', value: 'cra' };
    await p.api.start();
    assert.equal(p.reloads.length, 0);
    assert.equal(await p.api.checkSchema(2), 'waiting');
    p.doc.activeElement = null;
    p.docListeners.visibilitychange();
    assert.equal(p.reloads.length, 1);
    p.docListeners.visibilitychange();
    assert.equal(p.reloads.length, 1, 'once');
});

test('a deploy\'s /alive (maintenance) is not online, and the next tap stays in the client', async () => {
    const p = page({ alive: { clientSchemaMin: 1, maintenance: true } });
    await p.api.start();
    assert.equal(p.api.online, false);
    assert.match(pillText(p), /^Offline/);
    assert.equal(p.api.take(`${ORIGIN}/v/walls/W1`), false);
    p.state.alive = { clientSchemaMin: 1, maintenance: false };
    assert.equal(await p.api.probe(), true);
});

test('after "Continue offline" the next taps stay in the client for 2 minutes, or until the pill is tapped', async () => {
    const session = new Map([['bw-stay-offline', String(Date.now())]]);
    const p = page({ session });
    await p.api.start();
    assert.equal(p.api.online, true);
    assert.equal(p.api.take(`${ORIGIN}/v/walls/W1/boulders/B2`), false, 'a circuit-only drop: /alive answers, the Server page would not');
    assert.deepEqual(p.assigned, []);

    p.slot.children[0].listeners.click();
    assert.deepEqual(p.assigned, ['/walls/W1'], 'the pill itself always opens the live page');
    assert.equal(p.api.take(`${ORIGIN}/v/walls/W1/boulders/B2`), true, 'and the next tap hands back again');

    const old = page({ session: new Map([['bw-stay-offline', String(Date.now() - 121000)]]) });
    await old.api.start();
    assert.equal(old.api.take(`${ORIGIN}/v/walls/W1`), true, 'after 2 minutes a tap hands back as usual');
});

test('the pill is redrawn only when it changes, so a probe does not drop the focus on its button', async () => {
    const p = page();
    await p.api.start();
    const first = p.slot.children[0];
    await p.api.probe();
    await p.api.probe();
    assert.equal(p.slot.children[0], first);
    p.state.alive = null;
    await p.api.probe();
    assert.notEqual(p.slot.children[0], first);
});

test('the schema update carries the 3D code as the last fill had it, and a stale result is tried again', async () => {
    const withMarker = page({ alive: { clientSchemaMin: 2 }, localMarker: JSON.stringify({ version: OLD, complete: true, with3d: true }) });
    await withMarker.api.start();
    assert.equal(withMarker.messages[0].group3d, 'precache');
    const without = page({ alive: { clientSchemaMin: 2 } });
    await without.api.start();
    assert.equal(without.messages[0].group3d, 'opened');

    const busy = page({ alive: { clientSchemaMin: 2 }, worker: { version: OLD, complete: true } });
    await busy.api.start();
    assert.equal(await busy.api.checkSchema(2), 'stale');
    assert.equal(busy.asked.length, 2, 'the check ran again instead of keeping the first answer for the page\'s life');
});
