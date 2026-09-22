/*
 * Wall editor helpers. Pan/zoom itself lives in the shared engine (viewport.js);
 * everything here is thin glue so the Razor call sites stay unchanged.
 */
window.wallEditor = {
    getRelativePosition: function (element, clientX, clientY) {
        const rect = element.getBoundingClientRect();

        // The full-screen landscape takeover (wwwroot/js/fullscreen-stage.js) may draw this element
        // inside a stage turned a quarter, and getBoundingClientRect() then reports the axis-aligned
        // BBOX of that rotated box: its width is the content's HEIGHT and vice versa, and the
        // content's own origin sits at the bbox's TOP-RIGHT corner. Under a 90deg clockwise turn a
        // content point (sx, sy) lands at screen (right - sy, top + sx), so inverting it reads the
        // client Y along the content's x axis and the client X backwards along its y axis.
        // Only that stage ever carries data-bw-rot, so every other caller keeps the identity mapping
        // below, unchanged.
        const turned = element.closest && element.closest('[data-bw-rot="90"]');
        if (turned) {
            return {
                x: (clientY - rect.top) / rect.height,
                y: (rect.right - clientX) / rect.width
            };
        }

        return {
            x: (clientX - rect.left) / rect.width,
            y: (clientY - rect.top) / rect.height
        };
    },

    /** Attaches the shared scroll-model viewport engine. */
    setupViewport: function (viewport, dotnetHelper) {
        window.bwViewport.setupScroll(viewport, dotnetHelper);
    },

    // Legacy alias
    setupPan: function (viewport, dotnetHelper) {
        window.bwViewport.setupScroll(viewport, dotnetHelper);
    },

    blockPageZoom: function () {
        window.bwViewport.blockPageZoom();
    },
};
