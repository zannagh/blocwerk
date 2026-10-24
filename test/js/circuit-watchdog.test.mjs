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
    const conn = { status: 'connected', started: false, lastChangeAt: 0, marked: 0 };
    const win = {
        navigator: { onLine: opts.onLine ?? true },
        WebSocket: FakeSocket,
        Date: { now: () => now },
        setTimeout: (fn, ms) => { timers.push({ fn, at: now + ms }); return timers.length; },
        clearTimeout: () => {},
        fetch: () => (opts.serverUp ? Promise.resolve({ ok: true }) : Promise.reject(new Error('refused'))),
        blocwerkConnection: {
            status: () => conn.status, started: () => conn.started, lastChangeAt: () => conn.lastChangeAt,
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
    p.conn.started = true;
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

test('a start that has not finished after 10 s is reported down when the device is offline; not once it started', async () => {
    const offline = page({ onLine: false, serverUp: true });
    await offline.advance(9000);
    assert.equal(offline.conn.marked, 0);
    await offline.advance(1100);
    assert.equal(offline.conn.marked, 1, 'navigator.onLine false: no need to ask the server');

    const started = page({ onLine: false });
    started.conn.started = true;
    await started.advance(11000);
    assert.equal(started.conn.marked, 0);
});
