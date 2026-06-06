window.wallEditor = {
    getRelativePosition: function (element, clientX, clientY) {
        const rect = element.getBoundingClientRect();
        return {
            x: (clientX - rect.left) / rect.width,
            y: (clientY - rect.top) / rect.height
        };
    }
};
