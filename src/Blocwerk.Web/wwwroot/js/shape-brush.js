/*
 * The Shape tool's brush result: shape-raster.js draws the outline and the stroke into a grid, this traces
 * the kept area's boundary (marching squares on the cell centres, saddles cut so the trace never touches
 * itself) and simplifies it (Douglas-Peucker, the tolerance raised until the outline has at most maxPoints).
 * Pure, no DOM: window.bwShapeBrush in the page, module.exports under node (test/js/shape-tools.test.mjs).
 */
(function (root, factory) {
    'use strict';
    const load = function (name, file) {
        return root[name] || (typeof require === 'function' ? require(file) : null);
    };
    const api = factory(load('bwShapeRaster', './shape-raster.js'), load('bwShapeGeometry', './shape-geometry.js'));
    if (typeof module === 'object' && module.exports) {
        module.exports = api;
    } else {
        root.bwShapeBrush = api;
    }
})(typeof self !== 'undefined' ? self : this, function (raster, geometry) {
    'use strict';

    const MAX_POINTS = 120;

    /** Every closed boundary of the grid's solid cells, longest first, as [x, y] px. */
    function trace(g) {
        const adj = new Int32Array(g.w * g.h * 4).fill(-1);
        const link = function (p, q) {
            adj[(p * 2) + (adj[p * 2] < 0 ? 0 : 1)] = q;
            adj[(q * 2) + (adj[q * 2] < 0 ? 0 : 1)] = p;
        };
        const m = function (i, j) { return g.mask[(j * g.w) + i]; };
        for (let j = 0; j + 1 < g.h; j++) {
            for (let i = 0; i + 1 < g.w; i++) {
                linkSquare(link, m(i, j), m(i + 1, j), m(i + 1, j + 1), m(i, j + 1), sides(g, i, j));
            }
        }

        const loops = [];
        const seen = new Uint8Array(g.w * g.h * 2);
        for (let start = 0; start < seen.length; start++) {
            if (adj[start * 2] >= 0 && !seen[start]) {
                loops.push(walk(g, adj, seen, start));
            }
        }

        return loops.sort(function (a, b) { return b.length - a.length; });
    }

    function sides(g, i, j) {
        const h = function (x, y) { return 2 * ((y * g.w) + x); };
        return { top: h(i, j), bottom: h(i, j + 1), left: h(i, j) + 1, right: h(i + 1, j) + 1 };
    }

    function linkSquare(link, a, b, c, d, s) {
        const crossed = [];
        if (a !== b) {
            crossed.push(s.top);
        }

        if (b !== c) {
            crossed.push(s.right);
        }

        if (c !== d) {
            crossed.push(s.bottom);
        }

        if (d !== a) {
            crossed.push(s.left);
        }

        if (crossed.length === 2) {
            link(crossed[0], crossed[1]);
        } else if (crossed.length === 4 && a) {
            // Saddle: the two solid corners are NOT joined (4-connected solid), so each is cut off on its own.
            link(s.top, s.left);
            link(s.right, s.bottom);
        } else if (crossed.length === 4) {
            link(s.top, s.right);
            link(s.left, s.bottom);
        }
    }

    function position(g, id) {
        const c = id >> 1;
        const i = c % g.w;
        const j = (c - i) / g.w;
        return (id & 1) === 0
            ? [g.ox + ((i + 1) * g.cell), g.oy + ((j + 0.5) * g.cell)]
            : [g.ox + ((i + 0.5) * g.cell), g.oy + ((j + 1) * g.cell)];
    }

    function walk(g, adj, seen, start) {
        const loop = [];
        let prev = -1;
        let cur = start;
        while (cur >= 0 && !seen[cur]) {
            seen[cur] = 1;
            loop.push(position(g, cur));
            const next = adj[cur * 2] !== prev ? adj[cur * 2] : adj[(cur * 2) + 1];
            prev = cur;
            cur = next;
        }

        return loop;
    }

    function dpChain(points, from, to, tol, keep) {
        const stack = [[from, to]];
        while (stack.length) {
            const [a, b] = stack.pop();
            let worst = -1;
            let index = -1;
            for (let k = a + 1; k < b; k++) {
                const d = geometry.distanceToSegment(points[k], points[a], points[b]);
                if (d > worst) {
                    worst = d;
                    index = k;
                }
            }

            if (index >= 0 && worst > tol) {
                keep[index] = 1;
                stack.push([a, index], [index, b]);
            }
        }
    }

    /** Douglas-Peucker on a closed ring: split at the point farthest from the first, simplify both halves. */
    function simplifyRing(points, tol) {
        const n = points.length;
        let far = 0;
        for (let k = 1; k < n; k++) {
            if (Math.hypot(points[k][0] - points[0][0], points[k][1] - points[0][1]) > Math.hypot(points[far][0] - points[0][0], points[far][1] - points[0][1])) {
                far = k;
            }
        }

        const ring = points.concat([points[0]]);
        const keep = new Uint8Array(n + 1);
        keep[0] = keep[far] = keep[n] = 1;
        dpChain(ring, 0, far, tol, keep);
        dpChain(ring, far, n, tol, keep);
        return points.filter(function (_, k) { return keep[k]; });
    }

    /** The simplest outline with at most maxPoints points that is still simple; null when there is none. */
    function simplify(loop, cell, maxPoints) {
        let tol = cell * 0.6;
        let out = simplifyRing(loop, tol);
        while (out.length > maxPoints) {
            tol *= 1.4;
            out = simplifyRing(loop, tol);
        }

        for (let tries = 0; tries < 6 && !geometry.isSimple(out); tries++) {
            tol /= 1.25;
            out = simplifyRing(loop, tol);
        }

        return out.length >= 3 && out.length <= maxPoints * 1.5 && geometry.isSimple(out) ? out : null;
    }

    /**
     * The outline after a brush stroke. `polygon` is the current outline and `stroke` the samples
     * [{x, y, r}], all in screen px; `subtract` cuts the stroke out instead of adding it. Returns the new
     * outline as [x, y] px, wound like the old one, or null when nothing sensible is left.
     */
    function apply(polygon, stroke, subtract, opts) {
        if (!stroke.length || polygon.length < 3) {
            return null;
        }

        const options = opts || {};
        const g = raster.makeGrid(raster.bounds(polygon, stroke, subtract), options.maxCells);
        raster.fillPolygon(g, polygon, 1);
        const original = g.mask.slice();
        raster.paintStroke(g, stroke, subtract ? 0 : 1);
        if (!raster.keepMainPart(g, original)) {
            return null;
        }

        raster.fillHoles(g);
        const loops = trace(g);
        const out = loops.length ? simplify(loops[0], g.cell, options.maxPoints || MAX_POINTS) : null;
        if (!out) {
            return null;
        }

        const same = (geometry.signedArea(out) > 0) === (geometry.signedArea(polygon) > 0);
        return same ? out : out.reverse();
    }

    return { apply: apply, trace: trace, simplify: simplify, MAX_POINTS: MAX_POINTS };
});
