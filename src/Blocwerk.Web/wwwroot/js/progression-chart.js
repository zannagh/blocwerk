/*
 * blocwerkChart — pointer-drag read-out + y-axis drag-to-rescale for the progression line charts
 * (ProgressionChart.razor).
 *
 * The tooltip is driven entirely in JS (a Blazor Server pointermove round-trip per move would lag):
 * the component hands us the points once (x/y fractions + a preformatted label) and this module maps
 * the pointer to the nearest point. The y-axis drag reports coarse steps back to .NET (throttled),
 * which is fine because each step re-renders the chart at a new scale.
 */
window.blocwerkChart = (function () {
    'use strict';

    var Y_STEP_PX = 26;

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

    function bindTooltip(el, data) {
        removeTooltip(el);

        var guide = el.querySelector('.prog-chart-guide');
        var dot = el.querySelector('.prog-chart-dot');
        var tip = el.querySelector('.prog-chart-tip');
        var svg = el.querySelector('.prog-chart-svg');
        if (!guide || !tip || !dot || !svg) {
            return;
        }

        function show(clientX) {
            // Map against the SVG's box, not the padded container's, so the guide/dot line up with
            // the plotted line regardless of the y-axis gutter.
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

            // In local-time mode each point carries its UTC instant (point.utc, epoch ms) and a
            // bwTime format key (point.tfmt); the label is the prefix and the viewer-local time is
            // appended here, so the read-out is in the browser's timezone rather than the server's.
            // The " · " separator lives here (not in the C# prefix) so an unavailable/empty bwTime
            // leaves no dangling separator behind.
            var text = point.label;
            if (point.utc != null) {
                var t = window.bwTime && typeof window.bwTime.formatUtc === 'function'
                    ? window.bwTime.formatUtc(point.utc, point.tfmt)
                    : '';
                text += (t ? ' · ' + t : '');
            }

            tip.textContent = text;
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
        el._bwChartTip = handlers;
    }

    function removeTooltip(el) {
        var h = el._bwChartTip;
        if (!h) {
            return;
        }

        el.removeEventListener('pointermove', h.move);
        el.removeEventListener('pointerdown', h.down);
        el.removeEventListener('pointerleave', h.leave);
        el.removeEventListener('pointercancel', h.cancel);
        delete el._bwChartTip;
    }

    // Wired ONCE and kept across tooltip rebinds, so an in-progress drag isn't torn down when the
    // chart re-renders at the new scale.
    function bindYDrag(el, dotnet) {
        var yaxis = el.querySelector('.prog-chart-yaxis');
        if (!yaxis || el._bwChartY) {
            return;
        }

        yaxis.style.pointerEvents = 'auto';
        yaxis.style.cursor = 'ns-resize';
        yaxis.style.touchAction = 'none';

        var dragging = false;
        var baseY = 0;
        var acc = 0;

        var h = {
            down: function (e) {
                dragging = true;
                baseY = e.clientY;
                acc = 0;
                if (yaxis.setPointerCapture) {
                    yaxis.setPointerCapture(e.pointerId);
                }
                e.preventDefault();
                e.stopPropagation();
            },
            move: function (e) {
                if (!dragging) {
                    return;
                }

                e.preventDefault();
                e.stopPropagation();
                var dy = e.clientY - baseY;
                // Emit one step per Y_STEP_PX travelled. Down (dy > 0) widens the range (+1).
                while (Math.abs(dy - acc) >= Y_STEP_PX) {
                    var dir = (dy - acc) > 0 ? 1 : -1;
                    acc += dir * Y_STEP_PX;
                    try {
                        dotnet.invokeMethodAsync('YScale', dir);
                    } catch (_) { /* circuit gone */ }
                }
            },
            up: function (e) {
                dragging = false;
                e.stopPropagation();
            },
        };

        yaxis.addEventListener('pointerdown', h.down);
        yaxis.addEventListener('pointermove', h.move);
        yaxis.addEventListener('pointerup', h.up);
        yaxis.addEventListener('pointercancel', h.up);
        el._bwChartY = { yaxis: yaxis, h: h };
    }

    function bind(el, payload, dotnet) {
        if (!el || typeof el.querySelector !== 'function') {
            return;
        }

        var data = typeof payload === 'string' ? JSON.parse(payload) : payload;
        if (!data || !data.length) {
            return;
        }

        bindTooltip(el, data);

        // Local-time charts render their x-axis ticks as <time> nodes for bwTime to localize. bind()
        // runs on every re-render (OnAfterRenderAsync), so re-localize the axis here too — this covers
        // interactive re-renders that update existing tick nodes in place, which the MutationObserver
        // (added-nodes only) would miss.
        if (window.bwTime && typeof window.bwTime.localizeAll === 'function') {
            window.bwTime.localizeAll(el.parentNode || el);
        }

        if (dotnet) {
            bindYDrag(el, dotnet);
        }
    }

    function unbind(el) {
        if (!el) {
            return;
        }

        removeTooltip(el);
        if (el._bwChartY) {
            var y = el._bwChartY;
            y.yaxis.removeEventListener('pointerdown', y.h.down);
            y.yaxis.removeEventListener('pointermove', y.h.move);
            y.yaxis.removeEventListener('pointerup', y.h.up);
            y.yaxis.removeEventListener('pointercancel', y.h.up);
            delete el._bwChartY;
        }
    }

    return { bind: bind, unbind: unbind };
})();
