/*
 * Panel photo crop box (PanelCropTool.razor). The box is owned HERE while it is dragged, so it follows the finger
 * at frame rate instead of waiting for a server round trip; Blazor only hears the rectangle (throttled while
 * dragging, once more on release) to redraw the holds that would be cut and to save it.
 *
 * Every rectangle is { left, top, width, height } as fractions (0..1) of the shown photo — the same frame the
 * holds are stored in. Pointer events cover mouse, touch and pen; the handles are buttons, so arrow keys nudge
 * the focused edge (or the whole box) by 1 %, 5 % with Shift.
 */
const MIN = 0.1;
const NOTIFY_MS = 90;

const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));

/** The rectangle after moving `handle` by (dx, dy) fractions from `start`. */
function resize(start, handle, dx, dy) {
    let { left, top, width, height } = start;
    const right = left + width;
    const bottom = top + height;
    if (handle === 'move') {
        return { left: clamp(left + dx, 0, 1 - width), top: clamp(top + dy, 0, 1 - height), width, height };
    }
    if (handle.includes('l')) {
        left = clamp(left + dx, 0, right - MIN);
        width = right - left;
    }
    if (handle.includes('r')) {
        width = clamp(width + dx, MIN, 1 - left);
    }
    if (handle.includes('t')) {
        top = clamp(top + dy, 0, bottom - MIN);
        height = bottom - top;
    }
    if (handle.includes('b')) {
        height = clamp(height + dy, MIN, 1 - top);
    }
    return { left, top, width, height };
}

function paint(box, rect) {
    box.style.left = `${rect.left * 100}%`;
    box.style.top = `${rect.top * 100}%`;
    box.style.width = `${rect.width * 100}%`;
    box.style.height = `${rect.height * 100}%`;
}

const ARROWS = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] };

/** Wires the crop box on `stage`; returns a handle with setRect() and dispose(). */
export function attach(stage, box, dotnet, initial) {
    let rect = { ...initial };
    let drag = null;
    let lastNotify = 0;
    let pending = 0;

    const notify = (final) => {
        clearTimeout(pending);
        const now = performance.now();
        if (final || now - lastNotify >= NOTIFY_MS) {
            lastNotify = now;
            dotnet.invokeMethodAsync('OnCropRect', rect.left, rect.top, rect.width, rect.height).catch(() => { });
        } else {
            pending = setTimeout(() => notify(true), NOTIFY_MS);
        }
    };

    const onDown = (e) => {
        const target = e.target.closest('[data-crop-handle]');
        if (!target || !stage.contains(target) || e.button > 0) {
            return;
        }
        const bounds = stage.getBoundingClientRect();
        drag = { handle: target.dataset.cropHandle, x: e.clientX, y: e.clientY, start: { ...rect }, bounds, id: e.pointerId };
        stage.setPointerCapture(e.pointerId);
        stage.classList.add('dragging');
        e.preventDefault();
    };

    const onMove = (e) => {
        if (!drag || e.pointerId !== drag.id) {
            return;
        }
        const dx = (e.clientX - drag.x) / drag.bounds.width;
        const dy = (e.clientY - drag.y) / drag.bounds.height;
        rect = resize(drag.start, drag.handle, dx, dy);
        paint(box, rect);
        notify(false);
    };

    const onUp = (e) => {
        if (!drag || e.pointerId !== drag.id) {
            return;
        }
        drag = null;
        stage.classList.remove('dragging');
        notify(true);
    };

    const onKey = (e) => {
        const target = e.target.closest('[data-crop-handle]');
        const step = ARROWS[e.key];
        if (!target || !step) {
            return;
        }
        const amount = e.shiftKey ? 0.05 : 0.01;
        rect = resize(rect, target.dataset.cropHandle, step[0] * amount, step[1] * amount);
        paint(box, rect);
        notify(true);
        e.preventDefault();
    };

    stage.addEventListener('pointerdown', onDown);
    stage.addEventListener('pointermove', onMove);
    stage.addEventListener('pointerup', onUp);
    stage.addEventListener('pointercancel', onUp);
    stage.addEventListener('keydown', onKey);
    paint(box, rect);

    return {
        setRect(next) {
            rect = { ...next };
            paint(box, rect);
        },
        dispose() {
            clearTimeout(pending);
            stage.removeEventListener('pointerdown', onDown);
            stage.removeEventListener('pointermove', onMove);
            stage.removeEventListener('pointerup', onUp);
            stage.removeEventListener('pointercancel', onUp);
            stage.removeEventListener('keydown', onKey);
        },
    };
}
