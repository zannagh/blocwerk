/*
 * The pan/zoom model behind the full-screen landscape takeover, plus the rotation adapter that
 * lets the shared gesture recogniser drive it through a quarter turn. See fullscreen-stage.js
 * for the surface itself and for why the rotation lives where it does.
 *
 * A plain global (window.bwStageModel) rather than an ES module, like every other viewport helper
 * in this app: the app shell registers both stage files through @Assets[] so they are fingerprinted
 * with the rest of the bundle. See BlocwerkApp.razor.
 */
window.bwStageModel = (function () {
    'use strict';

    // 1 is FIT, because fit() sizes the world to the stage and leaves zoom at 1. Below it the
    // photo would shrink to a speck inside a black screen with nothing gained, so fit is the floor
    // — the same rule the ordinary photo surfaces use (SCROLL_ZOOM_MIN in viewport.js).
    const ZOOM_MIN = 1;
    const ZOOM_MAX = 12;

    function clamp(value, lo, hi) {
        return value < lo ? lo : (value > hi ? hi : value);
    }

    /**
     * Pan/zoom over the world layer, in STAGE-LOCAL pixels. Deliberately a local model rather than
     * bwViewport.setupTransform: that helper binds the gestures itself, with the raw model, and there
     * is no seam to slip the rotation adapter into without either editing viewport.js or binding the
     * recogniser twice. The maths is the same `translate(pan) scale(zoom)` transform.
     */
    function stageModel(world, noPanSelector) {
        let zoom = 1;
        let panX = 0;
        let panY = 0;
        let dead = false;
        // The stage box and the fit-sized world box, both set by reset() (i.e. by fit()).
        let stageW = 0;
        let stageH = 0;
        let worldW = 0;
        let worldH = 0;

        /*
         * Keeps the content on screen, the way a native scroller does and for the same reason: a
         * transform model has no scrollWidth to clamp against, so without this a two-finger scroll
         * or a drag walks the photo out of the stage entirely and nothing brings it back. Larger
         * than the stage: the edges may not come inside it. Smaller (only ever at fit): centred.
         */
        function clampPan() {
            if (worldW < 1 || worldH < 1) {
                return;
            }

            const cw = worldW * zoom;
            const ch = worldH * zoom;
            panX = cw <= stageW ? (stageW - cw) / 2 : clamp(panX, stageW - cw, 0);
            panY = ch <= stageH ? (stageH - ch) / 2 : clamp(panY, stageH - ch, 0);
        }

        function apply() {
            world.style.transform = 'translate(' + panX + 'px, ' + panY + 'px) scale(' + zoom + ')';

            // Zooming in past the rendition on screen should unlock a sharper one, exactly as it does
            // on the ordinary photo surfaces. The helper debounces to one measurement per frame and
            // never downgrades, so calling it from every transform is safe.
            if (window.bwImageRes) {
                window.bwImageRes.refresh(world, zoom);
            }
        }

        return {
            kill: function () {
                dead = true;
            },
            // Re-seat at fit: zoom 1 with the world (w x h) sized to the stage (sw x sh), centred.
            // Also the only place the pan bounds are learned.
            reset: function (sw, sh, w, h) {
                stageW = sw;
                stageH = sh;
                worldW = w;
                worldH = h;
                zoom = 1;
                panX = 0;
                panY = 0;
                clampPan();
                apply();
            },
            // A full-screen takeover always owns the gesture; there is nothing behind it to scroll.
            // Panning at fit is a no-op rather than a page scroll, because clampPan() pins it.
            capturesPan: function () {
                return true;
            },
            canPanFrom: function (target) {
                return !(target && target.closest && target.closest(noPanSelector));
            },
            panBy: function (dx, dy) {
                if (dead) {
                    return;
                }

                panX += dx;
                panY += dy;
                clampPan();
                apply();
            },
            // (sx, sy) are STAGE-LOCAL already — the adapter is the only caller and maps them.
            zoomBy: function (factor, sx, sy, panDx, panDy) {
                if (dead) {
                    return;
                }

                const worldX = (sx - panX) / zoom;
                const worldY = (sy - panY) / zoom;
                zoom = clamp(zoom * factor, ZOOM_MIN, ZOOM_MAX);
                panX = sx + (panDx || 0) - (worldX * zoom);
                panY = sy + (panDy || 0) - (worldY * zoom);
                clampPan();
                apply();
            },
            // Double tap toggles between fit (zoom 1, where the world was sized to the stage) and 2x.
            doubleTap: function (sx, sy) {
                this.zoomBy(zoom > 1.05 ? 1 / zoom : 2, sx, sy, 0, 0);
            },
        };
    }

    /**
     * Maps the recogniser's screen-space intents into the stage's rotated frame. At rotation 0 every
     * call is passed straight through bar the rect subtraction, so the identity case costs nothing.
     *
     * For the quarter turn the stage is drawn `translate(vw, 0) rotate(90deg)`, so a stage-local point
     * (sx, sy) lands at screen offset (vw - sy, sx). Inverting that: sx = oy, sy = vw - ox. Deltas
     * transpose and negate the same way, which is why panBy's (dx, dy) becomes (dy, -dx).
     */
    function rotationAdapter(model, viewport, getRotation) {
        function toLocal(cx, cy) {
            const rect = viewport.getBoundingClientRect();
            const ox = cx - rect.left;
            const oy = cy - rect.top;
            if (getRotation() === 90) {
                return { x: oy, y: rect.width - ox };
            }

            return { x: ox, y: oy };
        }

        return {
            capturesPan: function () {
                return model.capturesPan();
            },
            canPanFrom: function (target) {
                return model.canPanFrom(target);
            },
            panBy: function (dx, dy) {
                if (getRotation() === 90) {
                    model.panBy(dy, -dx);
                } else {
                    model.panBy(dx, dy);
                }
            },
            zoomBy: function (factor, cx, cy, panDx, panDy) {
                const p = toLocal(cx, cy);
                if (getRotation() === 90) {
                    model.zoomBy(factor, p.x, p.y, panDy || 0, -(panDx || 0));
                } else {
                    model.zoomBy(factor, p.x, p.y, panDx || 0, panDy || 0);
                }
            },
            toggleDoubleTapZoom: function (cx, cy) {
                const p = toLocal(cx, cy);
                model.doubleTap(p.x, p.y);
            },
        };
    }

    return {
        stageModel: stageModel,
        rotationAdapter: rotationAdapter,
    };
})();
