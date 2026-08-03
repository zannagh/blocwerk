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
         */
        fitBox: function (viewport) {
            // Guard a stale/non-element reference the same way setupScroll does.
            if (!viewport || typeof viewport.querySelector !== 'function') {
                return;
            }

            const img = viewport.querySelector('img');
            if (!img) {
                return;
            }

            const apply = function () {
                if (img.naturalWidth > 0 && img.naturalHeight > 0) {
                    viewport.style.aspectRatio = img.naturalWidth + ' / ' + img.naturalHeight;
                }
            };

            if (img.complete) {
                apply();
            } else {
                img.addEventListener('load', apply, { once: true });
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
