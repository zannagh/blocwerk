// The photo-real ladder's step up: a level already on the device is jumped to (wall3d-splat-ladder.js stepTarget), so
// the levels the asset cache does not keep (asset-budget.js keeps a wall's smallest and largest) are not downloaded
// again on every visit; and the cap level, never probed, is recorded as fine once it showed steadily
// (wall3d-splat-policy.js createSteadyWatch), so Auto and High jump too. Run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';

const stored = new Map();
globalThis.location ??= { search: '' };
globalThis.localStorage ??= {
    getItem: k => (stored.has(k) ? stored.get(k) : null),
    setItem: (k, v) => stored.set(k, String(v)),
    removeItem: k => stored.delete(k),
};
const { fileKeyOf, remembered, rememberSuccess, shaOf, stepTarget } = await import('../../src/Blocwerk.Web/wwwroot/js/wall3d-splat-ladder.js');
const { createSteadyWatch, STEADY_FRAMES } = await import('../../src/Blocwerk.Web/wwwroot/js/wall3d-splat-policy.js');

const sha = c => c.repeat(64);
const W = '/api/walls/e0959f0a-cc71-4da1-be39-889d8bbc4822';
const levels = [
    { url: `${W}/a/${sha('0')}`, splats: 40_000 },
    { url: `${W}/a/${sha('1')}`, splats: 120_000 },
    { url: `${W}/a/${sha('2')}?v=3`, splats: 250_000 },
    { url: `${W}/a/${sha('3')}`, splats: 800_000 },
    { url: `${W}/a/${sha('4')}?token=t`, splats: 3_200_000 },
];

test('shaOf reads the sha of an /a/ URL, with or without a query', () => {
    assert.equal(shaOf(levels[2]), sha('2'));
    assert.equal(shaOf(levels[4]), sha('4'));
    assert.equal(shaOf({ url: '/scene.spz' }), null);
    assert.equal(shaOf(undefined), null);
});

test('Ultra jumps from the smallest level straight to the largest one on the device', () => {
    assert.equal(stepTarget(levels, 0, 4, new Set([sha('0'), sha('4')]), undefined, true), 4);
});

test('Ultra walks one level when nothing above the next is cached or the cap is below it', () => {
    assert.equal(stepTarget(levels, 0, 3, new Set([sha('0'), sha('4')]), undefined, true), 1);
    assert.equal(stepTarget(levels, 0, 4, new Set([sha('0')]), undefined, true), 1);
    assert.equal(stepTarget(levels, 0, 4, null, undefined, true), 1);
    assert.equal(stepTarget(levels, 4, 4, new Set([sha('4')]), undefined, true), null, 'nothing above the cap');
});

test('outside Ultra only a level that ran fine here loads unmeasured; otherwise the step is probed (null)', () => {
    const cached = new Set([sha('0'), sha('3')]);
    assert.equal(stepTarget(levels, 0, 3, cached, 800_000, false), 3);
    assert.equal(stepTarget(levels, 0, 3, cached, 120_000, false), 1, 'the next level ran fine: walk to it');
    assert.equal(stepTarget(levels, 0, 3, cached, undefined, false), null);
});

// Draws `count` frames of `index` evenly from `from` to `to` (ms); true when one of them recorded the level.
function draw(watch, index, from, to, count = STEADY_FRAMES) {
    let fired = false;
    for (let i = 0; i < count; i++) {
        fired = watch.due(index, from + ((to - from) * i) / Math.max(1, count - 1)) || fired;
    }
    return fired;
}

test('the steady watch fires once, for the armed level, after its time and its frames, and never after a spoil', () => {
    const watch = createSteadyWatch(10_000);
    watch.arm(3, 0);
    assert.equal(draw(watch, 3, 0, 9_999), false, 'too early');
    assert.equal(draw(watch, 2, 10_000, 11_000), false, 'another level shows');
    assert.equal(draw(watch, 3, 10_000, 11_000), true);
    assert.equal(draw(watch, 3, 11_000, 30_000), false, 'once');

    watch.arm(3, 0);
    watch.spoil();
    assert.equal(draw(watch, 3, 10_000, 60_000), false, 'a step down or a lost context spoils it');
});

test('fewer than STEADY_FRAMES drawn frames at the cap do not record it, however long it showed', () => {
    const watch = createSteadyWatch(10_000);
    watch.arm(3, 0);
    assert.equal(draw(watch, 3, 10_000, 600_000, STEADY_FRAMES - 1), false);
    assert.equal(watch.due(3, 600_001), true, 'the frame that reaches the count records it');
});

test('hidden, then shown again, does not record at once: the time restarts from the show', () => {
    const watch = createSteadyWatch(10_000);
    watch.arm(3, 0);
    draw(watch, 3, 0, 4_000);
    watch.spoil();                    // the page went hidden at 4 s
    watch.arm(3, 60_000);             // and shows again a minute later
    assert.equal(draw(watch, 3, 60_000, 61_000), false, 'the hidden minute does not count');
    assert.equal(draw(watch, 3, 70_000, 71_000), true, '10 s of visible time after the show');
});

test('inactive, then active again, re-arms: the watch counts from the new activation', () => {
    const watch = createSteadyWatch(10_000);
    watch.arm(3, 0);
    watch.spoil();                    // the mode was left before it was steady
    assert.equal(draw(watch, 3, 20_000, 21_000), false, 'not armed while inactive');
    watch.arm(3, 30_000);             // the mode picked again
    assert.equal(draw(watch, 3, 30_000, 39_000), false);
    assert.equal(draw(watch, 3, 40_000, 41_000), true);
});

test('Auto: the cap level recorded as steady makes the next visit jump to it from the cached smallest level', () => {
    const key = fileKeyOf(levels);
    const cached = new Set([sha('0'), sha('3')]);
    assert.equal(stepTarget(levels, 0, 3, cached, remembered(key).okSplats, false), null, 'first visit: probe every rung');

    const watch = createSteadyWatch(10_000);
    watch.arm(3, 0);
    if (draw(watch, 3, 0, 10_000)) rememberSuccess(key, levels[3]);

    assert.equal(remembered(key).okSplats, 800_000);
    assert.equal(stepTarget(levels, 0, 3, cached, remembered(key).okSplats, false), 3, 'next visit: straight to the cap');
});
