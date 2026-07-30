/*
 * bwGestures — the input recogniser shared by every viewport in the app.
 *
 * It normalises wheel / mouse / touch / Safari-gesture input into three intents and
 * forwards them to a "transform model" (see viewport.js):
 *
 *   model.panBy(dx, dy)                              screen px, content follows finger
 *   model.zoomBy(factor, clientX, clientY, pdx, pdy) anchored zoom + simultaneous pan
 *   model.toggleDoubleTapZoom(clientX, clientY)
 *   model.canPanFrom(target) -> bool
 *
 * Every intent is applied exactly once, synchronously, per input event: there is no
 * deferred second pass that could run against stale geometry.
 */
window.bwGestures = (function () {
    'use strict';

    const DOUBLE_TAP_MS = 320;
    const DOUBLE_TAP_SLOP = 32;
    const NOTIFY_DEBOUNCE_MS = 150;

    function clamp(v, lo, hi) {
        return Math.max(lo, Math.min(hi, v));
    }

    /** Trailing-edge debounced .NET notification. Never on the critical path. */
    function makeNotifier(dotnet, method, argsFn) {
        let timer = 0;
        return function () {
            if (!dotnet) {
                return;
            }

            clearTimeout(timer);
            timer = setTimeout(function () {
                try {
                    dotnet.invokeMethodAsync.apply(dotnet, [method].concat(argsFn()));
                } catch (_) { /* circuit gone */ }
            }, NOTIFY_DEBOUNCE_MS);
        };
    }

    /**
     * Whether grabbing this element should suppress viewport panning.
     * Blazor's `@onmousedown:stopPropagation` runs via the root-delegated dispatcher,
     * i.e. AFTER the native event already bubbled past the viewport, so we cannot rely
     * on it — detect interactive targets ourselves.
     */
    function isInteractiveTarget(target) {
        if (!target) {
            return false;
        }

        const tag = (target.tagName || '').toLowerCase();
        if (tag === 'circle' || tag === 'polygon' || tag === 'rect' || tag === 'path' || tag === 'ellipse') {
            return true;
        }

        try {
            const cursor = window.getComputedStyle(target).cursor;
            if (cursor === 'pointer' || cursor === 'grab' || cursor === 'grabbing') {
                return true;
            }
        } catch (_) { /* ignore */ }
        return false;
    }

    function touchDistance(touches) {
        const dx = touches[0].clientX - touches[1].clientX;
        const dy = touches[0].clientY - touches[1].clientY;
        return Math.sqrt(dx * dx + dy * dy);
    }

    function touchCenter(touches) {
        return {
            x: (touches[0].clientX + touches[1].clientX) / 2,
            y: (touches[0].clientY + touches[1].clientY) / 2,
        };
    }

    /**
     * Normalises wheel / mouse / touch / Safari-gesture input into three intents on
     * `model`: panBy(dx, dy), zoomBy(factor, clientX, clientY, panDx, panDy) and
     * toggleDoubleTapZoom(clientX, clientY). All deltas are screen pixels and every
     * intent is applied exactly once per input event — no deferred second pass.
     */
    function bindGestures(el, model) {
        // ---- wheel: ctrl/cmd (and trackpad pinch, which browsers synthesise as
        // ctrl+wheel) zooms; plain wheel pans.
        el.addEventListener('wheel', function (e) {
            e.preventDefault();
            if (e.ctrlKey || e.metaKey) {
                model.zoomBy(Math.exp(-e.deltaY * 0.01), e.clientX, e.clientY, 0, 0);
            } else {
                model.panBy(-e.deltaX, -e.deltaY);
            }
        }, { passive: false });

        // ---- Safari macOS/iOS pinch. blockPageZoom() preventDefaults these on
        // `window`, but window listeners run in the bubble phase — the target-phase
        // listener here still sees them first.
        let gestureScale = 1;
        el.addEventListener('gesturestart', function (e) {
            e.preventDefault();
            gestureScale = 1;
        }, { passive: false });

        el.addEventListener('gesturechange', function (e) {
            e.preventDefault();
            const scale = e.scale || 1;
            model.zoomBy(scale / (gestureScale || 1), e.clientX, e.clientY, 0, 0);
            gestureScale = scale;
        }, { passive: false });

        el.addEventListener('gestureend', function (e) {
            e.preventDefault();
        }, { passive: false });

        // ---- mouse drag = pan
        let mouseDown = false;
        let mouseX = 0;
        let mouseY = 0;
        let mouseMoved = 0;

        el.addEventListener('mousedown', function (e) {
            if (e.button !== 0 && e.button !== 1) {
                return;
            }

            if (!model.canPanFrom(e.target)) {
                return;
            }

            mouseDown = true;
            mouseX = e.clientX;
            mouseY = e.clientY;
            mouseMoved = 0;
        });

        window.addEventListener('mousemove', function (e) {
            if (!mouseDown) {
                return;
            }

            const dx = e.clientX - mouseX;
            const dy = e.clientY - mouseY;
            mouseMoved += Math.abs(dx) + Math.abs(dy);
            if (mouseMoved > 3) {
                mouseX = e.clientX;
                mouseY = e.clientY;
                model.panBy(dx, dy);
                el.dataset.panActive = 'true';
            }
        });

        window.addEventListener('mouseup', function () {
            mouseDown = false;
            setTimeout(function () { delete el.dataset.panActive; }, 0);
        });

        el.addEventListener('dblclick', function (e) {
            if (!model.canPanFrom(e.target)) {
                return;
            }

            e.preventDefault();
            model.toggleDoubleTapZoom(e.clientX, e.clientY);
        });

        // ---- touch: 1 finger = pan, 2 fingers = one combined pinch-zoom + pan.
        let mode = null;
        let lastX = 0;
        let lastY = 0;
        let lastDist = 0;
        let lastCx = 0;
        let lastCy = 0;
        let touchMoved = 0;
        let lastTapAt = 0;
        let lastTapX = 0;
        let lastTapY = 0;

        el.addEventListener('touchstart', function (e) {
            if (e.touches.length === 2) {
                e.preventDefault();
                mode = 'pinch';
                lastDist = touchDistance(e.touches);
                const c = touchCenter(e.touches);
                lastCx = c.x;
                lastCy = c.y;
            } else if (e.touches.length === 1) {
                if (!model.canPanFrom(e.target)) {
                    mode = null;
                    return;
                }

                mode = 'pan';
                lastX = e.touches[0].clientX;
                lastY = e.touches[0].clientY;
                touchMoved = 0;
            }
        }, { passive: false });

        el.addEventListener('touchmove', function (e) {
            if (mode === 'pinch' && e.touches.length === 2) {
                e.preventDefault();
                const dist = touchDistance(e.touches);
                const c = touchCenter(e.touches);
                // Incremental, so it can never fight a stale start-anchor: scale and
                // centre movement since the previous frame are fed to ONE call that
                // zooms about the live centre and pans by the centre delta.
                model.zoomBy(dist / Math.max(lastDist, 1), c.x, c.y, c.x - lastCx, c.y - lastCy);
                lastDist = dist;
                lastCx = c.x;
                lastCy = c.y;
                el.dataset.panActive = 'true';
            } else if (mode === 'pan' && e.touches.length === 1) {
                const dx = e.touches[0].clientX - lastX;
                const dy = e.touches[0].clientY - lastY;
                touchMoved += Math.abs(dx) + Math.abs(dy);
                if (touchMoved > 4) {
                    e.preventDefault();
                    lastX = e.touches[0].clientX;
                    lastY = e.touches[0].clientY;
                    model.panBy(dx, dy);
                    el.dataset.panActive = 'true';
                }
            }
        }, { passive: false });

        el.addEventListener('touchend', function (e) {
            // Lifting one finger of a pinch continues as a pan with the survivor.
            if (e.touches.length === 1 && mode === 'pinch') {
                mode = 'pan';
                lastX = e.touches[0].clientX;
                lastY = e.touches[0].clientY;
                touchMoved = Number.MAX_SAFE_INTEGER;
                return;
            }

            if (e.touches.length > 0) {
                return;
            }

            const wasTap = mode === 'pan' && touchMoved <= 4;
            mode = null;
            setTimeout(function () { delete el.dataset.panActive; }, 0);

            const t = e.changedTouches[0];
            if (!wasTap || !t) {
                return;
            }

            const now = Date.now();
            if (now - lastTapAt < DOUBLE_TAP_MS &&
                Math.abs(t.clientX - lastTapX) < DOUBLE_TAP_SLOP &&
                Math.abs(t.clientY - lastTapY) < DOUBLE_TAP_SLOP) {
                e.preventDefault(); // suppress the synthesised dblclick
                model.toggleDoubleTapZoom(t.clientX, t.clientY);
                lastTapAt = 0;
            } else {
                lastTapAt = now;
                lastTapX = t.clientX;
                lastTapY = t.clientY;
            }
        }, { passive: false });
    }

    return {
        bind: bindGestures,
        clamp: clamp,
        makeNotifier: makeNotifier,
        isInteractiveTarget: isInteractiveTarget,
    };
})();

