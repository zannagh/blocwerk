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
        // ---- Long-press magnifier lens (~8x) on the wall photo.
        // A stationary press-and-hold (touch or mouse) after LENS_HOLD_MS pops a circular
        // lens ABOVE the finger/cursor that magnifies the wall image at the press point. It
        // follows the press while held and is removed on release, on a move past the slop
        // BEFORE it appears, on a second finger, or on scroll/wheel/pinch. Only arms over a
        // `.photo-editor img.wall-photo` and never on an interactive target (a hold), so the
        // editor's own hold drag/tap is untouched. getBoundingClientRect() on the image
        // already folds in the current zoom/crop/scroll, so the coord math needs nothing else.
        const LENS_HOLD_MS = 450;
        const LENS_SLOP = 8;
        const LENS_SIZE = 140;
        const LENS_MAG_DEFAULT = 2;
        const LENS_MAG_MIN = 1;
        const LENS_MAG_MAX = 16;
        // The selector value is a user-facing "dial" number; the effective magnification actually
        // applied to the image is a quarter of it (the lens already enlarges somewhat on its own).
        const LENS_MAG_SCALE = 0.25;
        let lensTimer = 0;
        let lensEl = null;
        let lensImg = null;
        let lensActive = false;

        function lensPhoto() {
            return el.querySelector('.photo-editor img.wall-photo');
        }

        // The current magnification, read live from the client pref each time the lens updates so a
        // change on the Profile page takes effect without a reload. Defensive: any missing pref /
        // NaN / out-of-range value falls back to the default.
        function lensMag() {
            try {
                if (window.bwPrefs && typeof window.bwPrefs.getZoomLensMag === 'function') {
                    const m = window.bwPrefs.getZoomLensMag();
                    if (typeof m === 'number' && m >= LENS_MAG_MIN && m <= LENS_MAG_MAX) {
                        return m;
                    }
                }
            } catch (_) { /* fall through to default */ }
            return LENS_MAG_DEFAULT;
        }

        function armLens(clientX, clientY, target) {
            cancelLens();
            if (isInteractiveTarget(target)) {
                return; // a hold: leave it to the editor's own drag/tap
            }

            const img = lensPhoto();
            if (!img || !(img.naturalWidth > 0)) {
                return;
            }

            lensImg = img;
            lensTimer = setTimeout(function () {
                lensTimer = 0;
                showLens(clientX, clientY);
            }, LENS_HOLD_MS);
        }

        function showLens(clientX, clientY) {
            if (!lensImg) {
                return;
            }

            if (!lensEl) {
                lensEl = document.createElement('div');
                lensEl.className = 'bw-zoom-lens';
                lensEl.style.cssText = 'position:fixed; z-index:2000; width:' + LENS_SIZE +
                    'px; height:' + LENS_SIZE + 'px; border-radius:50%; border:3px solid ' +
                    'rgba(255,255,255,0.9); box-shadow:0 6px 24px rgba(0,0,0,0.45); ' +
                    'background-repeat:no-repeat; background-color:#000; pointer-events:none; display:none;';
                document.body.appendChild(lensEl);
            }

            lensEl.style.backgroundImage = 'url("' + (lensImg.currentSrc || lensImg.src) + '")';
            lensEl.style.display = 'block';
            lensActive = true;
            updateLens(clientX, clientY);
        }

        function updateLens(clientX, clientY) {
            if (!lensActive || !lensEl || !lensImg) {
                return;
            }

            const r = lensImg.getBoundingClientRect();
            if (!(r.width > 0 && r.height > 0)) {
                return;
            }

            // Fraction of the (currently rendered) image under the press, from the live rect so the
            // lens still centres on the finger/cursor at any viewport zoom.
            const fx = clamp((clientX - r.left) / r.width, 0, 1);
            const fy = clamp((clientY - r.top) / r.height, 0, 1);

            // Magnify M× the image's ORIGINAL resolution, independent of the viewport's current
            // --zoom, so the effective magnification never compounds with it. Fall back to the
            // on-screen rect only when the natural size is unavailable.
            const M = lensMag() * LENS_MAG_SCALE;
            const bgW = lensImg.naturalWidth > 0 ? lensImg.naturalWidth * M : r.width * M;
            const bgH = lensImg.naturalHeight > 0 ? lensImg.naturalHeight * M : r.height * M;
            lensEl.style.backgroundSize = bgW + 'px ' + bgH + 'px';
            lensEl.style.backgroundPosition =
                (LENS_SIZE / 2 - fx * bgW) + 'px ' + (LENS_SIZE / 2 - fy * bgH) + 'px';

            // Float above the finger/cursor; drop below only when it would clip the top edge.
            const left = clamp(clientX - LENS_SIZE / 2, 4, window.innerWidth - LENS_SIZE - 4);
            let top = clientY - 24 - LENS_SIZE;
            if (top < 4) {
                top = clientY + 24;
            }

            lensEl.style.left = left + 'px';
            lensEl.style.top = top + 'px';
        }

        function cancelLens() {
            if (lensTimer) {
                clearTimeout(lensTimer);
                lensTimer = 0;
            }

            if (lensActive && lensEl) {
                lensEl.style.display = 'none';
            }

            lensActive = false;
            lensImg = null;
        }

        // ---- wheel: ctrl/cmd (and trackpad pinch, which browsers synthesise as
        // ctrl+wheel) zooms; plain wheel pans.
        el.addEventListener('scroll', cancelLens, { passive: true });
        el.addEventListener('wheel', function (e) {
            cancelLens();
            if (e.ctrlKey || e.metaKey) {
                e.preventDefault();
                model.zoomBy(Math.exp(-e.deltaY * 0.01), e.clientX, e.clientY, 0, 0);
            } else if (!model.capturesPan || model.capturesPan()) {
                e.preventDefault();
                model.panBy(-e.deltaX, -e.deltaY);
            }
            // Otherwise the viewport is at fit: let the wheel scroll the page.
        }, { passive: false });

        // ---- Safari macOS/iOS pinch. blockPageZoom() preventDefaults these on
        // `window`, but window listeners run in the bubble phase — the target-phase
        // listener here still sees them first.
        let gestureScale = 1;
        el.addEventListener('gesturestart', function (e) {
            e.preventDefault();
            cancelLens();
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

        // Suppress the browser's native image drag (the drag ghost) so a left-click-drag
        // pans the zoomed viewport instead of trying to copy the image. Unconditional:
        // native image drag is never wanted here. Mouse/touch events still flow normally.
        el.addEventListener('dragstart', function (e) {
            e.preventDefault();
        });

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
            armLens(e.clientX, e.clientY, e.target);
        });

        window.addEventListener('mousemove', function (e) {
            if (!mouseDown) {
                return;
            }

            if (lensActive) {
                updateLens(e.clientX, e.clientY);
                return; // lens owns the gesture; don't also pan
            }

            const dx = e.clientX - mouseX;
            const dy = e.clientY - mouseY;
            mouseMoved += Math.abs(dx) + Math.abs(dy);
            if (mouseMoved > LENS_SLOP) {
                cancelLens(); // moved before the hold fired: it's a drag, not a long-press
            }

            if (mouseMoved > 3) {
                if (model.capturesPan && !model.capturesPan()) {
                    return; // at fit: nothing to pan
                }

                mouseX = e.clientX;
                mouseY = e.clientY;
                model.panBy(dx, dy);
                el.dataset.panActive = 'true';
            }
        });

        window.addEventListener('mouseup', function () {
            mouseDown = false;
            cancelLens();
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
        // The one-finger gesture began on a hold / interactive shape. We still track
        // it as a *potential* pan (drag-beats-tap): only a stationary touch is handed
        // back to the shape as a tap; the moment the finger travels past the slop it
        // becomes a pan of the viewport instead.
        let startInteractive = false;
        let lastTapAt = 0;
        let lastTapX = 0;
        let lastTapY = 0;

        el.addEventListener('touchstart', function (e) {
            if (e.touches.length === 2) {
                e.preventDefault();
                cancelLens(); // second finger: this is a pinch, not a long-press
                mode = 'pinch';
                lastDist = touchDistance(e.touches);
                const c = touchCenter(e.touches);
                lastCx = c.x;
                lastCy = c.y;
            } else if (e.touches.length === 1) {
                // Always arm a pan, even when the touch lands on a hold. We defer the
                // pan/tap decision to how far the finger travels (see touchmove/touchend)
                // rather than refusing to pan up-front — otherwise grabbing a hold would
                // freeze the whole viewport for the gesture.
                mode = 'pan';
                startInteractive = !model.canPanFrom(e.target);
                lastX = e.touches[0].clientX;
                lastY = e.touches[0].clientY;
                touchMoved = 0;
                armLens(e.touches[0].clientX, e.touches[0].clientY, e.target);
            }
        }, { passive: false });

        el.addEventListener('touchmove', function (e) {
            // Once the lens is up it owns the single-finger gesture: it follows the finger
            // and the viewport does not pan until release.
            if (lensActive && e.touches.length === 1) {
                e.preventDefault();
                updateLens(e.touches[0].clientX, e.touches[0].clientY);
                return;
            }

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
                if (touchMoved > LENS_SLOP) {
                    cancelLens(); // moved before the hold fired: it's a pan, not a long-press
                }

                if (touchMoved > 4) {
                    if (model.capturesPan && !model.capturesPan()) {
                        // At fit: don't preventDefault, so the browser scrolls the page
                        // (touch-action: pan-y). A tap still registers for double-tap zoom.
                        return;
                    }

                    e.preventDefault();
                    lastX = e.touches[0].clientX;
                    lastY = e.touches[0].clientY;
                    model.panBy(dx, dy);
                    el.dataset.panActive = 'true';
                }
            }
        }, { passive: false });

        el.addEventListener('touchend', function (e) {
            // Tear down the lens (or a still-pending long-press timer). If it was showing,
            // this release belongs to the lens: swallow it so it doesn't also fire a tap /
            // double-tap zoom.
            const wasLens = lensActive;
            cancelLens();
            if (wasLens && e.touches.length === 0) {
                e.preventDefault();
                mode = null;
                setTimeout(function () { delete el.dataset.panActive; }, 0);
                return;
            }

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

            // A tap that began on a hold belongs to the hold: leave it to that element's
            // own click handling and don't hijack it for double-tap zoom. A drag (moved
            // past the slop) never reaches here as a tap, and because the finger moved
            // the browser won't synthesise a click, so the hold is not toggled either.
            const wasTap = mode === 'pan' && touchMoved <= 4 && !startInteractive;
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

