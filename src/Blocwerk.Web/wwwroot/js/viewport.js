/*
 * bwViewport — the single viewport (pan/zoom) engine for the whole app.
 *
 * Design rule: **JS is the sole synchronous authority for the viewport transform.**
 * Every gesture writes the DOM immediately, in the same task as the event, and only
 * *afterwards* notifies Blazor on a trailing debounce, purely so the server can show
 * a zoom read-out. Blazor must never render the authoritative geometry: if Razor
 * emitted `style="width: N%"` a late server re-render would stomp what JS set and
 * the zoom anchor would visibly jump. Instead the content element's width comes from
 * CSS (`width: calc(var(--zoom, 1) * 100%)`) and JS owns the `--zoom` custom property.
 *
 * Two transform models share one gesture recogniser (viewport-gestures.js):
 *   - scroll model    : content grows via width%, position via scrollLeft/scrollTop
 *                       (wall photo / schematic / hold picker surfaces)
 *   - transform model : `translate(px,py) scale(z)` on a world layer
 *                       (the image stitcher, whose layers live in world space)
 */
window.bwViewport = (function () {
    'use strict';

    const clamp = window.bwGestures.clamp;
    const makeNotifier = window.bwGestures.makeNotifier;
    const isInteractiveTarget = window.bwGestures.isInteractiveTarget;
    const bindGestures = window.bwGestures.bind;

    // The content has `min-width: 100%`, so anything below 1.0 cannot shrink it —
    // a smaller floor would be a dead range. 1.0 *is* "fit".
    const SCROLL_ZOOM_MIN = 1.0;
    const SCROLL_ZOOM_MAX = 6.0;
    const DOUBLE_TAP_ZOOM = 2.5;
    const TRANSFORM_ZOOM_MIN = 0.02;
    const TRANSFORM_ZOOM_MAX = 20.0;

    /**
     * Scroll model. `viewport` scrolls; its content element is widened by the
     * `--zoom` custom property (see the CSS `calc(var(--zoom, 1) * 100%)` rules).
     * The content element is resolved lazily because consumers swap it (photo vs map).
     */
    function scrollModel(viewport, dotnet) {
        let zoom = 1;
        const notify = makeNotifier(dotnet, 'SetZoomFromJs', function () { return [zoom]; });

        // A re-render can swap the content element (photo <-> map). A fresh element
        // starts at `--zoom: 1`, so adopt that as the truth rather than scaling from
        // a stale JS value.
        function syncContent() {
            const el = viewport.firstElementChild;
            if (el && el._bwZoom !== zoom) {
                zoom = 1;
                el._bwZoom = 1;
                el.style.setProperty('--zoom', 1);
            }

            return el;
        }

        // At fit (zoom 1) the viewport must NOT trap a drag/wheel — it has nothing to
        // scroll, so the gesture belongs to the page. Only once zoomed in does it own
        // the gesture. `touch-action: pan-y` lets a one-finger drag scroll the page
        // through the viewport; `none` hands every touch to our pan/zoom code.
        function isZoomed() {
            return zoom > SCROLL_ZOOM_MIN + 0.01;
        }

        function refreshTouchAction() {
            viewport.style.touchAction = isZoomed() ? 'none' : 'pan-y';
        }

        function applyZoom(z) {
            zoom = z;
            const el = viewport.firstElementChild;
            if (el) {
                el._bwZoom = z;
                el.style.setProperty('--zoom', z);
            }

            refreshTouchAction();

            // Force a layout flush so the browser knows the NEW content width before
            // we write scrollLeft/scrollTop. Without this it clamps the scroll offset
            // against the old width and the zoom anchor is destroyed.
            void viewport.scrollWidth;

            // The content layer just changed width, so the photo inside it is now displayed at a
            // different pixel size. Ask for a sharper rendition if zooming in has outgrown the one
            // on screen — debounced to one measurement per frame, and never a downgrade, so this
            // cannot churn mid-gesture. Purely additive: the transform above is already committed.
            if (window.bwImageRes) {
                window.bwImageRes.refresh(viewport);
            }
        }

        refreshTouchAction();

        return {
            getZoom: function () { return zoom; },
            // Whether the viewport consumes pan/scroll gestures. At fit it does not, so
            // the page scrolls normally instead of the drag being swallowed.
            capturesPan: function () { return isZoomed(); },
            canPanFrom: function (target) {
                // `data-pan-mode="true"` = explicit pan mode: drag from anywhere,
                // including on top of holds (their taps are ignored while it is on).
                return viewport.dataset.panMode === 'true' || !isInteractiveTarget(target);
            },
            panBy: function (dx, dy) {
                viewport.scrollLeft -= dx;
                viewport.scrollTop -= dy;
            },
            zoomBy: function (factor, cx, cy, panDx, panDy) {
                this.zoomTo(zoom * factor, cx, cy, panDx, panDy);
            },
            zoomTo: function (target, cx, cy, panDx, panDy) {
                syncContent();
                const next = clamp(target, SCROLL_ZOOM_MIN, SCROLL_ZOOM_MAX);
                const rect = viewport.getBoundingClientRect();
                const ax = cx == null ? rect.width / 2 : cx - rect.left;
                const ay = cy == null ? rect.height / 2 : cy - rect.top;
                const contentX = (viewport.scrollLeft + ax) / zoom;
                const contentY = (viewport.scrollTop + ay) / zoom;

                applyZoom(next);

                // The anchored content point ends up at (ax, ay) shifted by the pan.
                viewport.scrollLeft = (contentX * next) - (ax + (panDx || 0));
                viewport.scrollTop = (contentY * next) - (ay + (panDy || 0));
                notify();
            },
            toggleDoubleTapZoom: function (cx, cy) {
                this.zoomTo(zoom > SCROLL_ZOOM_MIN + 0.01 ? SCROLL_ZOOM_MIN : DOUBLE_TAP_ZOOM, cx, cy, 0, 0);
            },
            // Pan/zoom so a normalized content point (xNorm, yNorm in 0..1) sits at the
            // viewport centre. Used by the big-wall overlap stepper to auto-focus the current
            // hold pair. scrollWidth/scrollHeight are read AFTER applyZoom flushes layout, so
            // they reflect the widened content and the anchor lands correctly.
            centerOn: function (xNorm, yNorm, targetZoom) {
                syncContent();
                if (targetZoom != null) {
                    applyZoom(clamp(targetZoom, SCROLL_ZOOM_MIN, SCROLL_ZOOM_MAX));
                }

                const sw = viewport.scrollWidth;
                const sh = viewport.scrollHeight;
                viewport.scrollLeft = clamp(xNorm * sw - viewport.clientWidth / 2, 0, sw);
                viewport.scrollTop = clamp(yNorm * sh - viewport.clientHeight / 2, 0, sh);
                notify();
            },
        };
    }

    /** Transform model: pan/zoom a world layer via a CSS transform. */
    function transformModel(viewport, world, dotnet) {
        let zoom = 1;
        let panX = 0;
        let panY = 0;
        const notify = makeNotifier(dotnet, 'SetTransformFromJs', function () { return [panX, panY, zoom]; });

        function apply() {
            world.style.transform = 'translate(' + panX + 'px, ' + panY + 'px) scale(' + zoom + ')';
        }

        return {
            getState: function () { return { panX: panX, panY: panY, zoom: zoom }; },
            setState: function (px, py, z) {
                panX = px;
                panY = py;
                zoom = clamp(z, TRANSFORM_ZOOM_MIN, TRANSFORM_ZOOM_MAX);
                apply();
                notify();
            },
            getZoom: function () { return zoom; },
            // The stitcher is a dedicated full-surface tool: it always owns the gesture.
            capturesPan: function () { return true; },
            canPanFrom: function (target) {
                // Layers and gizmo handles are dragged by Blazor; everything else pans.
                return !(target && target.closest &&
                    target.closest('.stitcher-layer, .stitcher-overlay circle, .stitcher-zoom, button, input, select'));
            },
            panBy: function (dx, dy) {
                panX += dx;
                panY += dy;
                apply();
                notify();
            },
            zoomBy: function (factor, cx, cy, panDx, panDy) {
                const rect = viewport.getBoundingClientRect();
                const sx = cx == null ? rect.width / 2 : cx - rect.left;
                const sy = cy == null ? rect.height / 2 : cy - rect.top;
                const worldX = (sx - panX) / zoom;
                const worldY = (sy - panY) / zoom;
                zoom = clamp(zoom * factor, TRANSFORM_ZOOM_MIN, TRANSFORM_ZOOM_MAX);
                panX = sx + (panDx || 0) - (worldX * zoom);
                panY = sy + (panDy || 0) - (worldY * zoom);
                apply();
                notify();
            },
            toggleDoubleTapZoom: function (cx, cy) {
                this.zoomBy(2, cx, cy, 0, 0);
            },
        };
    }

    function modelOf(viewport) {
        return viewport ? viewport._bwModel : null;
    }

    // Every viewport node attachSwipe has bound, so detachSwipe can also unbind nodes that a
    // re-render has already replaced. Small and bounded — one entry per live panel viewer, cleared
    // on detach — and the page has at most a couple of those.
    const bwSwipeBound = [];

    return {
        /** Attaches the scroll model. Safe to call repeatedly. */
        setupScroll: function (viewport, dotnetHelper) {
            // A stale ElementReference — the element was replaced by a re-render or a reconnect
            // before this interop call ran — marshals to JS as an object with no live `.style`.
            // Reading `.style` on it (see refreshTouchAction) throws a JSException that Blazor
            // reports as an *unhandled circuit error* and tears the whole circuit down, which the
            // user sees as "something went wrong — Reload". If it is not a live element there is
            // nothing to attach to, so bail quietly.
            if (!viewport || !viewport.style || viewport._bwModel) {
                return;
            }

            const model = scrollModel(viewport, dotnetHelper);
            viewport._bwModel = model;
            bindGestures(viewport, model);
        },

        /**
         * Pins a scroll-model viewport's height to its image's aspect ratio, so the box is sized
         * to the fit image (no dead band) and — crucially — stays that height while zooming, with
         * the widened content scrolling inside it instead of growing the card. The `--zoom` width
         * only ever changes the content, never this box, once the aspect ratio is fixed here.
         *
         * Also publishes the aspect as a numeric `--fit-aspect` custom property on the box, which
         * the fullscreen/desktop layout uses to contain the view within the window: CSS caps the
         * width at `availableHeight * var(--fit-aspect)` so a tall image narrows instead of
         * overflowing (see the `.bw-fullscreen` rules). `--fit-aspect` is enough on its own — pass
         * `pinAspect === false` (the wall editor) to publish it WITHOUT also writing an inline
         * `aspect-ratio`, so the box keeps its flex-driven height in mobile/narrow mode and only
         * the fullscreen CSS opts it into aspect sizing.
         *
         * Optional `frame` = [minX, minY, maxX, maxY] in the image's normalized 0..1 space frames the
         * view to that sub-region (the actual wall) instead of the whole photo. It is a pure crop of
         * the SHARED content layer — the box aspect becomes the region's *pixel* aspect
         * ((frameW*naturalW)/(frameH*naturalH)), the content is widened by `--crop-scale = 1/frameW`
         * (composing with `--zoom` in the CSS width calc), and the viewport is scrolled so the region
         * fills the box. Because the img and the hold SVG are both inside that one content layer and
         * scale/scroll together, holds stay aligned and the SVG viewBox never has to change. With no
         * (or a degenerate) frame it clears the crop and behaves exactly as before.
         */
        fitBox: function (viewport, pinAspect, frame) {
            // Guard a stale/non-element reference the same way setupScroll does.
            if (!viewport || typeof viewport.querySelector !== 'function') {
                return;
            }

            const img = viewport.querySelector('img');
            if (!img) {
                return;
            }

            const pin = pinAspect !== false;

            // A frame is used only when it is a real, non-degenerate sub-rectangle.
            let fx0, fy0, fw, fh;
            const framed = Array.isArray(frame) && frame.length === 4 &&
                (fx0 = frame[0], fy0 = frame[1], fw = frame[2] - frame[0], fh = frame[3] - frame[1],
                    fw > 0.001 && fh > 0.001);

            const apply = function () {
                if (!(img.naturalWidth > 0 && img.naturalHeight > 0)) {
                    return;
                }

                if (framed) {
                    const aspect = (fw * img.naturalWidth) / (fh * img.naturalHeight);
                    viewport.style.setProperty('--fit-aspect', aspect);
                    viewport.style.setProperty('--crop-scale', 1 / fw);
                    if (pin) {
                        viewport.style.aspectRatio = String(aspect);
                    }

                    // Read the widened scroll size AFTER the crop scale/aspect land, then scroll so the
                    // region sits centred (which, with the box at the region's aspect, is also flush).
                    void viewport.scrollWidth;
                    const sw = viewport.scrollWidth;
                    const sh = viewport.scrollHeight;
                    viewport.scrollLeft = clamp((fx0 + fw / 2) * sw - viewport.clientWidth / 2, 0, sw);
                    viewport.scrollTop = clamp((fy0 + fh / 2) * sh - viewport.clientHeight / 2, 0, sh);
                    return;
                }

                // No frame: the whole photo, exactly as before. Clear any crop a prior state set.
                viewport.style.removeProperty('--crop-scale');
                viewport.style.setProperty('--fit-aspect', img.naturalWidth / img.naturalHeight);
                if (pin) {
                    viewport.style.aspectRatio = img.naturalWidth + ' / ' + img.naturalHeight;
                }
            };

            // The box has its final size here, which is the first moment the displayed pixel size of
            // the photo is known — so this is where "what does landing on this wall actually need?"
            // gets answered, before any zoom happens.
            const measure = function () {
                apply();
                if (window.bwImageRes) {
                    window.bwImageRes.refresh(viewport);
                }
            };

            if (img.complete) {
                measure();
            } else {
                img.addEventListener('load', measure, { once: true });
            }
        },

        /** Attaches the transform model over a world layer. */
        setupTransform: function (viewport, world, dotnetHelper) {
            // See setupScroll: guard against a stale ElementReference that is not a live element.
            if (!viewport || !viewport.style || !world || viewport._bwModel) {
                return;
            }

            const model = transformModel(viewport, world, dotnetHelper);
            viewport._bwModel = model;
            bindGestures(viewport, model);
        },

        // ---- C#-callable entry points. Buttons must go through these so every
        // zoom path is anchored the same way (viewport centre) instead of the
        // top-left corner you get from mutating a C# field.
        zoomBy: function (viewport, factor) {
            const m = modelOf(viewport);
            if (m) {
                m.zoomBy(factor, null, null, 0, 0);
            }
        },
        zoomTo: function (viewport, zoom) {
            const m = modelOf(viewport);
            if (m && m.zoomTo) {
                m.zoomTo(zoom, null, null, 0, 0);
            }
        },
        centerOn: function (viewport, xNorm, yNorm, zoom) {
            const m = modelOf(viewport);
            if (!m || !m.centerOn) {
                return;
            }

            // scrollHeight is only meaningful once the image has laid out; if it hasn't
            // loaded yet, re-run the centring on load so the anchor is correct either way.
            const img = viewport.querySelector('img');
            if (img && !img.complete) {
                img.addEventListener('load', function () { m.centerOn(xNorm, yNorm, zoom); }, { once: true });
            }

            m.centerOn(xNorm, yNorm, zoom);
        },
        transformGet: function (viewport) {
            const m = modelOf(viewport);
            return m && m.getState ? m.getState() : { panX: 0, panY: 0, zoom: 1 };
        },
        transformSet: function (viewport, panX, panY, zoom) {
            const m = modelOf(viewport);
            if (m && m.setState) {
                m.setState(panX, panY, zoom);
            }
        },

        // ---- Big-wall overlap stepper: suppress the browser's own scrolling on the
        // navigation keys the Blazor @onkeydown handler already drives (←/→ step, etc.)
        // WITHOUT swallowing Cmd/Ctrl/Alt shortcuts or character keys. Two listeners live
        // on the same root element: this one only preventDefaults the scroll, Blazor still
        // runs the step logic. Attach in OnAfterRenderAsync, release on dispose.
        trapStepperKeys: function (el) {
            if (!el || !el.addEventListener || el._bwStepperTrap) {
                return;
            }

            // Keys whose default action scrolls the page. Space is both ' ' and the legacy
            // 'Spacebar'; nav/paging keys round it out.
            const scrollKeys = new Set([
                'ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown',
                'PageUp', 'PageDown', 'Home', 'End', ' ', 'Spacebar',
            ]);
            const handler = function (e) {
                // Never touch modified chords (Cmd+F, Ctrl+Home, Alt+…) — those are the
                // user's / browser's, not ours.
                if (e.ctrlKey || e.metaKey || e.altKey) {
                    return;
                }

                if (scrollKeys.has(e.key)) {
                    e.preventDefault();
                }
            };
            el._bwStepperTrap = handler;
            el.addEventListener('keydown', handler);
        },
        releaseStepperKeys: function (el) {
            if (!el || !el._bwStepperTrap) {
                return;
            }

            el.removeEventListener('keydown', el._bwStepperTrap);
            el._bwStepperTrap = null;
        },

        // ---- Multi-panel viewer swipe: at (and only at) full zoom-out a decisive
        // HORIZONTAL gesture steps to the adjacent panel, gallery-style. Two input paths:
        //
        //   touch      : a one-finger swipe. The viewport already carries `touch-action: pan-y`
        //                at fit, so a sideways drag scrolls nothing and there is nothing to
        //                preventDefault; a vertical drag still scrolls the page.
        //   trackpad   : a two-finger horizontal slide arrives as wheel events with deltaX.
        //                Those DO scroll an ancestor (the wall carousel), so a clearly
        //                horizontal wheel at fit is preventDefault'ed — hence the non-passive
        //                listener — and accumulated until it passes a threshold. One continuous
        //                slide therefore advances at most one panel; the accumulator only
        //                re-arms after the wheel stream has been quiet (measured off the event
        //                timestamps, so no timers are involved).
        //
        // Above fit this does nothing at all: the gesture recogniser owns the pan exactly as
        // before. Whether a panel actually lies that way is decided on the .NET side
        // (OnSwipeNavigate -> the same GoToAsync the chevrons call), so a swipe into a dead
        // direction is simply ignored rather than falling through to the carousel.
        attachSwipe: function (viewport, dotnet) {
            if (!viewport || !viewport.addEventListener || viewport._bwSwipe) {
                return;
            }

            // Remember every node this call has ever bound, so detachSwipe can unbind a node that
            // has since been replaced by a re-render rather than only the one handed to it.
            if (!bwSwipeBound.includes(viewport)) {
                bwSwipeBound.push(viewport);
            }

            const THRESHOLD = 70;        // px of touch travel before a swipe counts
            // A flick is the shortcut for a deliberate quick sideways throw, not a licence to change
            // panel on a stray thumb. 35px at 0.5px/ms is roughly a 70ms nudge — comfortably inside
            // what a scroll or a mis-tap produces on a wall-mounted tablet, and a wrong panel change
            // costs a full photo swap to undo. Half the main threshold, at twice the speed, keeps the
            // gesture available while putting it clearly outside accidental travel.
            const FLICK_DISTANCE = 55;   // a short but fast swipe still counts...
            const FLICK_SPEED = 1.0;     // ...at this many px/ms
            const DOMINANCE = 1.6;       // one axis must beat the other by this factor
            const WHEEL_THRESHOLD = 90;  // accumulated horizontal wheel px before it counts
            const WHEEL_IDLE_MS = 200;   // quiet gap that ends one continuous trackpad gesture
            const LINE_PX = 16;          // deltaMode 1 (lines) -> approximate pixels

            let startX = 0;
            let startY = 0;
            let startTime = 0;
            let armed = false;
            let wheelDx = 0;
            let wheelFired = false;
            let wheelLast = 0;

            // "Fully zoomed out" == the scroll model's floor (1.0 is fit; it cannot go below).
            function notZoomed() {
                const m = viewport._bwModel;
                return !m || !m.getZoom || m.getZoom() <= 1.02;
            }

            // Whether a step in this direction would go anywhere. .NET publishes the answer onto the
            // viewport node on every render (see MultiPanelViewer.OnAfterRenderAsync); an absent
            // marker means "unknown", and unknown is treated as navigable so a missing hint can only
            // ever restore the old behaviour rather than disable the gesture.
            function canNavigate(dx) {
                const dir = dx < 0 ? 'right' : 'left';
                const attr = viewport.getAttribute('data-bw-swipe-' + dir);
                return attr !== 'false';
            }

            // dx < 0 means the content was dragged left, revealing the panel to the RIGHT.
            function navigate(dx) {
                try {
                    dotnet.invokeMethodAsync('OnSwipeNavigate', dx < 0 ? 'right' : 'left');
                } catch (_) { /* circuit gone */ }
            }

            function onStart(e) {
                armed = e.touches && e.touches.length === 1 && notZoomed();
                if (armed) {
                    startX = e.touches[0].clientX;
                    startY = e.touches[0].clientY;
                    startTime = e.timeStamp || Date.now();
                }
            }

            function onEnd(e) {
                if (!armed) {
                    return;
                }

                armed = false;
                if (!notZoomed()) {
                    return;
                }

                const t = e.changedTouches && e.changedTouches[0];
                if (!t) {
                    return;
                }

                const dx = t.clientX - startX;
                const dy = t.clientY - startY;
                const adx = Math.abs(dx);
                const ady = Math.abs(dy);

                // Horizontal only, and only when clearly dominant over any vertical travel, so
                // a diagonal or vertical drag stays a page scroll.
                if (adx <= ady * DOMINANCE) {
                    return;
                }

                const elapsed = Math.max(1, (e.timeStamp || Date.now()) - startTime);
                const decisive = adx >= THRESHOLD ||
                    (adx >= FLICK_DISTANCE && adx / elapsed >= FLICK_SPEED);
                if (decisive) {
                    navigate(dx);
                }
            }

            function onWheel(e) {
                // ctrl/cmd + wheel is a pinch-zoom; above fit the gesture recogniser pans.
                if (e.ctrlKey || e.metaKey || !notZoomed()) {
                    wheelDx = 0;
                    wheelFired = false;
                    return;
                }

                // shift+wheel is the browser's own "scroll horizontally" modifier, and a tilt wheel
                // sends deltaX on a mouse with no second axis to give. Neither is a trackpad panel
                // swipe, and swallowing them is what makes horizontal scrolling read as broken.
                if (e.shiftKey) {
                    return;
                }

                const factor = e.deltaMode === 1 ? LINE_PX
                    : e.deltaMode === 2 ? (viewport.clientWidth || LINE_PX)
                    : 1;
                const dx = e.deltaX * factor;
                const dy = e.deltaY * factor;
                if (Math.abs(dx) <= Math.abs(dy) * DOMINANCE) {
                    // Vertical (or ambiguous): leave it to the page so scrolling still works.
                    return;
                }

                // Only take over a gesture we actually act on. At the first/last panel in this
                // direction there is nothing to step to, so preventDefault would swallow the slide
                // and do nothing with it — which is precisely what "horizontal scrolling is broken"
                // feels like. Let it through to the carousel instead.
                if (!canNavigate(-dx)) {
                    return;
                }

                e.preventDefault();

                const now = e.timeStamp || Date.now();
                if (now - wheelLast > WHEEL_IDLE_MS) {
                    wheelDx = 0;
                    wheelFired = false;
                }

                wheelLast = now;
                if (wheelFired) {
                    // Already stepped once; the rest of this continuous slide is swallowed.
                    return;
                }

                wheelDx += dx;
                if (Math.abs(wheelDx) >= WHEEL_THRESHOLD) {
                    wheelFired = true;
                    navigate(-wheelDx);
                }
            }

            viewport._bwSwipe = { onStart: onStart, onEnd: onEnd, onWheel: onWheel };
            viewport.addEventListener('touchstart', onStart, { passive: true });
            viewport.addEventListener('touchend', onEnd, { passive: true });
            viewport.addEventListener('wheel', onWheel, { passive: false });
        },
        /**
         * One round-trip that (re)establishes everything a multi-panel viewport needs for the
         * current render: the pan/zoom model, the swipe listeners, and the "is there a panel that
         * way" hints the wheel handler consults before it takes a gesture over.
         *
         * The two setup calls have to re-run on every render anyway — a node replaced by a
         * re-render or a reconnect would otherwise come back inert — so folding them together is
         * what keeps that from costing two interop calls a render instead of one. Both halves stay
         * idempotent in JS, so a repeat call on an already-bound node does nothing.
         */
        setupPanelViewport: function (viewport, dotnet, canLeft, canRight) {
            if (!viewport || !viewport.setAttribute) {
                return;
            }

            this.setupScroll(viewport, null);
            if (dotnet) {
                this.attachSwipe(viewport, dotnet);
            }

            viewport.setAttribute('data-bw-swipe-left', canLeft ? 'true' : 'false');
            viewport.setAttribute('data-bw-swipe-right', canRight ? 'true' : 'false');
        },

        detachSwipe: function (viewport) {
            // Unbind the node asked for AND any earlier node this page bound and then discarded: a
            // re-render can replace the viewport element, and unbinding only the current ref left
            // the old node holding listeners (and, through them, a DotNetObjectReference) alive.
            const targets = bwSwipeBound.slice();
            if (viewport && targets.indexOf(viewport) === -1) {
                targets.push(viewport);
            }

            bwSwipeBound.length = 0;

            targets.forEach(function (node) {
                const s = node && node._bwSwipe;
                if (!s) {
                    return;
                }

                node.removeEventListener('touchstart', s.onStart);
                node.removeEventListener('touchend', s.onEnd);
                node.removeEventListener('wheel', s.onWheel);
                node._bwSwipe = null;
            });
        },

        /**
         * Warms the browser cache for an image and resolves once it is decodable (or has
         * failed). The multi-panel viewer awaits this before swapping panel, so the new
         * photo and its hold overlay land in the SAME render instead of the overlay
         * trailing the image. Resolves on error too, so a missing panel photo can never
         * wedge navigation.
         *
         * A STALLED request is the case `error` does not cover: a captive portal or a
         * half-open connection leaves the request pending with no event ever firing, and
         * the caller's `_swapping` latch would hold every chevron and swipe until Blazor's
         * own ~1-minute interop timeout expired. So the promise also resolves on a timeout
         * (default 4s) and the viewer simply shows the panel with a cold image — a photo
         * that pops in a frame late is a far better outcome than a viewer that ignores
         * input for a minute.
         */
        preloadImage: function (url, timeoutMs) {
            return new Promise(function (resolve) {
                if (!url) {
                    resolve(false);
                    return;
                }

                let settled = false;
                function finish(ok) {
                    if (settled) {
                        return;
                    }
                    settled = true;
                    clearTimeout(timer);
                    resolve(ok);
                }

                const timer = setTimeout(function () { finish(false); },
                    typeof timeoutMs === 'number' && timeoutMs > 0 ? timeoutMs : 4000);

                const img = new Image();
                img.onload = function () { finish(true); };
                img.onerror = function () { finish(false); };
                img.src = url;
                if (img.complete) {
                    finish(true);
                }
            });
        },

        /** Page-wide: stop the browser's own zoom outside our viewports. */
        blockPageZoom: function () {
            if (window._bwPageZoomBlocked) {
                return;
            }

            window._bwPageZoomBlocked = true;
            window.addEventListener('wheel', function (e) {
                if (e.ctrlKey || e.metaKey) {
                    e.preventDefault();
                }
            }, { passive: false });

            ['gesturestart', 'gesturechange', 'gestureend'].forEach(function (ev) {
                window.addEventListener(ev, function (e) { e.preventDefault(); }, { passive: false });
            });
        },
    };
})();
