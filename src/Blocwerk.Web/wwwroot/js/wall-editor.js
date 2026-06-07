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

        let isPanning = false;
        let startX, startY, scrollLeft, scrollTop;

        // Mouse pan (when pan mode or zoomed)
        viewport.addEventListener('mousedown', function (e) {
            if (!viewport.dataset.panMode) return;
            isPanning = true;
            startX = e.clientX;
            startY = e.clientY;
            scrollLeft = viewport.scrollLeft;
            scrollTop = viewport.scrollTop;
            viewport.style.cursor = 'grabbing';
            e.preventDefault();
        });

        viewport.addEventListener('mousemove', function (e) {
            if (!isPanning) return;
            viewport.scrollLeft = scrollLeft - (e.clientX - startX);
            viewport.scrollTop = scrollTop - (e.clientY - startY);
        });

        viewport.addEventListener('mouseup', function () {
            isPanning = false;
            viewport.style.cursor = viewport.dataset.panMode ? 'grab' : '';
        });

        viewport.addEventListener('mouseleave', function () {
            isPanning = false;
            viewport.style.cursor = viewport.dataset.panMode ? 'grab' : '';
        });

        // Touch: single finger pan, two finger pinch-to-zoom
        let touchStartDist = 0;
        let touchStartZoom = 1;
        let singleTouchPan = false;
        let touchStartX, touchStartY, touchScrollLeft, touchScrollTop;

        viewport.addEventListener('touchstart', function (e) {
            if (e.touches.length === 2) {
                e.preventDefault();
                touchStartDist = getTouchDistance(e.touches);
                touchStartZoom = parseFloat(viewport.dataset.currentZoom || '1');
            } else if (e.touches.length === 1) {
                const isZoomed = parseFloat(viewport.dataset.currentZoom || '1') > 1;
                if (viewport.dataset.panMode || isZoomed) {
                    singleTouchPan = true;
                    touchStartX = e.touches[0].clientX;
                    touchStartY = e.touches[0].clientY;
                    touchScrollLeft = viewport.scrollLeft;
                    touchScrollTop = viewport.scrollTop;
                }
            }
        }, { passive: false });

        viewport.addEventListener('touchmove', function (e) {
            if (e.touches.length === 2 && touchStartDist > 0) {
                e.preventDefault();
                const dist = getTouchDistance(e.touches);
                const scale = dist / touchStartDist;
                let newZoom = Math.round(touchStartZoom * scale * 4) / 4; // snap to 0.25
                newZoom = Math.max(0.5, Math.min(4, newZoom));
                if (dotnetHelper && newZoom !== parseFloat(viewport.dataset.currentZoom || '1')) {
                    viewport.dataset.currentZoom = newZoom;
                    dotnetHelper.invokeMethodAsync('SetZoomFromJs', newZoom);
                }
            } else if (singleTouchPan && e.touches.length === 1) {
                e.preventDefault();
                viewport.scrollLeft = touchScrollLeft - (e.touches[0].clientX - touchStartX);
                viewport.scrollTop = touchScrollTop - (e.touches[0].clientY - touchStartY);
            }
        }, { passive: false });

        viewport.addEventListener('touchend', function (e) {
            if (e.touches.length < 2) {
                touchStartDist = 0;
            }
            if (e.touches.length === 0) {
                singleTouchPan = false;
            }
        });

        function getTouchDistance(touches) {
            const dx = touches[0].clientX - touches[1].clientX;
            const dy = touches[0].clientY - touches[1].clientY;
            return Math.sqrt(dx * dx + dy * dy);
        }
    },

    // Legacy alias
    setupPan: function (viewport, dotnetHelper) {
        this.setupViewport(viewport, dotnetHelper);
    }
};
