/*
 * blocwerkChart — pointer-drag read-out for the progression line charts (ProgressionChart.razor).
 *
 * The tooltip is driven entirely in JS: on a Blazor Server circuit a pointermove round-trip per
 * move would lag badly, so the component hands us the points once (x/y fractions + a preformatted
 * label) and this module maps the pointer to the nearest point and positions the tooltip, a vertical
 * guide and a dot — all synchronously. Mirrors the "JS owns synchronous interaction" rule in viewport.js.
 */
window.blocwerkChart = (function () {
    'use strict';

    function nearestIndex(points, frac) {
        var best = 0;
        var bestDist = Infinity;
        for (var i = 0; i < points.length; i++) {
            var d = Math.abs(points[i].x - frac);
            if (d < bestDist) {
                bestDist = d;
                best = i;
            }
        }
        return best;
    }

    function bind(el, payload) {
        if (!el || typeof el.querySelector !== 'function') {
            return;
        }

        // Re-bind cleanly when the series changes.
        unbind(el);

        var data = typeof payload === 'string' ? JSON.parse(payload) : payload;
        if (!data || !data.length) {
            return;
        }

        var guide = el.querySelector('.prog-chart-guide');
        var dot = el.querySelector('.prog-chart-dot');
        var tip = el.querySelector('.prog-chart-tip');
        var svg = el.querySelector('.prog-chart-svg');
        if (!guide || !tip || !dot || !svg) {
            return;
        }

        function show(clientX) {
            // Map against the SVG's box, not the padded container's, so the guide/dot line up with
            // the plotted line regardless of the y-axis gutter. Overlays are positioned relative to
            // the container, hence the offset.
            var cRect = el.getBoundingClientRect();
            var sRect = svg.getBoundingClientRect();
            if (sRect.width <= 0) {
                return;
            }

            var frac = Math.max(0, Math.min(1, (clientX - sRect.left) / sRect.width));
            var point = data[nearestIndex(data, frac)];
            var px = (sRect.left - cRect.left) + (point.x * sRect.width);

            guide.style.left = px + 'px';
            guide.style.display = 'block';

            tip.textContent = point.label;
            tip.style.display = 'block';
            var tx = Math.max(0, Math.min(cRect.width - tip.offsetWidth, px - (tip.offsetWidth / 2)));
            tip.style.left = tx + 'px';

            if (point.y == null) {
                dot.style.display = 'none';
            } else {
                dot.style.display = 'block';
                dot.style.left = px + 'px';
                dot.style.top = ((sRect.top - cRect.top) + (point.y * sRect.height)) + 'px';
            }
        }

        function hide() {
            guide.style.display = 'none';
            tip.style.display = 'none';
            dot.style.display = 'none';
        }

        var handlers = {
            move: function (e) { show(e.clientX); },
            down: function (e) { show(e.clientX); },
            leave: hide,
            cancel: hide,
        };

        el.addEventListener('pointermove', handlers.move);
        el.addEventListener('pointerdown', handlers.down);
        el.addEventListener('pointerleave', handlers.leave);
        el.addEventListener('pointercancel', handlers.cancel);
        el._bwChart = handlers;
    }

    function unbind(el) {
        if (!el || !el._bwChart) {
            return;
        }

        var h = el._bwChart;
        el.removeEventListener('pointermove', h.move);
        el.removeEventListener('pointerdown', h.down);
        el.removeEventListener('pointerleave', h.leave);
        el.removeEventListener('pointercancel', h.cancel);
        delete el._bwChart;
    }

    return { bind: bind, unbind: unbind };
})();
