/*
 * The raster half of the Shape tool's brush (shape-brush.js traces the result): a painted stroke ADDED to a
 * hold's outline, or cut out of it. Pure, no DOM, so node runs it in test/js/shape-tools.test.mjs.
 *
 * A polygon boolean done by raster, deliberately: the outline and the stroke (round-capped capsules, one per
 * pair of pointer samples, each with its own radius for pen pressure) are drawn into a grid of at most
 * MAX_CELLS a side, the part connected to the old outline is kept, its holes are filled, and its boundary is
 * traced by marching squares and simplified (Douglas-Peucker) to a sensible point count. The grid is about a
 * screen pixel fine, so the result is as exact as the stroke the user could see. Everything is in screen px.
 * The server validates what is finally sent (ShapeEditValidation).
 */
(function (root, factory) {
    'use strict';
    const api = factory();
    if (typeof module === 'object' && module.exports) {
        module.exports = api;
    } else {
        root.bwShapeRaster = api;
    }
})(typeof self !== 'undefined' ? self : this, function () {
    'use strict';

    const MAX_CELLS = 256;
    const MIN_CELL_PX = 0.75;

    function bounds(polygon, stroke, subtract) {
        let x0 = Infinity;
        let y0 = Infinity;
        let x1 = -Infinity;
        let y1 = -Infinity;
        const grow = function (x, y, r) {
            x0 = Math.min(x0, x - r);
            y0 = Math.min(y0, y - r);
            x1 = Math.max(x1, x + r);
            y1 = Math.max(y1, y + r);
        };
        polygon.forEach(function (p) { grow(p[0], p[1], 0); });
        if (!subtract) {
            stroke.forEach(function (s) { grow(s.x, s.y, s.r); });
        }

        return { x0: x0, y0: y0, x1: x1, y1: y1 };
    }

    /** A grid over the box, two empty cells of margin all round so every contour closes. */
    function makeGrid(box, maxCells) {
        const size = Math.max(box.x1 - box.x0, box.y1 - box.y0, 1);
        const cell = Math.max(size / (maxCells || MAX_CELLS), MIN_CELL_PX);
        const w = Math.ceil((box.x1 - box.x0) / cell) + 4;
        const h = Math.ceil((box.y1 - box.y0) / cell) + 4;
        return { cell: cell, w: w, h: h, ox: box.x0 - (2 * cell), oy: box.y0 - (2 * cell), mask: new Uint8Array(w * h) };
    }

    /** Fills the polygon (even-odd, sampled at cell centres) with `value`. */
    function fillPolygon(g, polygon, value) {
        const n = polygon.length;
        for (let j = 0; j < g.h; j++) {
            const y = g.oy + ((j + 0.5) * g.cell);
            const xs = [];
            for (let i = 0, k = n - 1; i < n; k = i++) {
                const a = polygon[k];
                const b = polygon[i];
                if ((a[1] > y) !== (b[1] > y)) {
                    xs.push(a[0] + (((y - a[1]) / (b[1] - a[1])) * (b[0] - a[0])));
                }
            }

            xs.sort(function (p, q) { return p - q; });
            for (let s = 0; s + 1 < xs.length; s += 2) {
                const from = Math.max(0, Math.ceil(((xs[s] - g.ox) / g.cell) - 0.5));
                const to = Math.min(g.w - 1, Math.floor(((xs[s + 1] - g.ox) / g.cell) - 0.5));
                for (let i = from; i <= to; i++) {
                    g.mask[(j * g.w) + i] = value;
                }
            }
        }
    }

    /** Paints one capsule between two stroke samples, the radius blending from one to the other. */
    function paintCapsule(g, a, b, value) {
        const r = Math.max(a.r, b.r);
        const i0 = Math.max(0, Math.floor((Math.min(a.x, b.x) - r - g.ox) / g.cell));
        const i1 = Math.min(g.w - 1, Math.ceil((Math.max(a.x, b.x) + r - g.ox) / g.cell));
        const j0 = Math.max(0, Math.floor((Math.min(a.y, b.y) - r - g.oy) / g.cell));
        const j1 = Math.min(g.h - 1, Math.ceil((Math.max(a.y, b.y) + r - g.oy) / g.cell));
        const vx = b.x - a.x;
        const vy = b.y - a.y;
        const len2 = (vx * vx) + (vy * vy);
        for (let j = j0; j <= j1; j++) {
            const y = g.oy + ((j + 0.5) * g.cell);
            for (let i = i0; i <= i1; i++) {
                const x = g.ox + ((i + 0.5) * g.cell);
                const t = len2 > 0 ? Math.max(0, Math.min(1, (((x - a.x) * vx) + ((y - a.y) * vy)) / len2)) : 0;
                const radius = a.r + ((b.r - a.r) * t);
                if (Math.hypot(x - (a.x + (t * vx)), y - (a.y + (t * vy))) <= radius) {
                    g.mask[(j * g.w) + i] = value;
                }
            }
        }
    }

    function paintStroke(g, stroke, value) {
        for (let k = 0; k < stroke.length; k++) {
            paintCapsule(g, stroke[k], stroke[Math.min(k + 1, stroke.length - 1)], value);
        }
    }

    /** Keeps only the 4-connected solid part that covers most of `original`; false when nothing does. */
    function keepMainPart(g, original) {
        const label = new Int32Array(g.w * g.h);
        const queue = new Int32Array(g.w * g.h);
        let best = 0;
        let bestScore = 0;
        let next = 0;
        for (let start = 0; start < label.length; start++) {
            if (!g.mask[start] || label[start]) {
                continue;
            }

            next++;
            let score = 0;
            let head = 0;
            let tail = 0;
            queue[tail++] = start;
            label[start] = next;
            while (head < tail) {
                const c = queue[head++];
                score += original[c] ? 1 : 0;
                const i = c % g.w;
                const nb = [i > 0 ? c - 1 : -1, i < g.w - 1 ? c + 1 : -1, c - g.w, c + g.w];
                for (const d of nb) {
                    if (d >= 0 && d < label.length && g.mask[d] && !label[d]) {
                        label[d] = next;
                        queue[tail++] = d;
                    }
                }
            }

            if (score > bestScore) {
                bestScore = score;
                best = next;
            }
        }

        for (let c = 0; c < label.length; c++) {
            g.mask[c] = label[c] === best && best > 0 ? 1 : 0;
        }

        return best > 0;
    }

    /** Fills every empty cell the outside cannot reach (8-connected, matching the separated saddles below). */
    function fillHoles(g) {
        const outside = new Uint8Array(g.w * g.h);
        const queue = new Int32Array(g.w * g.h);
        let head = 0;
        let tail = 0;
        outside[0] = 1;
        queue[tail++] = 0;
        while (head < tail) {
            const c = queue[head++];
            const i = c % g.w;
            const j = (c - i) / g.w;
            for (let dj = -1; dj <= 1; dj++) {
                for (let di = -1; di <= 1; di++) {
                    const x = i + di;
                    const y = j + dj;
                    const d = (y * g.w) + x;
                    if (x >= 0 && y >= 0 && x < g.w && y < g.h && !g.mask[d] && !outside[d]) {
                        outside[d] = 1;
                        queue[tail++] = d;
                    }
                }
            }
        }

        for (let c = 0; c < outside.length; c++) {
            g.mask[c] = outside[c] ? 0 : 1;
        }
    }

    return {
        bounds: bounds,
        makeGrid: makeGrid,
        fillPolygon: fillPolygon,
        paintStroke: paintStroke,
        keepMainPart: keepMainPart,
        fillHoles: fillHoles,
    };
});
