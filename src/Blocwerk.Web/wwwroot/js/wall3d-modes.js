// Viewing modes of the 3D wall view (wall3d.js):
//   schematic — plain plywood facets with every hold as its coloured outline slab (no imagery);
//   photos    — the rectified per-facet photos, with the holds' traced outlines drawn over them;
//   photoreal — the captured Gaussian splat (wall3d-splat.js) instead of the modelled wall.
// Photos needs facet textures and photo-real a splat; a mode without its imagery is not offered.
import { describeShaderFailure } from './wall3d-splat-safe.js';

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
 * photo-real controller; `ui` gets setMode(mode, loading) and say(text); `request` asks for a frame;
 * `onMode(mode)` runs once a mode shows (the photo-real hold overlay, wall3d-overlay.js).
 */
export function createModeController({ modes, parts, photo, ui, request, PhotoRealUnsupportedError, onMode }) {
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
        onMode?.(name);
        ui.setMode(name, false);
        request();
    }

    return {
        get mode() { return mode; },
        /**
         * The photo-real view cannot go on (shader error, or its context lost for good): drops it,
         * falls back to Schematic and says so quietly, instead of leaving a black canvas. `shader`:
         * the failing program's numbered source, for the diagnostics beacon.
         */
        fail(message, reason, shader) {
            const showing = mode === 'photoreal';
            photo.fail(reason, shader);                // a switch still loading it now throws instead
            if (!showing) return pending;
            pending = pending.then(() => {
                showModelled('schematic');
                mode = 'schematic';
                onMode?.(mode);
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

/** What the viewer is told when the photo-real view cannot go on (a quiet note, not an alarm). */
export const PHOTO_REAL_FAILED = 'Photo-real is not available on this device right now, so the view shows Schematic.';

/**
 * Keeps render failures from leaving a black canvas. A lost WebGL context (iOS Safari drops it when
 * a frame is too heavy or the tab too big; three.js keeps it restorable) is the photo-real view's to
 * recover from (`photo.contextLost` / `contextRestored`, wall3d-splat.js: it resumes a level lower);
 * a shader that does not compile on this GPU is retried once on Spark's plainest path
 * (`photo.retrySimple`), then falls back to Schematic with a note (`modes.fail`).
 * `request` asks for a frame once the context is back. Returns `dispose()`.
 */
export function watchRenderFailures(renderer, modes, request, photo) {
    const canvas = renderer.domElement;
    const onLost = event => {
        console.warn('wall3d: WebGL context lost');
        event.preventDefault();
        photo.contextLost(event);
    };
    const onRestored = () => {
        photo.contextRestored();
        request();
    };
    canvas.addEventListener('webglcontextlost', onLost);
    canvas.addEventListener('webglcontextrestored', onRestored);
    renderer.debug.onShaderError = (gl, program, vertexShader, fragmentShader) => {
        const failure = describeShaderFailure(gl, program, vertexShader, fragmentShader);
        console.error('wall3d: shader failed to compile', failure.log);
        if (photo.retrySimple(failure)) return;
        modes.fail(PHOTO_REAL_FAILED, `shader: ${failure.log}`, failure.source);
    };
    return {
        dispose() {
            canvas.removeEventListener('webglcontextlost', onLost);
            canvas.removeEventListener('webglcontextrestored', onRestored);
            renderer.debug.onShaderError = null;
        },
    };
}
