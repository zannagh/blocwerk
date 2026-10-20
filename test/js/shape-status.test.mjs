// The Shape tool's pen policy (wall-shape-status.js) and its commit (wall-shape-commit.js): run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const require = createRequire(import.meta.url);
const js = '../../src/Blocwerk.Web/wwwroot/js/';
const status = require(js + 'wall-shape-status.js');

test('pen only lasts 30 s after the last pen input, then fingers paint again', () => {
    let now = 0;
    const pen = status.penPolicy(() => now);
    assert.equal(pen.penOnly(), false);
    pen.down(1);
    now = 60000;
    assert.equal(pen.penOnly(), true, 'a pen held down keeps it on');
    pen.up(1);
    now += 29000;
    assert.equal(pen.penOnly(), true);
    pen.touch();
    now += 29000;
    assert.equal(pen.penOnly(), true, 'pen input (a hover or move) extends it');
    now += 1500;
    assert.equal(pen.penOnly(), false);
});

test('a touch is a palm while the pen is down and shortly after it lifts', () => {
    let now = 0;
    const pen = status.penPolicy(() => now);
    assert.equal(pen.palm(), false);
    pen.down(3);
    assert.equal(pen.palm(), true);
    pen.up(3);
    now += 300;
    assert.equal(pen.palm(), true);
    now += 400;
    assert.equal(pen.palm(), false);
});

function commitModule(invoke) {
    const calls = { toasts: [], hidden: 0, ended: 0 };
    const timers = [];
    const window = {
        bwShapeGeometry: require(js + 'shape-geometry.js'),
        bwShapeLive: { draw() {}, end() { calls.ended++; } },
        bwShapeStatus: { toast(m) { calls.toasts.push(m); }, hideToast() { calls.hidden++; } },
    };
    const ctx = { window, setTimeout: (f) => { timers.push(f); return timers.length; }, clearTimeout: () => {} };
    vm.runInNewContext(readFileSync(new URL(js + 'wall-shape-commit.js', import.meta.url), 'utf8'), ctx);
    const pending = new Set();
    const op = { kind: 'drag', ctx: {}, edit: { holdId: 'h', cx: 0.5, cy: 0.5 }, env: { editor: { invokeMethodAsync: invoke } } };
    const result = { points: [{ dx: 0, dy: 0 }, { dx: 0.01, dy: 0 }, { dx: 0, dy: 0.01 }], control: false };
    return { calls, timers, pending, send: () => window.bwShapeCommit.send(op, result, pending) };
}

test('a commit the server answers takes the preview away without a toast', async () => {
    const m = commitModule(async () => true);
    m.send();
    assert.ok(m.pending.has('h'));
    await new Promise((r) => setImmediate(r));
    assert.equal(m.pending.size, 0);
    assert.equal(m.calls.ended, 1);
    assert.deepEqual(m.calls.toasts, []);
});

test('a failed commit (the circuit is gone) says the edit was not saved', async () => {
    const m = commitModule(async () => { throw new Error('circuit gone'); });
    m.send();
    await new Promise((r) => setImmediate(r));
    assert.equal(m.calls.ended, 1);
    assert.equal(m.calls.toasts.length, 1);
    assert.match(m.calls.toasts[0], /wasn't saved/);
});

test('a commit that never gets an answer times out with the same message, and a late "saved" takes it back', async () => {
    let answer;
    const m = commitModule(() => new Promise((r) => { answer = r; }));
    m.send();
    m.timers[0]();
    assert.equal(m.pending.size, 0);
    assert.equal(m.calls.toasts.length, 1);
    answer(true);
    await new Promise((r) => setImmediate(r));
    assert.equal(m.calls.hidden, 1);
    assert.equal(m.calls.ended, 1);
});
