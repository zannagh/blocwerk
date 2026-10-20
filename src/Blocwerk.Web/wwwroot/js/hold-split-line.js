// The wall editor's split tool (WallPhotoEditor.MergeSplit.cs): draws the cut across the selected hold with a mouse,
// a finger or a pen, and previews the two parts LIVE in the browser, so no pointer move costs a server round-trip.
// Only the finished stroke goes to .NET (OnSplitStroke), once, where the server cuts the hold for real
// (HoldOutlineBoolean.Split) with the same walk as below.
//
// While armed:
// - the overlay svg gets touch-action: none, so a finger or Apple Pencil draws instead of scrolling the page;
// - a ONE-finger touch and a mouse press stop at the svg, so the viewport's pan recogniser (viewport-gestures.js) and
//   the hold drag (wall-hold-drag.js) never see them; a second finger cancels the stroke and is let through to pinch;
// - the click that ends a stroke is swallowed, so it does not count as a tap on a hold.
// Everything is drawn into <g data-split-layer>, which Blazor renders EMPTY and never fills itself.

const SVG_NS = 'http://www.w3.org/2000/svg';
const MIN_STEP = 0.15;
const states = new WeakMap();

/** Arms the overlay svg inside the editor's .photo-editor for drawing a cut across the outline (viewBox units). */
export function arm(container, dotnet, outline) {
    const svg = overlayOf(container);
    disarm(container);
    if (!svg) {
        return;
    }

    const state = { svg, dotnet, outline: outline || [], points: null, pointerId: null, swallowClick: false, handlers: {} };
    state.handlers = {
        pointerdown: (e) => onDown(state, e),
        pointermove: (e) => onMove(state, e),
        pointerup: (e) => onUp(state, e),
        pointercancel: () => cancelStroke(state),
        touchstart: (e) => onTouch(state, e),
        touchmove: (e) => onTouch(state, e),
        mousedown: (e) => e.stopPropagation(),
        click: (e) => onClick(state, e),
    };
    for (const [name, fn] of Object.entries(state.handlers)) {
        const passive = name === 'touchstart' || name === 'touchmove' ? { passive: false } : undefined;
        svg.addEventListener(name, fn, name === 'click' ? true : passive);
    }

    state.previousTouchAction = svg.style.touchAction;
    svg.style.touchAction = 'none';
    states.set(svg, state);
}

export function disarm(container) {
    const svg = overlayOf(container);
    const state = svg ? states.get(svg) : null;
    if (!state) {
        return;
    }

    for (const [name, fn] of Object.entries(state.handlers)) {
        svg.removeEventListener(name, fn, name === 'click' ? true : undefined);
    }

    svg.style.touchAction = state.previousTouchAction || '';
    clearLayer(svg);
    states.delete(svg);
}

/** Drops the drawn stroke and its preview (a refused cut, a cancel). */
export function clear(container) {
    clearLayer(overlayOf(container));
}

function overlayOf(container) {
    return container && container.querySelector ? container.querySelector('svg.hold-overlay') : null;
}

function onTouch(state, e) {
    if (e.touches.length === 1) {
        // One finger draws: keep it from the viewport's pan.
        e.stopPropagation();
        if (e.cancelable) {
            e.preventDefault();
        }
    } else {
        cancelStroke(state);
    }
}

function onDown(state, e) {
    if (!e.isPrimary || (e.pointerType === 'mouse' && e.button !== 0)) {
        return;
    }

    e.stopPropagation();
    state.pointerId = e.pointerId;
    state.points = [toViewBox(state.svg, e)];
    try {
        state.svg.setPointerCapture(e.pointerId);
    } catch (_) { /* a pointer that is already gone */ }
    draw(state);
}

function onMove(state, e) {
    if (state.points === null || e.pointerId !== state.pointerId) {
        return;
    }

    e.stopPropagation();
    const events = typeof e.getCoalescedEvents === 'function' ? e.getCoalescedEvents() : [e];
    for (const ev of events.length ? events : [e]) {
        const p = toViewBox(state.svg, ev);
        const last = state.points[state.points.length - 1];
        if (Math.hypot(p[0] - last[0], p[1] - last[1]) >= MIN_STEP) {
            state.points.push(p);
        }
    }

    draw(state);
}

