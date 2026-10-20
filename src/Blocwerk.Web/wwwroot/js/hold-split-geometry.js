// The split tool's pure parts (no DOM), shared by hold-split-line.js and node's tests (test/js/):
// the cut preview, which walks the outline as HoldOutlineBoolean.Split does, and the palm rule.

/** A touch this soon after the pen lifted is still the hand resting on the screen (as wall-shape-drag.js). */
export const PEN_GRACE_MS = 600;


/**
 * What a press of this pointer type means right now: 'draw', 'palm' (swallow it: the hand resting next to the pen)
 * or 'navigate' (leave it to the viewport's pan and pinch).
 */
export function touchRole(state, pointerType, now) {
    if (pointerType !== 'touch') {
        return 'draw';
    }

    if (state.down.size > 0 || now - state.upAt < PEN_GRACE_MS) {
        return 'palm';
    }

    return state.seen ? 'navigate' : 'draw';
}

// ---- The cut, as HoldOutlineBoolean.Split does it (outer ring only; the server also cuts out the holes) ----

/** The two parts, larger first, or null while the stroke does not cross the hold yet. */
export function splitPreview(outline, line) {
    if (!outline || outline.length < 3 || !line || line.length < 2) {
        return null;
    }

    const outer = signedArea(outline) < 0 ? outline.slice().reverse() : outline;
    line = withoutLoops(line);
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

/** The stroke with the loops it draws over itself cut out, as HoldOutlineBoolean.WithoutLoops does. */
export function withoutLoops(line) {
    let points = line.slice();
    for (let i = 0; i + 1 < points.length; i++) {
        for (let j = points.length - 2; j >= i + 2; j--) {
            const hit = intersect(points[i], points[i + 1], points[j], points[j + 1]);
            if (hit) {
                points = points.slice(0, i + 1).concat([lerp(points[i], points[i + 1], hit.s)], points.slice(j + 1));
                break;
            }
        }
    }

    return points;
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
