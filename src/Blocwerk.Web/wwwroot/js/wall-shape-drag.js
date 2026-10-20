/*
 * The wall editor's Shape tool (WallPhotoEditor, ShapeEditHandles), handled in the browser: dragging a
 * corner or control point, the eraser and the brush. Template: wall-hold-drag.js.
 *
 * Nothing talks to the server while an edit runs. Pointer samples go to the running edit (wall-shape-ops.js),
 * the preview is drawn once per animation frame (wall-shape-live.js), and the server hears about it ONCE, on
 * release, through CommitShapeEdit with the finished outline. It validates, writes the working copy (with an
 * undo snapshot, like every editor edit) and re-renders; only then is the preview taken away, so the outline
 * never jumps. A refused or failed commit takes the preview away too, which shows the hold as it was.
 *
 * An editor takes part by rendering data-shape-tool ("handles" | "eraser" | "brush") and data-hold-drag-key on
 * its .photo-editor, the edited hold's g.shape-edit (its points as data-shape-*) and an empty g.shape-live, and
 * by registering a .NET reference under the key.
 *
 * Pens and fingers (iPad):
 *  - Claimed presses call preventDefault on pointerdown, so no compatibility mouse events follow: the
 *    viewport's mouse pan and long-press lens never see them. Touch events are a separate stream; the
 *    viewport's recogniser (viewport-gestures.js) asks ownsTouch() and stands down while an edit runs.
 *  - A second finger cancels a finger edit and becomes the usual pinch / two-finger pan.
 *  - While a pen is down (and shortly after it lifts) every touch on the photo is a resting palm: ignored.
 *  - Once a pen has been used, fingers no longer paint or erase: they pan and zoom, the pen draws.
 *  - Pen pressure widens the brush (wall-shape-ops.js).
 */
