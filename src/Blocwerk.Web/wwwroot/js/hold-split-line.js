// The wall editor's split tool (WallPhotoEditor.MergeSplit.cs): draws the cut across the selected hold with a mouse,
// a finger or a pen, and previews the two parts LIVE in the browser, so no pointer move costs a server round-trip.
// Only the finished stroke goes to .NET (OnSplitStroke), once, where the server cuts the hold for real
// (HoldOutlineBoolean.Split) with the same walk as below.
//
// While armed:
// - the overlay svg gets touch-action: none, so a finger or Apple Pencil draws instead of scrolling the page;
// - a ONE-finger touch and a mouse press stop at the svg, so the viewport's pan recogniser (viewport-gestures.js) and
//   the hold drag (wall-hold-drag.js) never see them; a second finger cancels the stroke and is let through to pinch;
// - the click that ends a stroke is swallowed, so it does not count as a tap on a hold;
// - palm rejection, the Shape tool's rule (wall-shape-drag.js): while a pen is down and for a moment after it lifts,
//   a touch is the resting hand and is swallowed; once a pen has been used, a finger navigates and the pen draws.
// Everything is drawn into <g data-split-layer>, which Blazor renders EMPTY and never fills itself.

import { splitPreview, touchRole } from './hold-split-geometry.js';

const SVG_NS = 'http://www.w3.org/2000/svg';
const MIN_STEP = 0.15;
const states = new WeakMap();
const pen = { down: new Set(), upAt: -Infinity, seen: false };

function swallow(e) {
    e.stopPropagation();
    if (e.cancelable) {
        e.preventDefault();
    }
}

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
        pointercancel: (e) => { penUp(e); cancelStroke(state); },
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
    const role = touchRole(pen, 'touch', performance.now());
    if (role === 'palm') {
        swallow(e);
        return;
    }

    if (role === 'navigate') {
        return;
    }

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
    if (e.pointerType === 'pen') {
        pen.down.add(e.pointerId);
        pen.seen = true;
    }

    const role = touchRole(pen, e.pointerType, performance.now());
    if (role === 'palm') {
        swallow(e);
        return;
    }

    if (role === 'navigate' || state.points !== null || !e.isPrimary || (e.pointerType === 'mouse' && e.button !== 0)) {
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

function penUp(e) {
    if (e.pointerType === 'pen' && pen.down.delete(e.pointerId)) {
        pen.upAt = performance.now();
    }
}

function onUp(state, e) {
    penUp(e);
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
