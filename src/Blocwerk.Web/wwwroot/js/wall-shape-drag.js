/*
 * The wall editor's Shape tool (WallPhotoEditor, ShapeEditHandles), handled in the browser: dragging a
 * corner or control point, the eraser and the brush. Template: wall-hold-drag.js.
 *
 * Nothing talks to the server while an edit runs: samples go to the edit (wall-shape-ops.js), the preview is
 * drawn per frame (wall-shape-live.js), and the finished outline is sent ONCE on release (wall-shape-commit.js).
 *
 * An editor takes part by rendering data-shape-tool and data-hold-drag-key on its .photo-editor, the edited
 * hold's g.shape-edit (data-shape-*) and an empty g.shape-live, and by registering a .NET reference under the key.
 *
 * Pens and fingers (iPad):
 *  - Claimed presses call preventDefault on pointerdown, so no compatibility mouse events follow: the
 *    viewport's mouse pan and long-press lens never see them. Touch events are a separate stream; the
 *    viewport's recogniser (viewport-gestures.js) asks ownsTouch() and stands down while an edit runs.
 *  - A second finger cancels a finger edit and becomes the usual pinch / two-finger pan.
 *  - While a pen is down (and shortly after) a touch on the photo is a resting palm: ignored, and so is the
 *    click its lift produces. A pen that goes down during a finger edit (the palm landed first) takes over.
 *  - While a pen is in use, fingers no longer paint or erase: they pan and zoom, the pen draws.
 *  - Pen pressure widens the brush (wall-shape-ops.js). "Pen only" ends 30 s after the last pen input, shown by a
 *    pill at the photo's corner while it lasts (wall-shape-status.js).
 *  - A tap with the eraser erases what is under it; Escape during a press cancels just that press.
 */
window.wallShapeDrag = (function () {
    'use strict';

    /** Client pixels the pointer must travel before a press counts as an edit, not a tap. */
    const START_PX = 3;

    const editors = new Map();
    const pending = new Set();
    const pen = window.bwShapeStatus.penPolicy();
    let penTimer = 0;
    let op = null;
    let frame = 0;
    let swallowClickUntil = 0;
    let palmClickUntil = 0;
    const palms = new Set();
    let hoverLayer = null;
    function penActive() {
        return pen.palm();
    }

    /** Shows the "Pen only" pill while that mode lasts, and takes it away when it ends by itself. */
    function updatePenOnly(container) {
        clearTimeout(penTimer);
        if (!pen.penOnly()) {
            window.bwShapeStatus.showPenOnly(null);
            return;
        }

        window.bwShapeStatus.showPenOnly(container || document.querySelector('.photo-editor[data-shape-tool]'));
        penTimer = setTimeout(function () { updatePenOnly(container); }, 1000);
    }

    /** Swallows an event completely (a palm, the click that ends an edit). */
    function block(e) {
        e.preventDefault();
        e.stopImmediatePropagation();
    }

    function create(env, edit, e) {
        const handle = e.target.closest('[data-shape-handle]');
        if (env.tool === 'handles') {
            return handle ? window.bwShapeOps.handle(env, edit, Number(handle.getAttribute('data-shape-handle'))) : null;
        }

        // While a pen is in use, a finger navigates instead of painting.
        if (e.pointerType === 'touch' && pen.penOnly()) {
            return null;
        }

        return env.tool === 'eraser' ? window.bwShapeOps.eraser(env, edit)
            : env.tool === 'brush' ? window.bwShapeOps.brush(env, edit) : null;
    }

    function onDown(e) {
        const env = window.bwShapeOps.envFor(e.target, editors);
        if (e.pointerType === 'pen') {
            pen.down(e.pointerId);
            updatePenOnly(env && env.container);
        }

        // A palm that landed before the pen started a finger edit: drop it and let the pen draw.
        if (op && e.pointerType === 'pen' && op.pointerType === 'touch') {
            palms.add(op.pointerId);
            cancel();
        }

        if (op) {
            if (e.pointerId !== op.pointerId) {
                // A palm next to the pen is ignored; a second finger is a pinch and the edit is dropped.
                if (op.pointerType === 'pen' && e.pointerType === 'touch') {
                    palms.add(e.pointerId);
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
            palms.add(e.pointerId);
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
        if (e.pointerType === 'pen') {
            pen.touch();
        }

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

    /** A palm lifting: the click its lift produces is swallowed (it would pick the hold under the hand). */
    function palmUp(e) {
        if (palms.delete(e.pointerId)) {
            palmClickUntil = performance.now() + 400;
        }
    }

    function onUp(e) {
        palmUp(e);
        if (e.pointerType === 'pen') {
            pen.up(e.pointerId);
        }

        if (!op || e.pointerId !== op.pointerId) {
            return;
        }

        const o = finish();
        if (!o.moved) {
            // A tap with the eraser erases under it; any other tap (or one that erased nothing) stays a tap.
            if (o.kind !== 'erase' || !o.result()) {
                return;
            }

            window.bwShapeLive.begin(o.ctx);
        }

        // The press moved: the click the browser may still send is not a tap on a hold.
        swallowClickUntil = performance.now() + 400;
        const result = o.result();
        if (result) {
            window.bwShapeCommit.send(o, result, pending);
        } else {
            window.bwShapeLive.end(o.ctx);
        }
    }

    function onCancel(e) {
        palmUp(e);
        if (e.pointerType === 'pen') {
            pen.up(e.pointerId);
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
        const now = performance.now();
        // The click a resting palm's lift produces would pick another hold under it; a pen's own tap still counts.
        if (now < swallowClickUntil || (now < palmClickUntil && e.pointerType !== 'pen')) {
            swallowClickUntil = 0;
            block(e);
        }
    }, true);
    // Escape during a press cancels just that press: window capture runs before bwKeys (document capture), so the
    // editor's own Escape never sees it. Every other Escape is left to the dispatcher and its takeover rules.
    window.addEventListener('keydown', function (e) {
        if (op && e.key === 'Escape') {
            cancel();
            block(e);
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
