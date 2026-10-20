// The split tool's browser side (hold-split-line.js), run by node with no dependencies:
//
//     node --test test/js/
//
// The live preview must cut the outline the way HoldOutlineBoolean.Split does (the parts tile the hold, the most even
// stretch of a stroke that crosses twice is the cut), and the palm rule must match the Shape tool's.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { splitPreview, touchRole, withoutLoops, PEN_GRACE_MS } from '../../src/Blocwerk.Web/wwwroot/js/hold-split-geometry.js';

const area = (ring) => {
    let s = 0;
    for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
        s += (ring[j][0] * ring[i][1]) - (ring[j][1] * ring[i][0]);
    }
    return Math.abs(s / 2);
};
const square = [[40, 40], [60, 40], [60, 60], [40, 60]];

test('a straight cut across the square gives two parts that tile it, larger first', () => {
    const parts = splitPreview(square, [[45, 30], [45, 70]]);
    assert.deepEqual(parts.map(area), [300, 100]);
});

test('a freehand cut that wiggles still tiles the square', () => {
    const line = Array.from({ length: 20 }, (_, i) => [50 + (3 * Math.sin(i)), 30 + (i * 2.2)]);
    const parts = splitPreview(square, line);
    assert.ok(Math.abs(area(parts[0]) + area(parts[1]) - 400) < 1e-9);
});

test('a stroke that does not cross the whole hold gives no preview', () => {
    assert.equal(splitPreview(square, [[45, 30], [45, 50]]), null);
    assert.equal(splitPreview(square, [[45, 30]]), null);
});

test('a stroke that runs inside a U-shaped hold twice is cut where it halves it best', () => {
    // A U: two arms (x 0..10 and 30..40) joined at the bottom (y 30..40).
    const u = [[0, 0], [10, 0], [10, 30], [30, 30], [30, 0], [40, 0], [40, 40], [0, 40]];
    const parts = splitPreview(u, [[-5, 20], [45, 20]]);
    const total = area(u);
    assert.ok(Math.abs(area(parts[0]) + area(parts[1]) - total) < 1e-9);
    assert.equal(area(parts[1]), 200);
});

test('palm rule: a touch while the pen is down, or just after it lifted, is a palm', () => {
    const pen = { down: new Set([7]), upAt: -Infinity, seen: true };
    assert.equal(touchRole(pen, 'touch', 1000), 'palm');
    assert.equal(touchRole(pen, 'pen', 1000), 'draw');
    pen.down.clear();
    pen.upAt = 1000;
    assert.equal(touchRole(pen, 'touch', 1000 + PEN_GRACE_MS - 1), 'palm');
    assert.equal(touchRole(pen, 'touch', 1000 + PEN_GRACE_MS + 1), 'navigate');
});

test('palm rule: without a pen a finger draws, and the mouse always draws', () => {
    const pen = { down: new Set(), upAt: -Infinity, seen: false };
    assert.equal(touchRole(pen, 'touch', 5), 'draw');
    assert.equal(touchRole(pen, 'mouse', 5), 'draw');
});

test('a scribbled loop in the stroke is cut out, so the parts still tile the hold', () => {
    const loop = [[50, 30], [50, 49], [53, 51], [47, 51], [50, 49.5], [50, 70]];
    const parts = splitPreview(square, loop);
    assert.ok(Math.abs(area(parts[0]) + area(parts[1]) - 400) < 1e-6);
    assert.ok(withoutLoops(loop).length < loop.length);
});
