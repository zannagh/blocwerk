/*
 * Wall view mode (Natural / Ortho / Cylindric), persisted per device, plus the cylinder image
 * warp. Mirrors wall-dim.js: two localStorage keys, read on demand by the host component so the
 * segmented control hydrates to the stored choice after a Blazor render.
 *
 *   blocwerk-wall-view  : "0" | "1" | "2"  (WallViewMode: Natural=0, Ortho=1, Cylindric=2)
 *   blocwerk-wall-curve : "0".."100"       (curve strength -> beta 0..1)
 *
 * bwCylindric.render() draws the ortho <img> onto a <canvas> as ~200 vertical strips. The C# twin
 * CylindricMap.cs warps the SVG hold overlay through the SAME map, which is what keeps holds glued
 * to the photo. Keep the two numerically identical.
 */
(function () {
    const viewKey = 'blocwerk-wall-view';
    const curveKey = 'blocwerk-wall-curve';

    const THETA_MAX = 0.8; // ~46deg; not stored on the wall yet, matches CylindricMap defaults
    const K = 0.45;        // must stay < cos(THETA_MAX) so the warp never folds
    const STRIPS = 200;
    const MAX_CANVAS_W = 2400; // cap the texture so a huge master doesn't blow up memory

    function clampInt(raw, lo, hi, fallback) {
        const v = parseInt(raw, 10);
        return isNaN(v) ? fallback : Math.max(lo, Math.min(hi, v));
    }

    function xs(t) {
        return Math.sin(t) / (1 - K * Math.cos(t));
    }

    const xsMax = xs(THETA_MAX);

    // The shared map: normalized (u,v) -> (sx,sy). Identical to CylindricMap.MapPoint.
    function makeMap(beta) {
        const b = Math.max(0, Math.min(1, beta));
        return function (u, v) {
            const theta = (u - 0.5) * 2 * THETA_MAX;
            const xn = xs(theta) / xsMax;
            const sxFull = 0.5 + 0.5 * xn;
            const sc = (1 - K) / (1 - K * Math.cos(theta));
            const syFull = 0.5 + (v - 0.5) * sc;
            return [(1 - b) * u + b * sxFull, (1 - b) * v + b * syFull];
        };
    }

    function warp(canvas, img, beta) {
        if (!canvas || !img || !img.naturalWidth) {
            return;
        }
        let w = img.naturalWidth;
        let h = img.naturalHeight;
        if (w > MAX_CANVAS_W) {
            h = Math.round(h * (MAX_CANVAS_W / w));
            w = MAX_CANVAS_W;
        }
        if (canvas.width !== w || canvas.height !== h) {
            canvas.width = w;
            canvas.height = h;
        }
        const ctx = canvas.getContext('2d');
        ctx.clearRect(0, 0, w, h);
        const map = makeMap(beta);
        const srcColW = img.naturalWidth / STRIPS;
        for (let i = 0; i < STRIPS; i++) {
            const u0 = i / STRIPS;
            const u1 = (i + 1) / STRIPS;
            const umid = (u0 + u1) / 2;
            const sx0 = map(u0, 0.5)[0];
            const sx1 = map(u1, 0.5)[0];
            const syTop = map(umid, 0)[1];
            const syBot = map(umid, 1)[1];
            const dX = sx0 * w;
            const dW = Math.max(0.5, (sx1 - sx0) * w);
            const dY = syTop * h;
            const dH = (syBot - syTop) * h;
            ctx.drawImage(img, i * srcColW, 0, srcColW, img.naturalHeight, dX, dY, dW, dH);
        }
    }

    window.bwCylindric = {
        /**
         * Warps `img` onto `canvas`. If the image has not loaded yet, defers to its load event so
         * the caller can fire this straight after a render without racing the <img>.
         */
        render: function (canvas, img, opts) {
            const beta = opts && typeof opts.beta === 'number' ? opts.beta : 0;
            if (!canvas || !img) {
                return;
            }
            if (img.complete && img.naturalWidth) {
                warp(canvas, img, beta);
            } else {
                img.addEventListener('load', function once() {
                    img.removeEventListener('load', once);
                    warp(canvas, img, beta);
                });
            }
        },
        // Exposed so callers (and tests) can share the exact map the strips use.
        map: makeMap,
    };

    window.bwWallView = {
        getMode: function () {
            return clampInt(localStorage.getItem(viewKey), 0, 2, 0);
        },
        setMode: function (value) {
            const v = clampInt(value, 0, 2, 0);
            localStorage.setItem(viewKey, String(v));
            return v;
        },
        getCurve: function () {
            return clampInt(localStorage.getItem(curveKey), 0, 100, 0);
        },
        setCurve: function (value) {
            const v = clampInt(value, 0, 100, 0);
            localStorage.setItem(curveKey, String(v));
            return v;
        },
    };
})();
