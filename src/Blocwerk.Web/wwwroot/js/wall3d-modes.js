// Viewing modes of the 3D wall view (wall3d.js):
//   schematic — plain plywood facets with every hold as its coloured outline slab (no imagery);
//   photos    — the rectified per-facet photos, with the holds' traced outlines drawn over them;
//   photoreal — the captured Gaussian splat (wall3d-splat.js) instead of the modelled wall.
// Photos needs facet textures and photo-real a splat; a mode without its imagery is not offered.

export const MODE_LABELS = { schematic: 'Schematic', photos: 'Photos', photoreal: 'Photo-real' };

/** The modes this view can show, in switch order. */
export function availableModes(view, photoRealAvailable) {
    const modes = ['schematic'];
    if ((view.textures || []).length > 0) modes.push('photos');
    if (photoRealAvailable) modes.push('photoreal');
    return modes;
}

/** `?mode=` style names, forgiving about case and the "photo-real" spelling; null when unknown. */
export function normalizeMode(name) {
    const key = String(name || '').toLowerCase().replace(/[^a-z]/g, '');
    return MODE_LABELS[key] ? key : null;
}

/**
 * Switches the scene between modes. `parts`: { textures, slabs: [lit, dim], outlines }. `photo` is the
 * photo-real controller; `ui` gets setMode(mode, loading) and say(text); `request` asks for a frame.
 */
export function createModeController({ modes, parts, photo, ui, request, PhotoRealUnsupportedError }) {
    let mode = null;
    let pending = Promise.resolve();

    function showModelled(name) {
        parts.textures.visible = name === 'photos';
        parts.outlines.visible = name === 'photos';
        for (const s of parts.slabs) s.visible = name === 'schematic';
    }

    async function switchTo(name) {
        const previous = mode;
        if (name === 'photoreal') {
            ui.setMode(name, true);
            if (!photo.loaded) ui.say('Loading the photo-real view…');
            try {
                await photo.setActive(true);
                ui.hideHint();
            } catch (err) {
                ui.say(err instanceof PhotoRealUnsupportedError
                    ? 'This device cannot show the photo-real view (its graphics are too limited).'
                    : 'The photo-real view could not be loaded.');
                console.warn('wall3d: photo-real view failed', err);
                ui.setMode(previous, false);
                request();
                return;
            }
        } else {
            await photo.setActive(false);
            showModelled(name);
        }
        mode = name;
        ui.setMode(name, false);
        request();
    }

    return {
        get mode() { return mode; },
        /**
         * The photo-real view broke while showing (lost WebGL context, shader error): drops it, falls
         * back to Schematic and says why, instead of leaving a black canvas.
         */
        fail(message) {
            const showing = mode === 'photoreal';
            photo.fail();                // a switch still loading it now throws instead
            if (!showing) return pending;
            pending = pending.then(() => {
                showModelled('schematic');
                mode = 'schematic';
                ui.setMode(mode, false);
                ui.say(message);
                request();
            });
            return pending;
        },
        /** Switches to `name` (ignored when not offered); calls queue so a double tap cannot interleave. */
        set(name) {
            const next = normalizeMode(name);
            if (!next || !modes.includes(next) || next === mode) return pending;
            pending = pending.then(() => switchTo(next));
            return pending;
        },
    };
}

/** What the viewer is told when the photo-real view breaks while it shows. */
export const PHOTO_REAL_FAILED = 'The photo-real view was too heavy for this device, so it switched back to Schematic.';

/**
 * Turns render failures into a Schematic fallback with a message (`modes.fail`) instead of a black
 * canvas: a lost WebGL context (iOS Safari drops it when a frame is too heavy or the tab too big —
 * three.js keeps it restorable) and a shader that does not compile on this GPU. `request` asks for
 * a frame once the context is back. Returns `dispose()`.
 */
export function watchRenderFailures(renderer, modes, request) {
    const canvas = renderer.domElement;
    const onLost = () => {
        console.warn('wall3d: WebGL context lost');
        modes.fail(PHOTO_REAL_FAILED);
    };
    const onRestored = () => request();
    canvas.addEventListener('webglcontextlost', onLost);
    canvas.addEventListener('webglcontextrestored', onRestored);
    renderer.debug.onShaderError = (gl, program) => {
        console.error('wall3d: shader failed to compile', gl.getProgramInfoLog(program));
        modes.fail(PHOTO_REAL_FAILED);
    };
    return {
        dispose() {
            canvas.removeEventListener('webglcontextlost', onLost);
            canvas.removeEventListener('webglcontextrestored', onRestored);
            renderer.debug.onShaderError = null;
        },
    };
}
