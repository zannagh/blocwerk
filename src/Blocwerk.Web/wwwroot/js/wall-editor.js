window.wallEditor = {
    getRelativePosition: function (element, clientX, clientY) {
        const rect = element.getBoundingClientRect();
        return {
            x: (clientX - rect.left) / rect.width,
            y: (clientY - rect.top) / rect.height
        };
    },
    scrollTo: function (viewport, deltaX, deltaY) {
        if (viewport) {
            viewport.scrollLeft += deltaX;
            viewport.scrollTop += deltaY;
        }
    },
    setupPan: function (viewport, dotnetHelper) {
        if (!viewport || viewport._panSetup) return;
        viewport._panSetup = true;

        let isPanning = false;
        let startX, startY, scrollLeft, scrollTop;

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
    }
};
