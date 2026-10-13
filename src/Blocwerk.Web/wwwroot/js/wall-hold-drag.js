/*
 * Moving a hold in the wall editor (WallPhotoEditor, Move tool), handled in the browser.
 *
 * A drag never talks to the server while it runs: the pointer moves translate the dragged hold's own
 * SVG elements (every element carrying data-hold="<id>") once per animation frame, and the server
 * hears about it once, on release, as the distance moved. The server writes the hold, re-renders and
 * answers; only then is the translate taken off again, so the hold never jumps. If the server says no,
 * or the call fails, taking the translate off puts the hold back where it was.
 *
 * An editor takes part by rendering data-hold-drag-key on its .photo-editor and registering a .NET
 * reference under that key. The existing [data-hold-drag] marker (Move mode on) stays the switch, so
 * viewport-gestures.js keeps handing a one-finger gesture on a hold to this drag instead of panning.
 */
window.wallHoldDrag = (function () {
    'use strict';

    /** Client pixels the pointer must travel before a press counts as a drag, not a tap. */
    const START_PX = 3;
    const editors = new Map();
    let drag = null;
    const pending = new Set();

    function elementsOf(d) {
        return d.svg.querySelectorAll('[data-hold="' + d.id + '"]');
    }

    function clear(d) {
        for (const el of elementsOf(d)) {
            el.removeAttribute('transform');
        }
    }

    function paint(d) {
        d.frame = 0;
        const value = 'translate(' + (d.dx * 100).toFixed(3) + ' ' + (d.dy * 100).toFixed(3) + ')';
        for (const el of elementsOf(d)) {
            el.setAttribute('transform', value);
        }
    }

    function listen(on) {
        const change = on ? window.addEventListener : window.removeEventListener;
        change.call(window, 'pointermove', onMove, true);
        change.call(window, 'pointerup', onUp, true);
        change.call(window, 'pointercancel', onCancel, true);
    }

    function finish() {
        const d = drag;
        drag = null;
        listen(false);
        if (d.frame) {
            cancelAnimationFrame(d.frame);
        }

        try {
            d.target.releasePointerCapture(d.pointerId);
        } catch (_) { /* never captured, or already released */ }
        return d;
    }

    function onCancel() {
        if (!drag) {
            return;
        }

        clear(finish());
    }

    function onDown(e) {
        if (drag) {
            // A second finger: this is a pinch, the hold goes back.
            if (e.pointerId !== drag.pointerId) {
                onCancel();
            }

            return;
        }

        if (!e.isPrimary || (e.pointerType === 'mouse' && e.button !== 0)) {
            return;
        }

        const shape = e.target && e.target.closest ? e.target.closest('[data-hold]') : null;
        const container = shape ? shape.closest('.photo-editor[data-hold-drag][data-hold-drag-key]') : null;
        const editor = container ? editors.get(container.getAttribute('data-hold-drag-key')) : null;
        if (!editor || !shape.ownerSVGElement) {
            return;
        }

        // The server has not answered this hold's last move yet: its place is not known here.
        if (pending.has(shape.getAttribute('data-hold'))) {
            return;
        }

        // The hold's centre in the 0..100 viewBox, to keep the drag inside the photo (the server
        // clamps again, on the real centre).
        let cx = 50;
        let cy = 50;
        try {
            const box = shape.getBBox();
            cx = box.x + box.width / 2;
            cy = box.y + box.height / 2;
        } catch (_) { /* not rendered: the server's clamp is enough */ }

        drag = {
            pointerId: e.pointerId, id: shape.getAttribute('data-hold'), target: shape, svg: shape.ownerSVGElement,
            container: container, editor: editor, startX: e.clientX, startY: e.clientY,
            start: window.wallEditor.getRelativePosition(container, e.clientX, e.clientY),
            cx: cx / 100, cy: cy / 100, dx: 0, dy: 0, moved: false, frame: 0
        };
        try {
            shape.setPointerCapture(e.pointerId);
        } catch (_) { /* a synthetic pointer cannot be captured */ }
        listen(true);
    }

    function onMove(e) {
        const d = drag;
        if (!d || e.pointerId !== d.pointerId) {
            return;
        }

        if (!d.moved && Math.hypot(e.clientX - d.startX, e.clientY - d.startY) < START_PX) {
            return;
        }

        d.moved = true;
        const now = window.wallEditor.getRelativePosition(d.container, e.clientX, e.clientY);
        d.dx = Math.min(1 - d.cx, Math.max(-d.cx, now.x - d.start.x));
        d.dy = Math.min(1 - d.cy, Math.max(-d.cy, now.y - d.start.y));
        if (!d.frame) {
            d.frame = requestAnimationFrame(function () { paint(d); });
        }
    }

    function settle(d) {
        pending.delete(d.id);
        clear(d);
    }

    function onUp(e) {
        if (!drag || e.pointerId !== drag.pointerId) {
            return;
        }

        const d = finish();
        if (!d.moved) {
            return;
        }

        paint(d);
        pending.add(d.id);
        d.editor.invokeMethodAsync('CommitHoldMove', d.id, d.dx, d.dy)
            .then(function () { settle(d); }, function () { settle(d); });
    }

    document.addEventListener('pointerdown', onDown, true);

    return {
        /** Registers the editor rendered with data-hold-drag-key="key". */
        register: function (key, dotnetRef) {
            editors.set(key, dotnetRef);
        },

        unregister: function (key) {
            editors.delete(key);
        }
    };
})();
