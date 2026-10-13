/*
 * Placement of the boulder designer's round role menu (HoldClassifyOverlay) next to the hold that was
 * tapped, so the hold and its outline stay in view instead of disappearing under the menu.
 */
(function () {
    'use strict';

    const GAP = 14; // between the hold and the menu

    window.bwHoldDial = {
        /**
         * Where the menu's centre goes, in viewport pixels: under the hold when there is room, else
         * above, right or left of it; with no room anywhere, the side with the most. `container` is
         * the picker layer holding the tapped hold's [data-hold] shape; without it, the tap point.
         */
        spot: function (container, holdId, hasRing, tapX, tapY) {
            const hold = container && container.querySelector('[data-hold="' + holdId + '"]');
            if (!hold) {
                return { x: tapX, y: tapY };
            }

            const vw = window.innerWidth;
            const vh = window.innerHeight;
            const size = Math.min(260, Math.max(180, 0.46 * vw)) + (hasRing ? 76 : 0); // .classify-root
            const half = size / 2;
            const r = hold.getBoundingClientRect();
            const cx = (r.left + r.right) / 2;
            const cy = (r.top + r.bottom) / 2;
            const spots = [
                { x: cx, y: r.bottom + GAP + half, room: vh - r.bottom },
                { x: cx, y: r.top - GAP - half, room: r.top },
                { x: r.right + GAP + half, y: cy, room: vw - r.right },
                { x: r.left - GAP - half, y: cy, room: r.left },
            ];
            const fits = spots.find(function (s) { return s.room >= size + GAP; });
            const best = fits || spots.slice().sort(function (a, b) { return b.room - a.room; })[0];
            return { x: best.x, y: best.y };
        },
    };
})();
