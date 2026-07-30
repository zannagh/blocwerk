/*
 * Wall editor helpers. Pan/zoom itself lives in the shared engine (viewport.js);
 * everything here is thin glue so the Razor call sites stay unchanged.
 */
window.wallEditor = {
    getRelativePosition: function (element, clientX, clientY) {
        const rect = element.getBoundingClientRect();
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
