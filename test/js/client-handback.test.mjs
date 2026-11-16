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
    session = new Map(), onLine = true, walls = [{ savedAt: Date.now() }], storageThrows = false, localMarker = null, partition = 'u-' + 'a'.repeat(20), setThrows = false } = {}) {
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
        setItem: (k, v) => { if (storageThrows || setThrows) { throw new Error('denied'); } session.set(k, v); },
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
        bwClientStore: { snapshot: async () => ({ state: 'ready', partition, walls }) },
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

// A kiosk tablet (PR 3.15) the worker handed to /v/walls (0.5.2: a failed or slow landing page `/` opens the wall list).
const KIOSK = 'k-' + 'k'.repeat(20);

test('a kiosk tablet back online goes live by itself, and its wall list opens `/` (its wall), not the signed-in /walls', async () => {
    for (const path of ['/v/walls', '/v/', '/v/walls?x=1']) {
        const p = page({ path, partition: KIOSK });
        await p.api.start();
        await settle();
        assert.deepEqual(p.assigned, ['/'], path);
    }
    const wall = page({ path: '/v/walls/W1', partition: KIOSK });
    await wall.api.start();
    await settle();
    assert.deepEqual(wall.assigned, ['/walls/W1'], 'the wall itself goes to its live page');

    const pill = page({ path: '/v/walls', partition: KIOSK, session: new Map([['bw-kiosk-golive', String(Date.now() - 5000)]]) });
    await pill.api.start();
    await settle();
    assert.deepEqual(pill.assigned, [], 'it went live 5 s ago and failed back: no loop');
    pill.slot.children[0].listeners.click();
    assert.deepEqual(pill.assigned, ['/'], 'the pill opens the landing, which sends the tablet to its wall');
});

test('a kiosk retries going live once a minute; a person, an offline tablet, "Continue offline" or no storage never goes on its own', async () => {
    const later = page({ path: '/v/walls', partition: KIOSK, session: new Map([['bw-kiosk-golive', String(Date.now() - 61000)]]) });
    await later.api.start();
    await settle();
    assert.deepEqual(later.assigned, ['/']);

    const cases = {
        person: { path: '/v/walls' },
        offline: { path: '/v/walls', partition: KIOSK, alive: null },
        'continue offline': { path: '/v/walls', partition: KIOSK, session: new Map([['bw-stay-offline', String(Date.now())]]) },
        'no session storage': { path: '/v/walls', partition: KIOSK, storageThrows: true },
    };
    for (const [name, options] of Object.entries(cases)) {
        const p = page(options);
        await p.api.start();
        await settle();
        assert.deepEqual(p.assigned, [], name);
    }
    const person = page({ path: '/v/walls' });
    await person.api.start();
    assert.equal(person.api.take(`${ORIGIN}/v/walls`), true);
    assert.deepEqual(person.assigned, ['/walls'], "a person's wall list is still /walls");
});

test('no flapping: a kiosk with the server down sits on its wall, and one whose pages bounce back retries once a minute', async () => {
    // The server down: /alive never answers, whatever wakes the probe; the tablet stays on its wall in the client.
    const down = page({ path: '/v/walls/W1', partition: KIOSK, alive: null });
    await down.api.start();
    for (let i = 0; i < 5; i++) {
        await down.api.probe();
    }
    down.winListeners.online();
    down.docListeners.visibilitychange();
    await settle();
    assert.deepEqual(down.assigned, []);
    assert.match(pillText(down), /^Offline/);

    // /alive answers but the Server page fails: the tablet goes live once, the worker bounces it back to /v/ (a new page in
    // the same tab), and that page waits out the minute however often it probes.
    const session = new Map();
    const first = page({ path: '/v/walls/W1', partition: KIOSK, session });
    await first.api.start();
    await settle();
    assert.deepEqual(first.assigned, ['/walls/W1']);
    session.delete('bw-handback'); // the Server page's welcomeBack() clears the hand-back stamp, never the kiosk's
    const bounced = page({ path: '/v/walls/W1', partition: KIOSK, session });
    await bounced.api.start();
    for (let i = 0; i < 4; i++) {
        await bounced.api.probe();
    }
    bounced.winListeners.online();
    await settle();
    assert.deepEqual(bounced.assigned, [], 'within the minute');
    session.set('bw-kiosk-golive', String(Date.now() - 61000));
    await bounced.api.probe();
    assert.deepEqual(bounced.assigned, ['/walls/W1'], 'a minute later, once');
    await bounced.api.probe();
    assert.deepEqual(bounced.assigned, ['/walls/W1']);
});

test("a kiosk stays offline through a deploy's /alive (maintenance), and without a stamp it can write it never goes on its own", async () => {
    const deploying = page({ path: '/v/walls/W1', partition: KIOSK, alive: { clientSchemaMin: 1, maintenance: true } });
    await deploying.api.start();
    await deploying.api.probe();
    await settle();
    assert.equal(deploying.api.online, false);
    assert.deepEqual(deploying.assigned, []);

    const readOnly = page({ path: '/v/walls/W1', partition: KIOSK, setThrows: true });
    await readOnly.api.start();
    await readOnly.api.probe();
    await settle();
    assert.deepEqual(readOnly.assigned, [], 'no write, no retry limit: no auto go-live');
});
