// How blazor-boot.js gets a circuit the watchdog reported down (markDown) back: a socket that fails to open is
// retried on the next probe, and only a circuit that never got an id is given up for a reload. Run by
// `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../../src/Blocwerk.Web/wwwroot/js/blazor-boot.js', import.meta.url), 'utf8');

/** reconnects: what each Blazor.reconnect() call does in turn: 'throw:<message>' or a boolean. */
function page(reconnects) {
    const timers = [];
    let now = 0;
    const calls = { reconnect: 0, resume: 0, reloads: 0, opened: null };
    const win = {
        setTimeout: (fn, ms) => { timers.push({ fn, at: now + (ms || 0) }); return timers.length; },
        clearTimeout: id => { if (id && timers[id - 1]) { timers[id - 1].done = true; } },
        fetch: () => Promise.resolve({ ok: true }),
        AbortController: undefined,
        location: { reload: () => { calls.reloads++; } },
        addEventListener: () => {},
        document: { getElementById: () => null, addEventListener: () => {}, visibilityState: 'visible' },
        console,
        Blazor: {
            addEventListener: () => {},
            start: options => { calls.opened = options.circuit.circuitHandlers[0].onCircuitOpened; return Promise.resolve(); },
            reconnect: async () => {
                calls.reconnect++;
                const next = reconnects.shift();
                if (typeof next === 'string' && next.startsWith('throw:')) {
                    throw new Error(next.slice(6));
                }
                return next;
            },
            resumeCircuit: async () => { calls.resume++; return false; },
        },
    };
    win.window = win;
    vm.runInNewContext(source, Object.assign(win, { Math, Promise, String, Date }));
    return {
        calls, conn: win.blocwerkConnection,
        advance: async ms => {
            const end = now + ms;
            for (;;) {
                const next = timers.filter(t => !t.done && t.at <= end).sort((a, b) => a.at - b.at)[0];
                if (!next) {
                    break;
                }
                next.done = true;
                now = next.at;
                next.fn();
                for (let i = 0; i < 10; i++) {
                    await new Promise(resolve => setImmediate(resolve));
                }
            }
            now = end;
        },
    };
}

test('a socket that fails to open twice is retried, and the circuit the server still holds comes back without a reload', async () => {
    const notConnected = "throw:Cannot send data if the connection is not in the 'Connected' State.";
    const p = page([notConnected, 'throw:Failed to complete negotiation with the server', true]);
    p.conn.markDown();
    assert.equal(p.conn.status(), 'failed');
    await p.advance(30000);
    assert.equal(p.calls.reconnect, 3);
    assert.equal(p.conn.status(), 'connected');
    assert.equal(p.calls.resume, 0);
    assert.equal(p.calls.reloads, 0);
});

test('a circuit that never got an id is given up: Session ended, for a reload', async () => {
    const p = page(['throw:Circuit host not initialized.']);
    p.conn.markDown();
    await p.advance(10000);
    assert.equal(p.conn.status(), 'rejected');
    assert.equal(p.calls.reconnect, 1, 'no second try');
});

test('a start the watchdog called down that opens after all is connected again', async () => {
    const p = page([]);
    await p.advance(0);
    p.conn.markDown();
    assert.equal(p.conn.circuitOpened(), false);
    p.calls.opened();
    assert.equal(p.conn.circuitOpened(), true);
    assert.equal(p.conn.status(), 'connected');
});
