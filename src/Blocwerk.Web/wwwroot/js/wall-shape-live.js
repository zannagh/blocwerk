/*
 * The live preview of a Shape-tool edit (wall-shape-drag.js) in the wall editor's overlay.
 *
 * While a handle drag, an eraser stroke or a brush stroke runs, the hold's own SVG elements and its handles
 * are hidden (a `visibility` attribute Blazor never renders, so a re-render cannot fight it) and the edited
 * outline is drawn instead into `g.shape-live`, a group Blazor renders WITHOUT children: nodes added to it are
 * invisible to Blazor's diff and survive every re-render. When the server has written the edit and rendered
 * it, end() takes the preview away and the hold's real elements show again, now with the new outline.
 *
 * Coordinates are the overlay's viewBox (0..100 on both axes, preserveAspectRatio none), so a circle that is
 * round on screen is an ellipse here; the cursor ring is drawn as one.
 */
window.bwShapeLive = (function () {
    'use strict';

    const NS = 'http://www.w3.org/2000/svg';
    const COPIED = ['fill', 'stroke', 'stroke-width', 'stroke-dasharray'];

    function make(name, attrs) {
        const el = document.createElementNS(NS, name);
        for (const key of Object.keys(attrs)) {
            el.setAttribute(key, attrs[key]);
        }

        el.setAttribute('pointer-events', 'none');
        return el;
    }

    function originals(ctx) {
        const shapes = ctx.svg.querySelectorAll('[data-hold="' + ctx.holdId + '"]:not(g)');
        return Array.prototype.slice.call(shapes).concat([ctx.handles]);
    }

    /** Hides the hold and its handles and puts a preview of the same paint in their place. */
    function begin(ctx) {
        const shape = ctx.svg.querySelector('polygon[data-hold="' + ctx.holdId + '"]:not(.hold-hit), path[data-hold="' + ctx.holdId + '"]');
        const attrs = { class: 'shape-live-outline' };
        for (const key of COPIED) {
            const value = shape && shape.getAttribute(key);
            if (value) {
                attrs[key] = value;
            }
        }

        const handle = ctx.handles.querySelector('[data-shape-handle]');
        ctx.handleLook = {};
        for (const key of ['r', 'fill', 'stroke', 'stroke-width']) {
            ctx.handleLook[key] = handle ? handle.getAttribute(key) : null;
        }

        ctx.outline = make('polygon', attrs);
        ctx.dots = [];
        ctx.layer.appendChild(ctx.outline);
        for (const el of originals(ctx)) {
            el.setAttribute('visibility', 'hidden');
        }
    }

    function f(v) {
        return (v * 100).toFixed(4);
    }

    /**
     * Draws the outline (absolute 0..1 points [{x, y}]) and, when given, the handles; `blocked` handle
     * indices (an eraser refused them) are drawn red.
     */
    function draw(ctx, outline, handles, blocked) {
        ctx.outline.setAttribute('points', outline.map(function (p) { return f(p.x) + ',' + f(p.y); }).join(' '));
        const list = handles || [];
        while (ctx.dots.length < list.length) {
            const dot = make('circle', {});
            for (const key of Object.keys(ctx.handleLook)) {
                if (ctx.handleLook[key]) {
                    dot.setAttribute(key, ctx.handleLook[key]);
                }
            }

            ctx.layer.appendChild(dot);
            ctx.dots.push(dot);
        }

        ctx.dots.forEach(function (dot, i) {
            if (i >= list.length) {
                dot.setAttribute('visibility', 'hidden');
                return;
            }

            dot.removeAttribute('visibility');
            dot.setAttribute('cx', f(list[i].x));
            dot.setAttribute('cy', f(list[i].y));
            dot.setAttribute('fill', blocked && blocked.has(list[i].index) ? '#ff5566' : (ctx.handleLook.fill || 'white'));
        });
    }

    /** The cursor ring of the eraser or brush at (x, y) (0..1), `rx`/`ry` its radius as fractions of the photo. */
    function ring(layer, x, y, rx, ry, kind) {
        let el = layer.querySelector('.shape-live-ring');
        if (!el) {
            // A dark halo under a bright ring, both a fixed screen width, so it reads on any wall colour.
            el = make('g', { class: 'shape-live-ring' });
            el.appendChild(make('ellipse', { fill: 'none', stroke: 'rgba(0,0,0,0.65)', 'stroke-width': '3.5', 'vector-effect': 'non-scaling-stroke' }));
            el.appendChild(make('ellipse', { fill: 'none', 'stroke-width': '1.5', 'vector-effect': 'non-scaling-stroke' }));
            layer.appendChild(el);
        }

        el.lastChild.setAttribute('stroke', kind === 'subtract' ? '#ff8a3d' : kind === 'eraser' ? '#ffffff' : '#3ddc84');
        for (const e of el.children) {
            e.setAttribute('cx', f(x));
            e.setAttribute('cy', f(y));
            e.setAttribute('rx', f(rx));
            e.setAttribute('ry', f(ry));
        }

        el.removeAttribute('visibility');
    }

    function hideRing(layer) {
        const el = layer && layer.querySelector('.shape-live-ring');
        if (el) {
            el.setAttribute('visibility', 'hidden');
        }
    }

    /** Takes the preview away and shows the hold's own elements again. */
    function end(ctx) {
        if (ctx.outline) {
            ctx.outline.remove();
        }

        (ctx.dots || []).forEach(function (dot) { dot.remove(); });
        ctx.outline = null;
        ctx.dots = [];
        for (const el of originals(ctx)) {
            el.removeAttribute('visibility');
        }
    }

    return { begin: begin, draw: draw, ring: ring, hideRing: hideRing, end: end };
})();
