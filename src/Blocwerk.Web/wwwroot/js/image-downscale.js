// Client-side image downscale for the big-wall panel uploader. A modern phone photo can be
// 15-20 MP / well over the server upload cap, and the old flow silently dropped anything past the
// cap. Here we read the chosen file straight from its <input>, draw it onto a canvas capped to a
// long edge, and hand the re-encoded JPEG back to .NET as a byte stream (IJSStreamReference) so a
// large photo just works while keeping enough resolution for hold detection.
//
// Loaded as a module (import "/js/image-downscale.js") rather than a collocated .razor.js, which
// breaks under this app's nested routes.

/**
 * Downscale the file currently selected in the given <input type="file"> to a JPEG whose long edge
 * is at most `maxEdge`, at the given quality. Returns a Uint8Array (marshalled to .NET as an
 * IJSStreamReference) on success, or null when there is no file or the browser cannot decode it —
 * the caller then falls back / flags the slot so an update can never start on a missing panel.
 */
export async function downscaleFromInput(inputId, maxEdge, quality) {
    const input = document.getElementById(inputId);
    if (!input || !input.files || input.files.length === 0) {
        return null;
    }

    const decoded = await decode(input.files[0]);
    if (!decoded) {
        return null;
    }

    try {
        const target = fit(decoded.width, decoded.height, maxEdge);
        const canvas = document.createElement('canvas');
        canvas.width = target.width;
        canvas.height = target.height;

        const ctx = canvas.getContext('2d');
        if (!ctx) {
            return null;
        }

        ctx.drawImage(decoded.source, 0, 0, target.width, target.height);

        const blob = await new Promise((resolve) => canvas.toBlob(resolve, 'image/jpeg', quality));
        if (!blob) {
            return null;
        }

        const buffer = await blob.arrayBuffer();
        return new Uint8Array(buffer);
    } catch {
        return null;
    } finally {
        decoded.cleanup();
    }
}

// Longest-edge fit: only ever shrinks, never enlarges a small image.
function fit(width, height, maxEdge) {
    const longest = Math.max(width, height);
    if (longest <= maxEdge) {
        return { width, height };
    }

    const scale = maxEdge / longest;
    return { width: Math.round(width * scale), height: Math.round(height * scale) };
}

// Decode a File into a canvas-drawable source with intrinsic dimensions, honouring EXIF
// orientation so a portrait phone photo is not staged sideways. Prefers createImageBitmap; falls
// back to an <img> + object URL where it is unavailable or throws.
async function decode(file) {
    if (window.createImageBitmap) {
        try {
            const bitmap = await createImageBitmap(file, { imageOrientation: 'from-image' });
            return {
                source: bitmap,
                width: bitmap.width,
                height: bitmap.height,
                cleanup: () => { if (bitmap.close) { bitmap.close(); } },
            };
        } catch {
            // Fall through to the <img> path.
        }
    }

    const url = URL.createObjectURL(file);
    try {
        const img = new Image();
        img.decoding = 'async';
        await new Promise((resolve, reject) => {
            img.onload = resolve;
            img.onerror = reject;
            img.src = url;
        });

        return {
            source: img,
            width: img.naturalWidth,
            height: img.naturalHeight,
            cleanup: () => URL.revokeObjectURL(url),
        };
    } catch {
        URL.revokeObjectURL(url);
        return null;
    }
}