function onUp(state, e) {
    if (state.points === null || e.pointerId !== state.pointerId) {
        return;
    }

    e.stopPropagation();
    state.points.push(toViewBox(state.svg, e));
    const points = state.points;
    state.points = null;
    state.pointerId = null;
    if (strokeLength(points) < 1) {
        // A tap, not a stroke: let its click pick a hold.
        clearLayer(state.svg);
        return;
    }

    state.swallowClick = true;
    draw(state, points);
    const xs = points.map(p => p[0] / 100);
    const ys = points.map(p => p[1] / 100);
    state.dotnet.invokeMethodAsync('OnSplitStroke', xs, ys).catch(() => { /* circuit gone */ });
}

function onClick(state, e) {
    if (state.swallowClick) {
        state.swallowClick = false;
        e.stopPropagation();
        e.preventDefault();
    }
}

function cancelStroke(state) {
    if (state.points !== null) {
        state.points = null;
        state.pointerId = null;
        clearLayer(state.svg);
    }
}

function toViewBox(svg, e) {
    const m = svg.getScreenCTM();
    if (!m) {
        return [0, 0];
    }

    const pt = svg.createSVGPoint();
    pt.x = e.clientX;
    pt.y = e.clientY;
    const p = pt.matrixTransform(m.inverse());
    return [p.x, p.y];
}

function strokeLength(points) {
    let sum = 0;
    for (let i = 1; i < points.length; i++) {
        sum += Math.hypot(points[i][0] - points[i - 1][0], points[i][1] - points[i - 1][1]);
    }

    return sum;
}

function layer(svg) {
    return svg.querySelector('g[data-split-layer]');
}

function clearLayer(svg) {
    const g = svg ? layer(svg) : null;
    while (g && g.firstChild) {
        g.removeChild(g.firstChild);
    }
}

function draw(state, finished) {
    const g = layer(state.svg);
    if (!g) {
        return;
    }

    clearLayer(state.svg);
    const points = finished || state.points || [];
    const parts = splitPreview(state.outline, points);
    if (parts) {
        g.appendChild(polygon(parts[0], 'rgba(51,221,102,0.35)'));
        g.appendChild(polygon(parts[1], 'rgba(255,153,51,0.45)'));
    }

    const line = document.createElementNS(SVG_NS, 'polyline');
    line.setAttribute('points', points.map(p => `${p[0]},${p[1]}`).join(' '));
    line.setAttribute('fill', 'none');
    line.setAttribute('stroke', '#ffffff');
    line.setAttribute('stroke-width', '2.5');
    line.setAttribute('stroke-dasharray', '6 4');
    line.setAttribute('vector-effect', 'non-scaling-stroke');
    line.setAttribute('pointer-events', 'none');
    g.appendChild(line);
}

function polygon(ring, fill) {
    const el = document.createElementNS(SVG_NS, 'polygon');
    el.setAttribute('points', ring.map(p => `${p[0]},${p[1]}`).join(' '));
    el.setAttribute('fill', fill);
    el.setAttribute('stroke', 'none');
    el.setAttribute('pointer-events', 'none');
    return el;
}

// ---- The cut, as HoldOutlineBoolean.Split does it (outer ring only; the server also cuts out the holes) ----

/** The two parts, larger first, or null while the stroke does not cross the hold yet. */
export function splitPreview(outline, line) {
    if (!outline || outline.length < 3 || !line || line.length < 2) {
        return null;
    }

    const outer = signedArea(outline) < 0 ? outline.slice().reverse() : outline;
    const crossings = [];
    for (let i = 0; i + 1 < line.length; i++) {
        for (let e = 0; e < outer.length; e++) {
            const hit = intersect(line[i], line[i + 1], outer[e], outer[(e + 1) % outer.length]);
            if (hit) {
                crossings.push({ t: i + hit.s, edge: e, u: hit.u, p: lerp(line[i], line[i + 1], hit.s) });
            }
        }
    }

    crossings.sort((a, b) => a.t - b.t);
    let best = null;
    for (let k = 0; k + 1 < crossings.length; k++) {
        const a = crossings[k];
        const b = crossings[k + 1];
        if (b.t - a.t < 1e-9 || !contains(outer, pointAt(line, (a.t + b.t) / 2))) {
            continue;
        }

        const inside = [];
        for (let i = 0; i < line.length; i++) {
            if (i > a.t && i < b.t) {
                inside.push(line[i]);
            }
        }

        const one = walk(outer, b, a, inside);
        const two = walk(outer, a, b, inside.slice().reverse());
        const parts = Math.abs(signedArea(one)) >= Math.abs(signedArea(two)) ? [one, two] : [two, one];
        if (!best || Math.abs(signedArea(parts[1])) > Math.abs(signedArea(best[1]))) {
            best = parts;
        }
    }

    return best;
}

