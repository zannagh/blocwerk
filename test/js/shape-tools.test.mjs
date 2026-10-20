// The wall editor's Shape tool geometry, run by node with no dependencies:
//
//     node --test test/js/
//
// densify must give exactly what HoldOutlineSpline.Densify gives (the fixture is written and checked by
// test/Blocwerk.Core.Tests/HoldOutlineSplineFixtureTests.cs); the eraser and the brush must never hand the server
// an outline that crosses itself.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { readFileSync } from 'node:fs';

const require = createRequire(import.meta.url);
const js = '../../src/Blocwerk.Web/wwwroot/js/';
const geometry = require(js + 'shape-geometry.js');
const raster = require(js + 'shape-raster.js');
const brush = require(js + 'shape-brush.js');

const fixture = JSON.parse(readFileSync(new URL('./fixtures/spline-densify.json', import.meta.url), 'utf8'));

const toPoints = (flat) => Array.from({ length: flat.length / 2 }, (_, i) => ({ dx: flat[2 * i], dy: flat[(2 * i) + 1] }));
const flatten = (points) => points.flatMap((p) => [p.dx, p.dy]);
const area = (poly) => Math.abs(geometry.signedArea(poly));
const square = [[0, 0], [100, 0], [100, 100], [0, 100]];

test('densify gives the C# curve exactly, for every fixture case', () => {
    assert.ok(fixture.length >= 9);
    for (const c of fixture) {
        assert.deepEqual(flatten(geometry.densify(toPoints(c.Input))), c.Output, c.Name);
    }
});

test('round5 rounds to five decimals, half to even like .NET Math.Round', () => {
    assert.equal(geometry.round5(0.123456789), 0.12346);
    assert.equal(geometry.round5(-0.123456789), -0.12346);
    // A tie: 2.5 / 1e5 scales back to exactly 2.5, which .NET rounds to the even 2 (Math.round would give 3).
    assert.equal(2.5 / 1e5 * 1e5, 2.5);
    assert.equal(geometry.round5(2.5 / 1e5), 2 / 1e5);
    assert.equal(geometry.round5(-2.5 / 1e5), -2 / 1e5);
});

test('isSimple tells a square from a bow tie', () => {
    assert.equal(geometry.isSimple(square), true);
    assert.equal(geometry.isSimple([[0, 0], [100, 100], [100, 0], [0, 100]]), false);
});

test('eraser removes exactly the covered points', () => {
    const ring = Array.from({ length: 12 }, (_, i) => [100 * Math.cos(i * Math.PI / 6), 100 * Math.sin(i * Math.PI / 6)]);
    const keep = ring.map((_, i) => i);
    const step = geometry.erase(ring, keep, [100, -5], [100, 5], 10, (k) => k.map((i) => ring[i]));
    assert.deepEqual(step.keep, keep.filter((i) => i !== 0));
    assert.deepEqual(step.blocked, []);
});

test('eraser never leaves fewer than three points', () => {
    const tri = [[0, 0], [10, 0], [0, 10]];
    const step = geometry.erase(tri, [0, 1, 2], [-50, -50], [50, 50], 100, (k) => k.map((i) => tri[i]));
    assert.deepEqual(step.keep, [0, 1, 2]);
    assert.deepEqual(step.blocked, [0, 1, 2]);
});

test('eraser refuses a removal that would make the outline cross itself', () => {
    const poly = [[3, 5], [6, 2], [9, 5], [9, 6], [6, 5], [5, 7]];
    assert.equal(geometry.isSimple(poly), true);
    const step = geometry.erase(poly, [0, 1, 2, 3, 4, 5], [6, 2], [6, 2], 0.5, (k) => k.map((i) => poly[i]));
    assert.deepEqual(step.blocked, [1]);
    assert.equal(step.keep.length, 6);
});

