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
