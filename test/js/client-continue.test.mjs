// "Continue offline" on the Server pages (client-continue.js, Phase 3 PR 3.14): a circuit drop asks the worker for the
// offline client's twin of the page and the pill offers it; never while connected, during an announced deploy, or for a
// path the page has left. A page opened by the client's hand-back says "online" once. Runs in a vm against fakes.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../../src/Blocwerk.Web/wwwroot/js/client-continue.js', import.meta.url), 'utf8');

function page({ answer = '/v/walls/W1?view=x', session = new Map(), controller = true, editing = false, confirms = true } = {}) {
    const asked = [];
    const confirmed = [];
    const assigned = [];
    const shown = [];
    const subscribers = [];
    const timers = [];
    let renders = 0;
    const connection = { status: 'connected' };
    const queue = { online: true };
    const maintenance = { active: false };
    const win = {
        location: { pathname: '/walls/W1', search: '?view=x', assign: url => assigned.push(url) },
        navigator: {
            serviceWorker: controller ? {
                controller: {
                    postMessage: (message, ports) => {
                        asked.push(message);
                        ports[0].onmessage({ data: { proto: 1, type: 'client-target', target: answer } });
                    }
                }
            } : {},
        },
        MessageChannel: class { constructor() { this.port1 = {}; this.port2 = this.port1; } },
        setTimeout: fn => { timers.push(fn); return timers.length; },
        clearTimeout: () => undefined,
        sessionStorage: { getItem: k => session.get(k) ?? null, setItem: (k, v) => session.set(k, v), removeItem: k => session.delete(k) },
        bwEditGuard: { isEditing: () => editing },
        confirm: message => { confirmed.push(message); return confirms; },
        blocwerkConnection: { status: () => connection.status, subscribe: cb => { subscribers.push(cb); cb(connection.status); } },
        blocwerkOfflineQueue: { state: () => ({ online: queue.online }), subscribe: cb => subscribers.push(cb) },
        blocwerkStatus: { render: () => { renders++; } },
        bwMaintenance: { active: () => maintenance.active },
        bwStatus: { show: kind => shown.push(kind) },
    };
    const doc = { readyState: 'complete', addEventListener: () => undefined };
    vm.runInNewContext(source, { window: win, document: doc, Date, Number, Promise });
    const change = async () => {
        subscribers.forEach(cb => cb(connection.status));
        await new Promise(resolve => setTimeout(resolve, 0));
    };
    return { api: win.bwClientContinue, win, session, confirmed, asked, assigned, shown, connection, queue, maintenance, change, timers, renders: () => renders };
}

test('connected, nothing is asked and nothing offered', async () => {
    const p = page();
    await p.change();
    assert.deepEqual(p.asked, []);
    assert.equal(p.api.target(), null);
    assert.equal(p.api.go(), false);
});

test('a circuit drop asks the worker for this page and the pill offers the client; the tap opens it', async () => {
    for (const status of ['reconnecting', 'failed', 'rejected']) {
        const p = page();
        p.connection.status = status;
        await p.change();
        assert.deepEqual(p.asked.map(m => [m.type, m.path]), [['client-target', '/walls/W1?view=x']], status);
        assert.ok(p.renders() > 0, 'the pill is drawn again once the worker answers');
        assert.equal(p.api.target(), '/v/walls/W1?view=x', status);
        assert.equal(p.api.go(), true);
        assert.deepEqual(p.assigned, ['/v/walls/W1?view=x']);
    }
    const offline = page();
    offline.queue.online = false;
    await offline.change();
    assert.equal(offline.api.target(), '/v/walls/W1?view=x', 'a device without network too');
});

test('no offer when the worker has no client for the page, during an announced deploy, back online, or on another path', async () => {
    const none = page({ answer: null });
    none.connection.status = 'failed';
    await none.change();
    assert.equal(none.api.target(), null);
    const foreign = page({ answer: 'https://elsewhere.test/v/x' });
    foreign.connection.status = 'failed';
    await foreign.change();
    assert.equal(foreign.api.target(), null, 'only a /v/ path');
    const old = page({ controller: false });
    old.connection.status = 'failed';
    await old.change();
    assert.equal(old.api.target(), null, 'no worker');

    const p = page();
    p.connection.status = 'failed';
    await p.change();
    p.maintenance.active = true;
    assert.equal(p.api.target(), null, 'deploy announced');
    p.maintenance.active = false;
    p.win.location.pathname = '/walls/W2';
    assert.equal(p.api.target(), null, 'the page moved on');
    p.win.location.pathname = '/walls/W1';
    p.connection.status = 'connected';
    assert.equal(p.api.target(), null, 'connected again');
});

test('a page opened by the hand-back says "online" once, and only when fresh', () => {
    const session = new Map([['bw-handback', String(Date.now())]]);
    const p = page({ session });
    p.timers.forEach(fn => fn());
    assert.deepEqual(p.shown, ['online']);
    assert.equal(session.has('bw-handback'), false);
    assert.equal(p.api.welcomeBack(), false);

    const stale = page({ session: new Map([['bw-handback', String(Date.now() - 120000)]]) });
    stale.timers.forEach(fn => fn());
    assert.deepEqual(stale.shown, []);
});

test('with an editor open, "Continue offline" asks first: declined, nothing happens; accepted, the client opens and stays', async () => {
    const declined = page({ editing: true, confirms: false });
    declined.connection.status = 'reconnecting';
    await declined.change();
    assert.equal(declined.api.go(), false);
    assert.equal(declined.confirmed.length, 1);
    assert.deepEqual(declined.assigned, []);

    const accepted = page({ editing: true });
    accepted.connection.status = 'reconnecting';
    await accepted.change();
    assert.equal(accepted.api.go(), true);
    assert.deepEqual(accepted.assigned, ['/v/walls/W1?view=x']);
    assert.ok(Number(accepted.session.get('bw-stay-offline')) > 0, 'the client then stays put for a while (client-handback.js)');

    const idle = page();
    idle.connection.status = 'failed';
    await idle.change();
    assert.equal(idle.api.go(), true);
    assert.deepEqual(idle.confirmed, [], 'no question without an open editor');
});