window.wallShapeDrag = (function () {
    'use strict';

    /** Client pixels the pointer must travel before a press counts as an edit, not a tap. */
    const START_PX = 3;

    /** A touch this soon after the pen lifted is still the hand resting on the screen. */
    const PEN_GRACE_MS = 600;

    const editors = new Map();
    const pending = new Set();
    const pens = new Set();
    let penSeen = false;
    let penUpAt = -Infinity;
    let op = null;
    let frame = 0;
    let swallowClickUntil = 0;
    let hoverLayer = null;

    function penActive() {
        return pens.size > 0 || performance.now() - penUpAt < PEN_GRACE_MS;
    }

    /** Swallows an event completely: a resting palm, or the click that ends an edit. */
    function block(e) {
        e.preventDefault();
        e.stopImmediatePropagation();
    }

    function create(env, edit, e) {
        const handle = e.target.closest('[data-shape-handle]');
        if (env.tool === 'handles') {
            return handle ? window.bwShapeOps.handle(env, edit, Number(handle.getAttribute('data-shape-handle'))) : null;
        }

        // Once a pen is in use, a finger navigates instead of painting.
        if (e.pointerType === 'touch' && penSeen) {
            return null;
        }

        return env.tool === 'eraser' ? window.bwShapeOps.eraser(env, edit)
            : env.tool === 'brush' ? window.bwShapeOps.brush(env, edit) : null;
    }

    function onDown(e) {
        if (e.pointerType === 'pen') {
            pens.add(e.pointerId);
            penSeen = true;
        }

        const env = window.bwShapeOps.envFor(e.target, editors);
        if (op) {
            if (e.pointerId !== op.pointerId) {
                // A palm next to the pen is ignored; a second finger is a pinch and the edit is dropped.
                if (op.pointerType === 'pen' && e.pointerType === 'touch') {
                    block(e);
                } else {
                    cancel();
                }
            }

            return;
        }

        if (!env) {
            return;
        }

        if (e.pointerType === 'touch' && penActive()) {
            block(e);
            return;
        }

        if (!e.isPrimary || (e.pointerType === 'mouse' && e.button !== 0)) {
            return;
        }

        const edit = window.bwShapeOps.readEdit(env);
        const created = pending.has(edit.holdId) || edit.points.length < 3 ? null : create(env, edit, e);
        if (!created) {
            return;
        }

        e.preventDefault();
        op = Object.assign(created, {
            env: env, edit: edit, pointerId: e.pointerId, pointerType: e.pointerType,
            startX: e.clientX, startY: e.clientY, moved: false,
        });
        try {
            env.svg.setPointerCapture(e.pointerId);
        } catch (_) { /* a synthetic pointer cannot be captured */ }
        op.add(e);
    }

    function schedule() {
        if (!frame) {
            frame = requestAnimationFrame(function () {
                frame = 0;
                if (op && op.moved) {
                    op.paint();
                }
            });
        }
    }

    function onMove(e) {
        if (!op) {
            hover(e);
            return;
        }

        if (e.pointerId !== op.pointerId) {
            return;
        }

        if (!op.moved) {
            if (Math.hypot(e.clientX - op.startX, e.clientY - op.startY) < START_PX) {
                return;
            }

            op.moved = true;
            window.bwShapeLive.begin(op.ctx);
        }

        const samples = typeof e.getCoalescedEvents === 'function' ? e.getCoalescedEvents() : [];
        (samples.length ? samples : [e]).forEach(function (s) { op.add(s); });
        schedule();
    }

    function finish() {
        const o = op;
        op = null;
        if (frame) {
            cancelAnimationFrame(frame);
            frame = 0;
        }

        try {
            o.env.svg.releasePointerCapture(o.pointerId);
        } catch (_) { /* never captured, or already released */ }
        window.bwShapeLive.hideRing(o.env.layer);
        return o;
    }

    function cancel() {
        const o = finish();
        if (o.moved) {
            window.bwShapeLive.end(o.ctx);
        }
    }

    function flatten(points) {
        const flat = [];
        points.forEach(function (p) { flat.push(p.dx, p.dy); });
        return flat;
    }

    function commit(o, result) {
        const edit = o.edit;
        const outline = result.control ? window.bwShapeGeometry.densify(result.points) : result.points;
        window.bwShapeLive.draw(o.ctx, outline.map(function (p) { return { x: edit.cx + p.dx, y: edit.cy + p.dy }; }), null, null);
        pending.add(edit.holdId);
        const settle = function () {
            pending.delete(edit.holdId);
            window.bwShapeLive.end(o.ctx);
        };
        o.env.editor.invokeMethodAsync('CommitShapeEdit', edit.holdId, o.kind, flatten(result.points), result.control)
            .then(settle, settle);
    }

    function onUp(e) {
        if (e.pointerType === 'pen') {
            pens.delete(e.pointerId);
            penUpAt = performance.now();
        }

        if (!op || e.pointerId !== op.pointerId) {
            return;
        }

        const o = finish();
        if (!o.moved) {
            return;
        }

        // The press moved: the click the browser may still send is not a tap on a hold.
        swallowClickUntil = performance.now() + 400;
        const result = o.result();
        if (result) {
            commit(o, result);
        } else {
            window.bwShapeLive.end(o.ctx);
        }
    }

    function onCancel(e) {
        if (e.pointerType === 'pen') {
            pens.delete(e.pointerId);
            penUpAt = performance.now();
        }

        if (op && e.pointerId === op.pointerId) {
            cancel();
        }
    }

    /** The eraser's or brush's ring under a hovering mouse or pen, so the user sees what it will touch. */
    function hover(e) {
        if (e.pointerType === 'touch') {
            return;
        }

        const env = window.bwShapeOps.envFor(e.target, editors);
        if (!env || (env.tool !== 'eraser' && env.tool !== 'brush') || !env.layer) {
            window.bwShapeLive.hideRing(hoverLayer);
            hoverLayer = null;
            return;
        }

        const edit = window.bwShapeOps.readEdit(env);
        hoverLayer = env.layer;
        window.bwShapeOps.ring(env, window.bwShapeOps.rel(env, e), edit.radius,
            env.tool === 'eraser' ? 'eraser' : edit.subtract ? 'subtract' : 'brush');
    }

    document.addEventListener('pointerdown', onDown, true);
    window.addEventListener('pointermove', onMove, true);
    window.addEventListener('pointerup', onUp, true);
    window.addEventListener('pointercancel', onCancel, true);
    window.addEventListener('click', function (e) {
        if (performance.now() < swallowClickUntil) {
            swallowClickUntil = 0;
            block(e);
        }
    }, true);
    document.addEventListener('keydown', function (e) {
        if (op && e.key === 'Escape') {
            cancel();
        }
    }, true);

    return {
        register: function (key, dotnetRef) {
            editors.set(key, dotnetRef);
        },

        unregister: function (key) {
            editors.delete(key);
        },

        /**
         * For viewport-gestures.js: true when this touch belongs to the Shape tool (an edit is running, or a
         * pen is on the photo and the touch is the palm beside it), so the viewport must neither pan nor pinch.
         */
        ownsTouch: function (e) {
            return !!op || (penActive() && !!window.bwShapeOps.envFor(e.target, editors));
        },
    };
})();