test('eraser on a smooth outline checks the curve of the remaining control points', () => {
    const control = Array.from({ length: 8 }, (_, i) => ({ dx: Math.cos(i * Math.PI / 4), dy: Math.sin(i * Math.PI / 4) }));
    const px = control.map((p) => [p.dx * 100, p.dy * 100]);
    const curveOf = (k) => geometry.densify(k.map((i) => control[i])).map((p) => [p.dx * 100, p.dy * 100]);
    const step = geometry.erase(px, px.map((_, i) => i), [90, -60], [90, 60], 15, curveOf);
    assert.equal(step.keep.length, 7);
    assert.equal(geometry.isSimple(curveOf(step.keep)), true);
});

test('brush adds a stroke to the outline', () => {
    const out = brush.apply(square, [{ x: 100, y: 50, r: 20 }, { x: 140, y: 50, r: 20 }], false);
    assert.ok(out && geometry.isSimple(out));
    assert.ok(out.length <= brush.MAX_POINTS);
    assert.ok(Math.abs(area(out) - 12228) < 250, String(area(out)));
    assert.equal(geometry.signedArea(out) > 0, geometry.signedArea(square) > 0);
});

test('brush with subtract cuts the stroke out', () => {
    const out = brush.apply(square, [{ x: 50, y: -10, r: 20 }, { x: 50, y: 40, r: 20 }], true);
    assert.ok(out && geometry.isSimple(out));
    assert.ok(Math.abs(area(out) - 7772) < 250, String(area(out)));
});

test('a cut in the middle leaves the outer outline (no hole is made)', () => {
    const out = brush.apply(square, [{ x: 50, y: 50, r: 20 }], true);
    assert.ok(out && Math.abs(area(out) - 10000) < 250);
});

test('a stroke that does not touch the outline adds nothing', () => {
    const out = brush.apply(square, [{ x: 300, y: 300, r: 10 }, { x: 320, y: 300, r: 10 }], false);
    assert.ok(out && Math.abs(area(out) - 10000) < 250);
});

test('cutting everything away gives nothing to save', () => {
    assert.equal(brush.apply(square, [{ x: 50, y: 50, r: 200 }], true), null);
});

test('a long wobbly pen stroke stays simple and below the point budget', () => {
    const stroke = Array.from({ length: 200 }, (_, i) => ({ x: 100 + (i * 1.5), y: 50 + (25 * Math.sin(i / 7)), r: 4 + (8 * Math.abs(Math.sin(i / 11))) }));
    const out = brush.apply(square, stroke, false, { maxCells: 320 });
    assert.ok(out && geometry.isSimple(out));
    assert.ok(out.length <= brush.MAX_POINTS);
    assert.ok(area(out) > 10000);
});

test('marching squares cuts a saddle, so two diagonal cells trace as two loops', () => {
    const g = raster.makeGrid({ x0: 0, y0: 0, x1: 2, y1: 2 }, 2);
    g.mask.fill(0);
    const at = (i, j) => (j * g.w) + i;
    g.mask[at(2, 2)] = 1;
    g.mask[at(3, 3)] = 1;
    const loops = brush.trace(g);
    assert.equal(loops.length, 2);
    loops.forEach((loop) => assert.equal(loop.length, 4));
});

test('the preview is fast enough to run every frame', () => {
    const control = toPoints(fixture.find((c) => c.Name === 'editable-32').Input);
    let t = performance.now();
    for (let i = 0; i < 1000; i++) {
        geometry.densify(control);
    }

    const densifyMs = (performance.now() - t) / 1000;
    const stroke = Array.from({ length: 60 }, (_, i) => ({ x: 100 + i, y: 50, r: 14 }));
    t = performance.now();
    for (let i = 0; i < 20; i++) {
        brush.apply(square, stroke, false, { maxCells: 160 });
    }

    const brushMs = (performance.now() - t) / 20;
    console.log(`densify(32) ${densifyMs.toFixed(3)} ms, brush preview (60 samples) ${brushMs.toFixed(2)} ms`);
    assert.ok(densifyMs < 2 && brushMs < 40);
});
