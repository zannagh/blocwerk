/*
 * The three Shape-tool edits wall-shape-drag.js runs in the browser, one object per running edit:
 *
 *   handle  drag one corner (or, on a smooth outline, one control point; the curve is redrawn here)
 *   eraser  take away every corner / control point the stroke covers, never leaving a broken outline
 *   brush   paint area onto the outline, or cut it out (edit.subtract)
 *
 * Each has add(event) (a pointer sample), paint() (once per frame, draws the preview through bwShapeLive)
 * and result() (what to send on release: { points: [{dx, dy}] offsets from the hold's centre, control:
 * whether they are a smooth outline's control points }, or null for "nothing to save").
 * Geometry runs in SCREEN px of the photo as drawn, so the eraser and brush radius (CSS px) is round on screen
 * and the same size at every zoom.
 */
window.bwShapeOps = (function () {
    'use strict';

    const G = window.bwShapeGeometry;
    const B = window.bwShapeBrush;
    const PREVIEW_CELLS = 160;
    const FINAL_CELLS = 320;

    function clamp01(v) {
        return Math.max(0, Math.min(1, v));
    }

    /** The photo's drawn size in px along its own axes (a quarter-turned stage swaps them). */
    function sizeOf(env) {
        const rect = env.container.getBoundingClientRect();
        const turned = env.container.closest('[data-bw-rot="90"]');
        return turned ? { w: rect.height, h: rect.width } : { w: rect.width, h: rect.height };
    }

    function rel(env, ev) {
        return window.wallEditor.getRelativePosition(env.container, ev.clientX, ev.clientY);
    }

    /** A pen presses harder for a wider stroke; everything else draws at the set radius. */
    function pressure(ev) {
        return ev.pointerType === 'pen' && ev.pressure > 0 ? 0.35 + (1.3 * ev.pressure) : 1;
    }

    function ctxOf(env, edit) {
        return { svg: env.svg, layer: env.layer, holdId: edit.holdId, handles: env.group };
    }

    function abs(edit, p) {
        return { x: edit.cx + p.dx, y: edit.cy + p.dy };
    }

    /** Draws offsets `pts` as the outline (their curve when smooth) and, unless `noHandles`, as handles. */
    function drawPoints(ctx, edit, pts, indices, blocked, noHandles) {
        const outline = (edit.smooth ? G.densify(pts) : pts).map(function (p) { return abs(edit, p); });
        const handles = noHandles ? null : pts.map(function (p, i) {
            const a = abs(edit, p);
            return { x: a.x, y: a.y, index: indices ? indices[i] : i };
        });
        window.bwShapeLive.draw(ctx, outline, handles, blocked);
    }

    function ring(env, at, radius, kind) {
        if (!at) {
            return;
        }

        const size = sizeOf(env);
        window.bwShapeLive.ring(env.layer, at.x, at.y, radius / size.w, radius / size.h, kind);
    }

    /** The editor a press landed in, when it is editing a hold's outline: its elements and .NET reference. */
    function envFor(target, editors) {
        const container = target && target.closest ? target.closest('.photo-editor[data-shape-tool][data-hold-drag-key]') : null;
        const group = container ? container.querySelector('g.shape-edit') : null;
        const editor = container ? editors.get(container.getAttribute('data-hold-drag-key')) : null;
        if (!editor || !group || !group.ownerSVGElement) {
            return null;
        }

        return {
            container: container, editor: editor, group: group, svg: group.ownerSVGElement,
            layer: container.querySelector('g.shape-live'), tool: container.getAttribute('data-shape-tool'),
        };
    }

    /** The edited hold as the editor rendered it on g.shape-edit (see ShapeEditHandles.razor). */
    function readEdit(env) {
        const g = env.group;
        const points = (g.getAttribute('data-shape-points') || '').split(' ').filter(Boolean).map(function (s) {
            const xy = s.split(',');
            return { dx: Number(xy[0]), dy: Number(xy[1]) };
        });
        return {
            holdId: g.getAttribute('data-shape-hold'),
            cx: Number(g.getAttribute('data-shape-cx')),
            cy: Number(g.getAttribute('data-shape-cy')),
            smooth: g.getAttribute('data-shape-smooth') === 'true',
            radius: Number(g.getAttribute('data-shape-radius')) || 14,
            subtract: g.getAttribute('data-shape-subtract') === 'true',
            points: points,
        };
    }

    function handle(env, edit, index) {
        const pts = edit.points.map(function (p) { return { dx: p.dx, dy: p.dy }; });
        let grab = null;
        return {
            kind: 'drag',
            ctx: ctxOf(env, edit),
            add: function (ev) {
                const r = rel(env, ev);
                // Keep where on the handle it was grabbed, so the point does not jump to the finger.
                grab = grab || { x: edit.cx + pts[index].dx - r.x, y: edit.cy + pts[index].dy - r.y };
                pts[index] = { dx: clamp01(r.x + grab.x) - edit.cx, dy: clamp01(r.y + grab.y) - edit.cy };
            },
            paint: function () {
                drawPoints(this.ctx, edit, pts, null, null, false);
            },
            result: function () {
                return { points: pts, control: edit.smooth };
            },
        };
    }

    function eraser(env, edit) {
        const size = sizeOf(env);
        const toPx = function (p) { return [(edit.cx + p.dx) * size.w, (edit.cy + p.dy) * size.h]; };
        const px = edit.points.map(toPx);
        const outlineOf = function (k) {
            return edit.smooth ? G.densify(k.map(function (i) { return edit.points[i]; })).map(toPx) : k.map(function (i) { return px[i]; });
        };
        const blocked = new Set();
        let keep = edit.points.map(function (_, i) { return i; });
        let last = null;
        return {
            kind: 'erase',
            ctx: ctxOf(env, edit),
            add: function (ev) {
                const r = rel(env, ev);
                const p = [r.x * size.w, r.y * size.h];
                const step = G.erase(px, keep, last || p, p, edit.radius, outlineOf);
                keep = step.keep;
                step.blocked.forEach(function (b) { blocked.add(b); });
                last = p;
                this.at = r;
            },
            paint: function () {
                drawPoints(this.ctx, edit, keep.map(function (i) { return edit.points[i]; }), keep, blocked, false);
                ring(env, this.at, edit.radius, 'eraser');
            },
            result: function () {
                return keep.length < edit.points.length
                    ? { points: keep.map(function (i) { return edit.points[i]; }), control: edit.smooth }
                    : null;
            },
        };
    }

    function brush(env, edit) {
        const size = sizeOf(env);
        const outline = edit.smooth ? G.densify(edit.points) : edit.points;
        const base = outline.map(function (p) { return [(edit.cx + p.dx) * size.w, (edit.cy + p.dy) * size.h]; });
        const samples = [];
        let preview = null;
        let dirty = false;
        const toAbs = function (p) { return { x: p[0] / size.w, y: p[1] / size.h }; };
        return {
            kind: 'brush',
            ctx: ctxOf(env, edit),
            add: function (ev) {
                const r = rel(env, ev);
                const s = { x: r.x * size.w, y: r.y * size.h, r: edit.radius * pressure(ev) };
                const prev = samples[samples.length - 1];
                if (!prev || Math.hypot(s.x - prev.x, s.y - prev.y) >= Math.min(s.r, prev.r) * 0.3) {
                    samples.push(s);
                    dirty = true;
                }

                this.at = r;
            },
            paint: function () {
                if (dirty) {
                    preview = B.apply(base, samples, edit.subtract, { maxCells: PREVIEW_CELLS });
                    dirty = false;
                }

                window.bwShapeLive.draw(this.ctx, (preview || base).map(toAbs), null, null);
                ring(env, this.at, edit.radius, edit.subtract ? 'subtract' : 'brush');
            },
            result: function () {
                const out = B.apply(base, samples, edit.subtract, { maxCells: FINAL_CELLS });
                return out ? {
                    points: out.map(function (p) { return { dx: clamp01(p[0] / size.w) - edit.cx, dy: clamp01(p[1] / size.h) - edit.cy }; }),
                    control: false,
                } : null;
            },
        };
    }

    return { envFor: envFor, readEdit: readEdit, handle: handle, eraser: eraser, brush: brush, sizeOf: sizeOf, rel: rel, ring: ring };
})();
