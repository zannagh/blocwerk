// The edit lease's presence reporter (edit-presence.js): run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const presence = require('../../src/Blocwerk.Web/wwwroot/js/edit-presence.js');

function harness(active = true) {
    const sent = [];
    const state = { now: 0, active };
    const reporter = presence.createReporter({
        now: () => state.now,
        active: () => state.active,
        send: (...args) => sent.push(args),
    });
    return { sent, state, reporter };
}

test('only trusted pointer down, drags, keys and the wheel are user input', () => {
    const yes = [
        { type: 'pointerdown', isTrusted: true },
        { type: 'pointermove', isTrusted: true, buttons: 1 },
        { type: 'keydown', isTrusted: true },
        { type: 'wheel', isTrusted: true },
    ];
    const no = [
        { type: 'pointermove', isTrusted: true, buttons: 0 },
        { type: 'pointerdown', isTrusted: false },
        { type: 'focus', isTrusted: true },
        { type: 'resize', isTrusted: true },
        { type: 'visibilitychange', isTrusted: true },
        { type: 'pointerover', isTrusted: true },
    ];
    yes.forEach((e) => assert.equal(presence.isUserInput(e), true, e.type));
    no.forEach((e) => assert.equal(presence.isUserInput(e), false, `${e.type} ${e.isTrusted} ${e.buttons}`));
});

test('input is reported at most once per throttle window', () => {
    const { sent, state, reporter } = harness();
    assert.equal(reporter.input({ type: 'pointerdown' }), true);
    state.now = presence.INPUT_EVERY_MS - 1;
    assert.equal(reporter.input({ type: 'keydown' }), false);
    state.now = presence.INPUT_EVERY_MS;
    assert.equal(reporter.input({ type: 'wheel' }), true);
    assert.deepEqual(sent, [['OnUserInput'], ['OnUserInput']]);
});

test('nothing is reported while no editor is open', () => {
    const { sent, reporter } = harness(false);
    reporter.input({ type: 'pointerdown' });
    reporter.visibility(false);
    assert.deepEqual(sent, []);
});

test('hiding and showing are reported once per change, and showing is not input', () => {
    const { sent, reporter } = harness();
    reporter.visibility(false);
    reporter.visibility(false);
    reporter.visibility(true);
    assert.deepEqual(sent, [['OnVisibility', false], ['OnVisibility', true]]);
});

test('the first input after the page was hidden is reported at once, not throttled', () => {
    const { sent, state, reporter } = harness();
    reporter.input({ type: 'pointerdown' });
    reporter.visibility(false);
    state.now = 1000;
    assert.equal(reporter.input({ type: 'pointerdown' }), true);
    assert.deepEqual(sent.at(-1), ['OnUserInput']);
});

// The review's probe: the phone is unlocked while the socket is still reconnecting. The visible report and the first
// tap fail to send; neither may be remembered as sent, and the next tap once connected must go through at once.
test('a report lost to a reconnect is rolled back, so the next tap is not throttled away', async () => {
    let t = 0;
    let up = true;
    const server = [];
    const r = presence.createReporter({
        now: () => t,
        active: () => true,
        send: (m, a) => (up ? (server.push([t, m, a]), Promise.resolve()) : Promise.reject(new Error('reconnecting'))),
    });
    const flush = () => new Promise((resolve) => setImmediate(resolve));

    r.input({ type: 'pointerdown', isTrusted: true });
    t = 60000; r.visibility(false);
    t = 600000; up = false; r.visibility(true);
    r.input({ type: 'pointerdown', isTrusted: true });
    await flush();
    t = 605000; up = true;
    assert.equal(r.input({ type: 'pointerdown', isTrusted: true }), true, 'the tap after the reconnect is sent');
    await flush();
    assert.deepEqual(server.map((s) => s[1]), ['OnUserInput', 'OnVisibility', 'OnUserInput']);
    assert.equal(server.at(-1)[0], 605000);
});

test('resync re-sends the current visibility even when it did not change', () => {
    const { sent, reporter } = harness();
    reporter.visibility(true);
    reporter.resync(true);
    assert.deepEqual(sent, [['OnVisibility', true], ['OnVisibility', true]]);
});

test('a send that throws synchronously is rolled back too', async () => {
    let fail = true;
    const sent = [];
    const r = presence.createReporter({
        now: () => 0,
        active: () => true,
        send: (...args) => { if (fail) { throw new Error('no handle'); } sent.push(args); return Promise.resolve(); },
    });
    r.visibility(false);
    await new Promise((resolve) => setImmediate(resolve));
    fail = false;
    assert.equal(r.visibility(false), true, 'the failed hidden report is sent again');
    assert.deepEqual(sent, [['OnVisibility', false]]);
});
