window.imageStitcher = {
    /**
     * Given a data URL, resolve to its natural dimensions.
     */
    probeDimensions(dataUrl) {
        return new Promise((resolve, reject) => {
            const img = new Image();
            img.onload = () => resolve({ width: img.naturalWidth, height: img.naturalHeight });
            img.onerror = () => reject(new Error('probe failed'));
            img.src = dataUrl;
        });
    },

    /**
     * Convert a clientX/clientY pixel coordinate into the SVG's viewBox
     * user-space coords. This is the correct way to hit-test SVG content —
     * a plain bounding-rect division ignores preserveAspectRatio and any
     * CSS scaling on the SVG.
     */
    clientToSvgCoords(svg, clientX, clientY) {
        if (!svg) return { x: 0, y: 0 };
        const pt = svg.createSVGPoint();
        pt.x = clientX;
        pt.y = clientY;
        const ctm = svg.getScreenCTM();
        if (!ctm) return { x: 0, y: 0 };
        const p = pt.matrixTransform(ctm.inverse());
        return { x: p.x, y: p.y };
    },

    /**
     * layers: [{ dataUrl, width, height, x, y, scale, rotation, skewX, skewY, opacity? }]
     *   (x, y) is the CENTER of the image in workspace coordinates.
     *   rotation, skewX, skewY are in degrees; scale is a multiplier on the natural size.
     *   opacity here is IGNORED — exports are always full-alpha (the on-canvas
     *   opacity is for alignment aid only).
     * Auto-fits the output canvas to the transformed bounding box of all layers
     * and returns a PNG Blob. Blazor treats a returned Blob as an
     * IJSStreamReference so a large export can be streamed back to C# without
     * crashing the SignalR hub message limit.
     */
    async exportPngBlob(layers) {
        if (!layers || layers.length === 0) return null;

        const loaded = await Promise.all(layers.map(l => new Promise((res, rej) => {
            const img = new Image();
            img.onload = () => res({ img, layer: l });
            img.onerror = rej;
            img.src = l.dataUrl;
        })));

        // Compute rotated bounding box for each layer. Skew is ignored in the
        // bbox estimate — a small conservative pad keeps skewed content from
        // being clipped.
        let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
        for (const { layer } of loaded) {
            const halfW = (layer.width * layer.scale) / 2;
            const halfH = (layer.height * layer.scale) / 2;
            const rad = layer.rotation * Math.PI / 180;
            const cos = Math.abs(Math.cos(rad));
            const sin = Math.abs(Math.sin(rad));
            const skewPad = 1 + Math.max(Math.abs(layer.skewX || 0), Math.abs(layer.skewY || 0)) / 45;
            const rotHalfW = (halfW * cos + halfH * sin) * skewPad;
            const rotHalfH = (halfW * sin + halfH * cos) * skewPad;
            minX = Math.min(minX, layer.x - rotHalfW);
            minY = Math.min(minY, layer.y - rotHalfH);
            maxX = Math.max(maxX, layer.x + rotHalfW);
            maxY = Math.max(maxY, layer.y + rotHalfH);
        }

        const w = Math.ceil(maxX - minX);
        const h = Math.ceil(maxY - minY);
        if (w <= 0 || h <= 0) return null;

        const canvas = document.createElement('canvas');
        canvas.width = w;
        canvas.height = h;
        const ctx = canvas.getContext('2d');

        for (const { img, layer } of loaded) {
            ctx.save();
            ctx.translate(layer.x - minX, layer.y - minY);
            ctx.rotate(layer.rotation * Math.PI / 180);
            // Apply skew via affine matrix. Canvas .transform is a *multiply*
            // (not a set), so this composes with the current translate+rotate.
            const kx = Math.tan((layer.skewX || 0) * Math.PI / 180);
            const ky = Math.tan((layer.skewY || 0) * Math.PI / 180);
            ctx.transform(1, ky, kx, 1, 0, 0);
            ctx.scale(layer.scale, layer.scale);
            ctx.drawImage(img, -img.naturalWidth / 2, -img.naturalHeight / 2);
            ctx.restore();
        }

        return await new Promise((res, rej) =>
            canvas.toBlob(b => b ? res(b) : rej(new Error('toBlob failed')), 'image/png')
        );
    },

    /**
     * Rasterize the layers and trigger a browser download. All in JS — no
     * server round-trip, so the payload size doesn't matter.
     */
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
        // Give the browser a tick to start the download before revoking.
        setTimeout(() => URL.revokeObjectURL(url), 1000);
        return true;
    }
};
