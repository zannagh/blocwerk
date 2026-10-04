/*
 * Usage heat map: paints a smooth density of how often each hold is used onto a canvas that sits
 * inside the panel's zoomable layer (.photo-editor), so it follows the zoom/pan exactly like the
 * photo and the hold shapes. Unused areas are dark blue; heavily used ones run through cyan, green
 * and yellow to red. Every hold adds a Gaussian splat whose height is its boulder count (relative
 * to the busiest hold on the wall) and whose size follows the hold's own radius, drawn in the same
 * normalized space as the hold overlay so it lines up with the shapes.
 */
(function () {
    'use strict';

    const GRID_W = 480;
    const SPLAT_K = 2.2;      // splat sigma = hold radius * K
    const GAMMA = 0.7;        // lifts rarely used holds so a count of 1 is still visible
    const ALPHA_MIN = 0.42;   // opacity over unused areas
    const ALPHA_MAX = 0.62;   // opacity over the hottest areas

    // Colour stops, shared with the legend gradient in usage-heat.css.
    const STOPS = [
        [0.00, 38, 52, 160],
        [0.25, 0, 170, 230],
        [0.50, 60, 200, 90],
        [0.75, 250, 220, 40],
        [1.00, 215, 40, 35],
    ];

    const lut = buildLut();

    function buildLut() {
        const out = new Uint8ClampedArray(256 * 4);
        for (let i = 0; i < 256; i++) {
            const t = i / 255;
            let s = 1;
            while (s < STOPS.length - 1 && STOPS[s][0] < t) {
                s++;
            }

            const a = STOPS[s - 1];
            const b = STOPS[s];
            const f = (t - a[0]) / (b[0] - a[0]);
            out[i * 4] = a[1] + (b[1] - a[1]) * f;
            out[i * 4 + 1] = a[2] + (b[2] - a[2]) * f;
            out[i * 4 + 2] = a[3] + (b[3] - a[3]) * f;
            out[i * 4 + 3] = 255 * (ALPHA_MIN + (ALPHA_MAX - ALPHA_MIN) * t);
        }

        return out;
    }

    function paint(canvas, aspect, holds, max) {
        const w = GRID_W;
        const h = Math.max(1, Math.round(w / aspect));
        canvas.width = w;
        canvas.height = h;
        const field = new Float32Array(w * h);

        holds.forEach(function (p) {
            // Hold radius is a fraction of each axis (the overlay stretches to the image), so the
            // splat is elliptical in grid pixels exactly like the drawn hold.
            const sx = Math.max(1.5, p.r * w * SPLAT_K);
            const sy = Math.max(1.5, p.r * h * SPLAT_K);
            const amp = Math.pow(p.c / max, GAMMA);
            const cx = p.x * w;
            const cy = p.y * h;
            const x0 = Math.max(0, Math.floor(cx - 3 * sx));
            const x1 = Math.min(w - 1, Math.ceil(cx + 3 * sx));
            const y0 = Math.max(0, Math.floor(cy - 3 * sy));
            const y1 = Math.min(h - 1, Math.ceil(cy + 3 * sy));
            for (let y = y0; y <= y1; y++) {
                const dy = (y - cy) / sy;
                for (let x = x0; x <= x1; x++) {
                    const dx = (x - cx) / sx;
                    field[y * w + x] += amp * Math.exp(-0.5 * (dx * dx + dy * dy));
                }
            }
        });

        const ctx = canvas.getContext('2d');
        const img = ctx.createImageData(w, h);
        for (let i = 0; i < field.length; i++) {
            const idx = Math.min(255, Math.round(Math.min(1, field[i]) * 255)) * 4;
            img.data[i * 4] = lut[idx];
            img.data[i * 4 + 1] = lut[idx + 1];
            img.data[i * 4 + 2] = lut[idx + 2];
            img.data[i * 4 + 3] = lut[idx + 3];
        }

        ctx.putImageData(img, 0, 0);
    }

    window.bwHeat = {
        /** Draws the heat map for `holds` ([{x, y, r, c}], normalized) into the viewport's canvas. */
        render: function (viewport, holds, max) {
            const canvas = viewport && viewport.querySelector('canvas.usage-heat');
            if (!canvas) {
                return;
            }

            const img = viewport.querySelector('img.wall-photo');
            const draw = function () {
                const aspect = img && img.naturalWidth ? img.naturalWidth / img.naturalHeight : 4 / 3;
                paint(canvas, aspect, holds, Math.max(1, max));
            };

            if (img && !img.complete) {
                img.addEventListener('load', draw, { once: true });
            }

            draw();
        },
    };
})();
