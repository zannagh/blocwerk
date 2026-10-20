/*
 * Pure outline geometry for the wall editor's Shape tool (wall-shape-drag.js), with no DOM.
 *
 *  - densify: the browser's copy of HoldOutlineSpline.Densify (Blocwerk.Core). A smooth outline's handle
 *    drag redraws the curve here on every frame instead of asking the server. It MUST give the server's
 *    numbers exactly (same operations in the same order, same banker's rounding to 5 decimals), or the
 *    outline would jump on release; test/js/shape-tools.test.mjs checks it against the fixture the C# test
 *    pins (test/Blocwerk.Core.Tests/Fixtures/spline-densify.json).
 *  - isSimple / segmentsIntersect: ShapeGeometry's, for the eraser's "never leave a crossing outline".
 *  - erase: which points an eraser stroke takes away, refusing any removal that would break the outline.
 *
 * Loaded as a plain script (window.bwShapeGeometry) and by node (module.exports) for the tests.
 */
(function (root, factory) {
    'use strict';
    const api = factory();
    if (typeof module === 'object' && module.exports) {
        module.exports = api;
    } else {
        root.bwShapeGeometry = api;
    }
})(typeof self !== 'undefined' ? self : this, function () {
    'use strict';

    const MAX_OUTLINE_POINTS = 96;
    const MIN_SAMPLES = 3;
    const MAX_SAMPLES = 12;
    const MIN_SPACING = 1e-9;

    function distance(a, b) {
        return Math.sqrt(((a.x - b.x) * (a.x - b.x)) + ((a.y - b.y) * (a.y - b.y)));
    }

    /** .NET's Math.Round(v, 5): scale, round half to even, scale back. */
    function round5(v) {
        const scaled = v * 1e5;
        let r = Math.round(scaled);
        if (Math.abs(scaled % 1) === 0.5 && r % 2 !== 0) {
            r -= 1;
        }

        return r / 1e5;
    }

    function distinct(points) {
        const out = [];
        for (const point of points) {
            const p = { x: point.dx, y: point.dy };
            if (out.length === 0 || distance(out[out.length - 1], p) > MIN_SPACING) {
                out.push(p);
            }
        }

        if (out.length > 1 && distance(out[0], out[out.length - 1]) <= MIN_SPACING) {
            out.pop();
        }

        return out;
    }

    function blend(a, b, ta, tb, t) {
        const w = (t - ta) / (tb - ta);
        return { x: a.x + ((b.x - a.x) * w), y: a.y + ((b.y - a.y) * w) };
    }

    /** The point at u (0..1) between p1 and p2: Barry-Goldman pyramid, centripetal knots. */
    function segment(p0, p1, p2, p3, u) {
        const t1 = Math.sqrt(distance(p0, p1));
        const t2 = t1 + Math.sqrt(distance(p1, p2));
        const t3 = t2 + Math.sqrt(distance(p2, p3));
        const t = t1 + ((t2 - t1) * u);
        const a1 = blend(p0, p1, 0, t1, t);
        const a2 = blend(p1, p2, t1, t2, t);
        const a3 = blend(p2, p3, t2, t3, t);
        const b1 = blend(a1, a2, 0, t2, t);
        const b2 = blend(a2, a3, t1, t3, t);
        return blend(b1, b2, t1, t2, t);
    }

    /** The smooth closed outline through control points [{dx, dy}], as [{dx, dy}]. */
    function densify(control) {
        const points = distinct(control);
        const n = points.length;
        if (n < 3) {
            return control.map(function (p) { return { dx: p.dx, dy: p.dy }; });
        }

        const samples = Math.min(MAX_SAMPLES, Math.max(MIN_SAMPLES, Math.trunc(MAX_OUTLINE_POINTS / n)));
        const outline = [];
        for (let i = 0; i < n; i++) {
            const p0 = points[(i + n - 1) % n];
            const p1 = points[i];
            const p2 = points[(i + 1) % n];
            const p3 = points[(i + 2) % n];
            for (let s = 0; s < samples; s++) {
                const q = s === 0 ? p1 : segment(p0, p1, p2, p3, s / samples);
                outline.push({ dx: round5(q.x), dy: round5(q.y) });
            }
        }

        return outline;
    }

    function cross(ax, ay, bx, by) {
        return (ax * by) - (ay * bx);
    }

    function onSegment(a, b, p) {
        return Math.min(a[0], b[0]) <= p[0] && p[0] <= Math.max(a[0], b[0])
            && Math.min(a[1], b[1]) <= p[1] && p[1] <= Math.max(a[1], b[1]);
    }

    /** True when the closed segments ab and cd share a point (points are [x, y]). */
    function segmentsIntersect(a, b, c, d) {
        const d1 = cross(b[0] - a[0], b[1] - a[1], c[0] - a[0], c[1] - a[1]);
        const d2 = cross(b[0] - a[0], b[1] - a[1], d[0] - a[0], d[1] - a[1]);
        const d3 = cross(d[0] - c[0], d[1] - c[1], a[0] - c[0], a[1] - c[1]);
        const d4 = cross(d[0] - c[0], d[1] - c[1], b[0] - c[0], b[1] - c[1]);
        return (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
            || (d1 === 0 && onSegment(a, b, c))
            || (d2 === 0 && onSegment(a, b, d))
            || (d3 === 0 && onSegment(c, d, a))
            || (d4 === 0 && onSegment(c, d, b));
    }

    /** True when no two non-adjacent edges of the closed polygon cross. */
    function isSimple(poly) {
        const n = poly.length;
        for (let i = 0; i < n; i++) {
            for (let j = i + 2; j < n; j++) {
                if (i === 0 && j === n - 1) {
                    continue;
                }

                if (segmentsIntersect(poly[i], poly[(i + 1) % n], poly[j], poly[(j + 1) % n])) {
                    return false;
                }
            }
        }

        return true;
    }

    function signedArea(poly) {
        let sum = 0;
        for (let i = 0, j = poly.length - 1; i < poly.length; j = i++) {
            sum += cross(poly[j][0], poly[j][1], poly[i][0], poly[i][1]);
        }

        return sum / 2;
    }

    /** Distance from p to the segment ab (all [x, y]). */
    function distanceToSegment(p, a, b) {
        const vx = b[0] - a[0];
        const vy = b[1] - a[1];
        const len2 = (vx * vx) + (vy * vy);
        const t = len2 > 0 ? Math.max(0, Math.min(1, (((p[0] - a[0]) * vx) + ((p[1] - a[1]) * vy)) / len2)) : 0;
        return Math.hypot(p[0] - (a[0] + (t * vx)), p[1] - (a[1] + (t * vy)));
    }

    /**
     * One eraser step. `points` are the editable points in SCREEN px ([x, y], so the radius is round on
     * screen whatever the photo's aspect); every point within `radius` of the stroke segment a-b is taken
     * away, one at a time, and a removal that would leave fewer than three points or a crossing outline is
     * refused (that point stays and is reported as blocked). `outlineOf(keptIndices)` gives the outline the
     * remaining points would draw, in the same px space: the points themselves for a polygon, their curve
     * for a smooth outline.
     * Returns { keep: indices still there, blocked: indices that could not go }.
     */
    function erase(points, keep, a, b, radius, outlineOf) {
        let kept = keep.slice();
        const blocked = [];
        for (const index of keep) {
            if (distanceToSegment(points[index], a, b) > radius) {
                continue;
            }

            const without = kept.filter(function (k) { return k !== index; });
            const outline = without.length >= 3 ? outlineOf(without) : null;
            if (!outline || outline.length < 3 || !isSimple(outline) || Math.abs(signedArea(outline)) < 1e-6) {
                blocked.push(index);
                continue;
            }

            kept = without;
        }

        return { keep: kept, blocked: blocked };
    }

    return {
        densify: densify,
        round5: round5,
        segmentsIntersect: segmentsIntersect,
        isSimple: isSimple,
        signedArea: signedArea,
        distanceToSegment: distanceToSegment,
        erase: erase,
    };
});
