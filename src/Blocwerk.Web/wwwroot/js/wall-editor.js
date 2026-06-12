window.wallEditor = {
    getRelativePosition: function (element, clientX, clientY) {
        const rect = element.getBoundingClientRect();
        return {
            x: (clientX - rect.left) / rect.width,
            y: (clientY - rect.top) / rect.height
        };
    },

    setupViewport: function (viewport, dotnetHelper) {
        if (!viewport || viewport._viewportSetup) return;
        viewport._viewportSetup = true;

        const ZOOM_MIN = 0.5;
        const ZOOM_MAX = 6;
        const ZOOM_STEP = 0.1;

        function currentZoom() {
            return parseFloat(viewport.dataset.currentZoom || '1');
        }

        function setZoom(newZoom, centerClientX, centerClientY) {
            newZoom = Math.max(ZOOM_MIN, Math.min(ZOOM_MAX, newZoom));
            const prevZoom = currentZoom();
            if (Math.abs(newZoom - prevZoom) < 0.001) return;

            const rect = viewport.getBoundingClientRect();
            const cx = (centerClientX ?? rect.left + rect.width / 2) - rect.left;
            const cy = (centerClientY ?? rect.top + rect.height / 2) - rect.top;

            const contentX = (viewport.scrollLeft + cx) / prevZoom;
            const contentY = (viewport.scrollTop + cy) / prevZoom;

            viewport.dataset.currentZoom = newZoom;
            if (dotnetHelper) {
                dotnetHelper.invokeMethodAsync('SetZoomFromJs', newZoom);
            }

            // Defer scroll adjust so the DOM picks up the new size first.
            requestAnimationFrame(function () {
                viewport.scrollLeft = contentX * newZoom - cx;
                viewport.scrollTop = contentY * newZoom - cy;
            });
        }

        // Wheel: trackpad pinch (ctrl+wheel) and Ctrl/Cmd+wheel = zoom; plain wheel = pan.
        viewport.addEventListener('wheel', function (e) {
            e.preventDefault();
            if (e.ctrlKey || e.metaKey) {
                const factor = Math.exp(-e.deltaY * 0.01);
                setZoom(currentZoom() * factor, e.clientX, e.clientY);
            } else {
                viewport.scrollLeft += e.deltaX;
                viewport.scrollTop += e.deltaY;
            }
        }, { passive: false });

        // Mouse drag = pan (any button, when not on an interactive element handled by Blazor).
        let isMousePanning = false;
        let mouseStartX, mouseStartY, mouseStartScrollLeft, mouseStartScrollTop;

        viewport.addEventListener('mousedown', function (e) {
            // Only pan on middle-click or when no svg element underneath consumes the event;
            // svg holds/border stop propagation, so reaching here means empty area.
            if (e.button !== 0 && e.button !== 1) return;
            if (viewport.dataset.gestureLock === 'true') return;
            isMousePanning = true;
            mouseStartX = e.clientX;
            mouseStartY = e.clientY;
            mouseStartScrollLeft = viewport.scrollLeft;
            mouseStartScrollTop = viewport.scrollTop;
        });

        window.addEventListener('mousemove', function (e) {
            if (!isMousePanning) return;
            const dx = e.clientX - mouseStartX;
            const dy = e.clientY - mouseStartY;
            if (Math.abs(dx) + Math.abs(dy) > 3) {
                viewport.scrollLeft = mouseStartScrollLeft - dx;
                viewport.scrollTop = mouseStartScrollTop - dy;
                viewport.dataset.panActive = 'true';
            }
        });

        window.addEventListener('mouseup', function () {
            isMousePanning = false;
            setTimeout(function () { delete viewport.dataset.panActive; }, 0);
        });

        // Touch: 1-finger drag = pan, 2-finger = pinch zoom + pan.
        let touchMode = null; // 'pan' | 'pinch'
        let touchStartDist = 0;
        let touchStartZoom = 1;
        let touchStartX, touchStartY, touchStartScrollLeft, touchStartScrollTop;
        let touchPinchCenterX, touchPinchCenterY;

        function getTouchDistance(touches) {
            const dx = touches[0].clientX - touches[1].clientX;
            const dy = touches[0].clientY - touches[1].clientY;
            return Math.sqrt(dx * dx + dy * dy);
        }

        function getTouchCenter(touches) {
            return {
                x: (touches[0].clientX + touches[1].clientX) / 2,
                y: (touches[0].clientY + touches[1].clientY) / 2,
            };
        }

        viewport.addEventListener('touchstart', function (e) {
            if (e.touches.length === 2) {
                e.preventDefault();
                touchMode = 'pinch';
                touchStartDist = getTouchDistance(e.touches);
                touchStartZoom = currentZoom();
                const c = getTouchCenter(e.touches);
                touchPinchCenterX = c.x;
                touchPinchCenterY = c.y;
                touchStartScrollLeft = viewport.scrollLeft;
                touchStartScrollTop = viewport.scrollTop;
            } else if (e.touches.length === 1) {
                touchMode = 'pan';
                touchStartX = e.touches[0].clientX;
                touchStartY = e.touches[0].clientY;
                touchStartScrollLeft = viewport.scrollLeft;
                touchStartScrollTop = viewport.scrollTop;
            }
        }, { passive: false });

        viewport.addEventListener('touchmove', function (e) {
            if (touchMode === 'pinch' && e.touches.length === 2) {
                e.preventDefault();
                const dist = getTouchDistance(e.touches);
                const c = getTouchCenter(e.touches);
                const scale = dist / Math.max(touchStartDist, 1);
                setZoom(touchStartZoom * scale, c.x, c.y);
                viewport.scrollLeft = touchStartScrollLeft - (c.x - touchPinchCenterX);
                viewport.scrollTop = touchStartScrollTop - (c.y - touchPinchCenterY);
                viewport.dataset.panActive = 'true';
            } else if (touchMode === 'pan' && e.touches.length === 1) {
                const dx = e.touches[0].clientX - touchStartX;
                const dy = e.touches[0].clientY - touchStartY;
                if (Math.abs(dx) + Math.abs(dy) > 4) {
                    e.preventDefault();
                    viewport.scrollLeft = touchStartScrollLeft - dx;
                    viewport.scrollTop = touchStartScrollTop - dy;
                    viewport.dataset.panActive = 'true';
                }
            }
        }, { passive: false });

        viewport.addEventListener('touchend', function (e) {
            if (e.touches.length === 0) {
                touchMode = null;
                setTimeout(function () { delete viewport.dataset.panActive; }, 0);
            } else if (e.touches.length === 1 && touchMode === 'pinch') {
                touchMode = 'pan';
                touchStartX = e.touches[0].clientX;
                touchStartY = e.touches[0].clientY;
                touchStartScrollLeft = viewport.scrollLeft;
                touchStartScrollTop = viewport.scrollTop;
            }
        });
    },

    // Legacy alias
    setupPan: function (viewport, dotnetHelper) {
        this.setupViewport(viewport, dotnetHelper);
    },

    // Page-wide: prevent browser zoom (ctrl+wheel and pinch gestures outside viewport).
    blockPageZoom: function () {
        if (window._wallEditorPageZoomBlocked) return;
        window._wallEditorPageZoomBlocked = true;

        window.addEventListener('wheel', function (e) {
            if (e.ctrlKey || e.metaKey) {
                e.preventDefault();
            }
        }, { passive: false });

        // Safari macOS / iOS pinch gesture events
        ['gesturestart', 'gesturechange', 'gestureend'].forEach(function (ev) {
            window.addEventListener(ev, function (e) { e.preventDefault(); }, { passive: false });
        });
    },
};
