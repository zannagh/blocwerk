// The circuit watchdog (circuit-watchdog.js): a circuit socket lost with no reconnect state from the framework, or a
// start that never finishes, is reported down when the device or the server is down. Run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../../src/Blocwerk.Web/wwwroot/js/circuit-watchdog.js', import.meta.url), 'utf8');

function page(opts = {}) {
    const timers = [];
    let now = 1_000_000;
    const sockets = [];
    class FakeSocket {
        constructor(url) { this.url = url; this.listeners = {}; sockets.push(this); }
        addEventListener(t, fn) { (this.listeners[t] = this.listeners[t] || []).push(fn); }
        close() { (this.listeners.close || []).forEach(fn => fn({})); }
    }
    const conn = { status: 'connected', opened: false, lastChangeAt: 0, marked: 0 };
    const comments = opts.markers ? ['Blazor:{"type":"server","prerenderId":"1","key":{}}', 'Blazor:{"prerenderId":"1"}'] : [' a comment '];
    const win = {
        document: {
            documentElement: {},
            createTreeWalker: () => {
                let i = 0;
                return { nextNode: () => (i < comments.length ? { nodeValue: comments[i++] } : null) };
            },
        },
        navigator: { onLine: opts.onLine ?? true },
        WebSocket: FakeSocket,
        Date: { now: () => now },
        setTimeout: (fn, ms) => { timers.push({ fn, at: now + ms }); return timers.length; },
        clearTimeout: () => {},
        fetch: () => (opts.serverUp ? Promise.resolve({ ok: true }) : Promise.reject(new Error('refused'))),
        blocwerkConnection: {
            status: () => conn.status, circuitOpened: () => conn.opened, lastChangeAt: () => conn.lastChangeAt,
            markDown: () => { conn.marked++; conn.status = 'failed'; }
        },
    };
    win.window = win;
    vm.runInNewContext(source, Object.assign(win, { String, Promise }));
    return {
        conn, sockets, win,
        open: url => new win.WebSocket(url),
        advance: async ms => {
            const end = now + ms;
            for (const t of timers.filter(x => !x.done && x.at <= end).sort((a, b) => a.at - b.at)) {
                t.done = true;
                now = t.at;
                t.fn();
            }
            now = end;
            await new Promise(resolve => setImmediate(resolve));
        },
    };
}

test('a circuit socket that drops with no reconnect state, the server gone: reported down after 2 s', async () => {
    const p = page();
    p.conn.opened = true;
    p.open('ws://x/_blazor?id=1').close();
    await p.advance(1900);
    assert.equal(p.conn.marked, 0);
    await p.advance(200);
    assert.equal(p.conn.marked, 1);
    assert.equal(p.conn.status, 'failed');
});

test('nothing is reported when the framework handles it, the server still answers, or it is not the circuit', async () => {
    const handled = page();
    handled.open('ws://x/_blazor?id=1').close();
    handled.conn.lastChangeAt = Date.now() + 10_000_000; // the framework raised a state after the close
    await handled.advance(3000);
    assert.equal(handled.conn.marked, 0);

    const up = page({ serverUp: true });
    up.open('ws://x/_blazor?id=1').close();
    await up.advance(3000);
    assert.equal(up.conn.marked, 0, 'a navigation or a resume: the server answers');

    const other = page();
    other.open('ws://x/some-feed').close();
    await other.advance(3000);
    assert.equal(other.conn.marked, 0);
});

test('a page with server components whose circuit has not opened after 10 s, offline: reported down', async () => {
    const offline = page({ onLine: false, serverUp: true, markers: true });
    offline.open('ws://x/_blazor?id=1'); // a socket that never opens, and never closes either
    await offline.advance(9000);
    assert.equal(offline.conn.marked, 0);
    await offline.advance(1100);
    assert.equal(offline.conn.marked, 1, 'navigator.onLine false: no need to ask the server');
});

test('the start check stays quiet when the circuit opened, the server answers, or the page wants no circuit', async () => {
    const opened = page({ onLine: false, markers: true });
    opened.conn.opened = true;
    await opened.advance(11000);
    assert.equal(opened.conn.marked, 0);

    const slow = page({ serverUp: true, markers: true });
    await slow.advance(11000);
    assert.equal(slow.conn.marked, 0, 'a slow start with the server answering is not offline');

    const staticPage = page({ onLine: false });
    await staticPage.advance(11000);
    assert.equal(staticPage.conn.marked, 0, 'no server component markers: no circuit to wait for');
});
