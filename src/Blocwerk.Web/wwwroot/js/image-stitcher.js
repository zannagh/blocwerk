window.imageStitcher = {
    /** Given a data URL, resolve to its natural dimensions. */
    probeDimensions(dataUrl) {
        return new Promise((resolve, reject) => {
            const img = new Image();
            img.onload = () => resolve({ width: img.naturalWidth, height: img.naturalHeight });
            img.onerror = () => reject(new Error('probe failed'));
            img.src = dataUrl;
        });
    },

    /** Size and viewport-relative origin of the static viewport element. */
    getViewportSize(el) {
        if (!el) return { width: 0, height: 0, left: 0, top: 0 };
        const r = el.getBoundingClientRect();
        return { width: r.width, height: r.height, left: r.left, top: r.top };
    },

    /**
     * Convert a clientX/clientY into world coordinates given the current
     * pan (screen px) and zoom of the world container inside the viewport.
     */
    clientToWorld(viewportEl, clientX, clientY, panX, panY, zoom) {
        if (!viewportEl || zoom === 0) return { x: 0, y: 0 };
        const r = viewportEl.getBoundingClientRect();
        return {
            x: (clientX - r.left - panX) / zoom,
            y: (clientY - r.top - panY) / zoom,
        };
    },

    // ---- projective helpers (source rect -> world corners) -----------------
    _adj(m) {
        return [
            m[4] * m[8] - m[5] * m[7], m[2] * m[7] - m[1] * m[8], m[1] * m[5] - m[2] * m[4],
            m[5] * m[6] - m[3] * m[8], m[0] * m[8] - m[2] * m[6], m[2] * m[3] - m[0] * m[5],
            m[3] * m[7] - m[4] * m[6], m[1] * m[6] - m[0] * m[7], m[0] * m[4] - m[1] * m[3],
        ];
    },
    _mm(a, b) {
        const r = new Array(9).fill(0);
        for (let i = 0; i < 3; i++)
            for (let j = 0; j < 3; j++)
                r[i * 3 + j] = a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j];
        return r;
    },
    _mv(m, v) {
        return [
            m[0] * v[0] + m[1] * v[1] + m[2] * v[2],
            m[3] * v[0] + m[4] * v[1] + m[5] * v[2],
            m[6] * v[0] + m[7] * v[1] + m[8] * v[2],
        ];
    },
    _basis(p) {
        const m = [p[0][0], p[1][0], p[2][0], p[0][1], p[1][1], p[2][1], 1, 1, 1];
        const v = this._mv(this._adj(m), [p[3][0], p[3][1], 1]);
        return this._mm(m, [v[0], 0, 0, 0, v[1], 0, 0, 0, v[2]]);
    },
    _homography(w, h, corners) {
        const s = this._basis([[0, 0], [w, 0], [w, h], [0, h]]);
        const d = this._basis(corners);
        return this._mm(d, this._adj(s));
    },
    _apply(m, x, y) {
        const w = m[6] * x + m[7] * y + m[8] || 1e-12;
        return [(m[0] * x + m[1] * y + m[2]) / w, (m[3] * x + m[4] * y + m[5]) / w];
    },

    /**
     * layers: [{ dataUrl, width, height, corners: [[x,y] x4 world coords] }]
     * Rasterizes each layer with a true perspective warp (homography) by
     * subdividing the source into a grid and drawing each cell's two triangles
     * through their local affine. Returns a PNG Blob (streamed to C#).
     */
    async exportPngBlob(layers) {
        if (!layers || layers.length === 0) return null;

        const loaded = await Promise.all(layers.map(l => new Promise((res, rej) => {
            const img = new Image();
            img.onload = () => res({ img, layer: l });
            img.onerror = rej;
            img.src = l.dataUrl;
        })));

        let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
        for (const { layer } of loaded) {
            for (const [x, y] of layer.corners) {
                minX = Math.min(minX, x); minY = Math.min(minY, y);
                maxX = Math.max(maxX, x); maxY = Math.max(maxY, y);
            }
        }

        const outW = Math.ceil(maxX - minX);
        const outH = Math.ceil(maxY - minY);
        if (outW <= 0 || outH <= 0) return null;

        const canvas = document.createElement('canvas');
        canvas.width = outW;
        canvas.height = outH;
        const ctx = canvas.getContext('2d');

        for (const { img, layer } of loaded) {
            this._drawWarped(ctx, img, layer, -minX, -minY);
        }

        return await new Promise((res, rej) =>
            canvas.toBlob(b => b ? res(b) : rej(new Error('toBlob failed')), 'image/png')
        );
    },

    _drawWarped(ctx, img, layer, offX, offY) {
        const W = layer.width, H = layer.height;
        const H3 = this._homography(W, H, layer.corners);
        const N = 12;
        // Precompute grid of destination (world) points.
        const grid = [];
        for (let iy = 0; iy <= N; iy++) {
            const row = [];
            for (let ix = 0; ix <= N; ix++) {
                const sx = (W * ix) / N, sy = (H * iy) / N;
                const [wx, wy] = this._apply(H3, sx, sy);
                row.push([wx + offX, wy + offY]);
            }
            grid.push(row);
        }

        for (let iy = 0; iy < N; iy++) {
            for (let ix = 0; ix < N; ix++) {
                const sx0 = (W * ix) / N, sy0 = (H * iy) / N;
                const sx1 = (W * (ix + 1)) / N, sy1 = (H * (iy + 1)) / N;
                const d00 = grid[iy][ix], d10 = grid[iy][ix + 1];
                const d11 = grid[iy + 1][ix + 1], d01 = grid[iy + 1][ix];
                this._drawTri(ctx, img, [sx0, sy0], [sx1, sy0], [sx1, sy1], d00, d10, d11);
                this._drawTri(ctx, img, [sx0, sy0], [sx1, sy1], [sx0, sy1], d00, d11, d01);
            }
        }
    },

    _drawTri(ctx, img, s0, s1, s2, d0, d1, d2) {
        // Solve the affine that maps source triangle -> dest triangle.
        const x1 = s1[0] - s0[0], y1 = s1[1] - s0[1];
        const x2 = s2[0] - s0[0], y2 = s2[1] - s0[1];
        const det = x1 * y2 - x2 * y1;
        if (Math.abs(det) < 1e-9) return;

        const ux1 = d1[0] - d0[0], ux2 = d2[0] - d0[0];
        const uy1 = d1[1] - d0[1], uy2 = d2[1] - d0[1];
        const aX = (ux1 * y2 - ux2 * y1) / det;
        const bX = (x1 * ux2 - x2 * ux1) / det;
        const cX = d0[0] - aX * s0[0] - bX * s0[1];
        const aY = (uy1 * y2 - uy2 * y1) / det;
        const bY = (x1 * uy2 - x2 * uy1) / det;
        const cY = d0[1] - aY * s0[0] - bY * s0[1];

        // Expand the clip triangle ~0.6px outward from its centroid to hide seams.
        const gx = (d0[0] + d1[0] + d2[0]) / 3, gy = (d0[1] + d1[1] + d2[1]) / 3;
        const e = (p) => {
            const dx = p[0] - gx, dy = p[1] - gy, len = Math.hypot(dx, dy) || 1;
            return [p[0] + (dx / len) * 0.6, p[1] + (dy / len) * 0.6];
        };
        const e0 = e(d0), e1 = e(d1), e2 = e(d2);

        ctx.save();
        ctx.beginPath();
        ctx.moveTo(e0[0], e0[1]);
        ctx.lineTo(e1[0], e1[1]);
        ctx.lineTo(e2[0], e2[1]);
        ctx.closePath();
        ctx.clip();
        ctx.setTransform(aX, aY, bX, bY, cX, cY);
        ctx.drawImage(img, 0, 0);
        ctx.restore();
    },

    async downloadPng(layers, filename) {
        const blob = await this.exportPngBlob(layers);
        if (!blob) return false;
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        setTimeout(() => URL.revokeObjectURL(url), 1000);
        return true;
    }
};
