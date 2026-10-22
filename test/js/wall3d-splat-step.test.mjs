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
const { createSteadyWatch } = await import('../../src/Blocwerk.Web/wwwroot/js/wall3d-splat-policy.js');

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

test('the steady watch fires once, for the armed level, after its time, and never after a spoil', () => {
    const watch = createSteadyWatch(10_000);
    watch.arm(3, 1000);
    assert.equal(watch.due(3, 10_999), false, 'too early');
    assert.equal(watch.due(2, 11_000), false, 'another level shows');
    assert.equal(watch.due(3, 11_000), true);
    assert.equal(watch.due(3, 30_000), false, 'once');

    watch.arm(3, 0);
    watch.spoil();
    assert.equal(watch.due(3, 60_000), false, 'a step down or a lost context spoils it');
});

test('Auto: the cap level recorded as steady makes the next visit jump to it from the cached smallest level', () => {
    const key = fileKeyOf(levels);
    const cached = new Set([sha('0'), sha('3')]);
    assert.equal(stepTarget(levels, 0, 3, cached, remembered(key).okSplats, false), null, 'first visit: probe every rung');

    const watch = createSteadyWatch(10_000);
    watch.arm(3, 0);
    if (watch.due(3, 10_000)) rememberSuccess(key, levels[3]);

    assert.equal(remembered(key).okSplats, 800_000);
    assert.equal(stepTarget(levels, 0, 3, cached, remembered(key).okSplats, false), 3, 'next visit: straight to the cap');
});
