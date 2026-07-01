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
     * layers: [{ dataUrl, width, height, x, y, scale, rotation }]
     *   (x, y) is the CENTER of the image in workspace coordinates.
     *   rotation is degrees, scale is a multiplier on the natural size.
     * Auto-fits the output canvas to the transformed bounding box of all layers.
     * Returns a PNG data URL.
     */
    async exportPng(layers) {
        if (!layers || layers.length === 0) return null;

        const loaded = await Promise.all(layers.map(l => new Promise((res, rej) => {
            const img = new Image();
            img.onload = () => res({ img, layer: l });
            img.onerror = rej;
            img.src = l.dataUrl;
        })));

        let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
        for (const { layer } of loaded) {
            const halfW = (layer.width * layer.scale) / 2;
            const halfH = (layer.height * layer.scale) / 2;
            const rad = layer.rotation * Math.PI / 180;
            const cos = Math.abs(Math.cos(rad));
            const sin = Math.abs(Math.sin(rad));
            const rotHalfW = halfW * cos + halfH * sin;
            const rotHalfH = halfW * sin + halfH * cos;
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
            ctx.scale(layer.scale, layer.scale);
            ctx.drawImage(img, -img.naturalWidth / 2, -img.naturalHeight / 2);
            ctx.restore();
        }

        return canvas.toDataURL('image/png');
    },

    downloadDataUrl(dataUrl, filename) {
        const a = document.createElement('a');
        a.href = dataUrl;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
    },

    /** Convert a data URL to a base64 string (strip the "data:...;base64," prefix). */
    dataUrlToBase64(dataUrl) {
        const comma = dataUrl.indexOf(',');
        return comma >= 0 ? dataUrl.substring(comma + 1) : dataUrl;
    }
};