function walk(outer, from, to, cut) {
    const ring = [from.p];
    if (!(from.edge === to.edge && to.u > from.u)) {
        let e = from.edge;
        do {
            e = (e + 1) % outer.length;
            ring.push(outer[e]);
        } while (e !== to.edge);
    }

    ring.push(to.p);
    return ring.concat(cut);
}

function intersect(p, p2, a, b) {
    const r = [p2[0] - p[0], p2[1] - p[1]];
    const s = [b[0] - a[0], b[1] - a[1]];
    const denom = r[0] * s[1] - r[1] * s[0];
    if (Math.abs(denom) < 1e-12) {
        return null;
    }

    const ap = [a[0] - p[0], a[1] - p[1]];
    const t = (ap[0] * s[1] - ap[1] * s[0]) / denom;
    const u = (ap[0] * r[1] - ap[1] * r[0]) / denom;
    return t >= 0 && t < 1 && u >= 0 && u < 1 ? { s: t, u } : null;
}

function lerp(a, b, t) {
    return [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t];
}

function pointAt(line, t) {
    const i = Math.max(0, Math.min(line.length - 2, Math.floor(t)));
    return lerp(line[i], line[i + 1], t - i);
}

function signedArea(ring) {
    let sum = 0;
    for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
        sum += ring[j][0] * ring[i][1] - ring[j][1] * ring[i][0];
    }

    return sum / 2;
}

function contains(ring, p) {
    let inside = false;
    for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
        const a = ring[i];
        const b = ring[j];
        if ((a[1] > p[1]) !== (b[1] > p[1]) && p[0] < (b[0] - a[0]) * (p[1] - a[1]) / (b[1] - a[1]) + a[0]) {
            inside = !inside;
        }
    }

    return inside;
}

// ---- The split row's per-boulder chooser (HoldSplitRow.razor): a manual popover placed against the toolbar row ----

const placed = new WeakMap();

/** Shows the popover just below its toolbar row (above it when the row is docked at the bottom) and keeps it there. */
export function openBelowRow(pop) {
    if (!pop) {
        return;
    }

    try {
        if (!pop.matches(':popover-open')) {
            pop.showPopover();
        }
    } catch (_) { /* no Popover API: it renders in flow */ }

    const place = () => {
        const row = pop.closest('.bw-toolbar-row') || pop.closest('.option-bar') || pop.parentElement;
        if (!row || !pop.isConnected) {
            return;
        }

        const r = row.getBoundingClientRect();
        const w = Math.min(pop.offsetWidth, window.innerWidth - 16);
        const h = pop.offsetHeight;
        pop.style.left = `${Math.max(8, Math.min(r.left + 8, window.innerWidth - w - 8))}px`;
        const below = r.bottom + 6;
        pop.style.top = `${below + h > window.innerHeight - 8 ? Math.max(8, r.top - h - 6) : below}px`;
    };
    place();
    if (!placed.has(pop)) {
        window.addEventListener('resize', place);
        window.addEventListener('scroll', place, true);
        placed.set(pop, place);
    }
}

export function closePopover(pop) {
    const place = pop ? placed.get(pop) : null;
    if (place) {
        window.removeEventListener('resize', place);
        window.removeEventListener('scroll', place, true);
        placed.delete(pop);
    }

    try {
        pop.hidePopover();
    } catch (_) { /* not open */ }
}
