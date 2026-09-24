// Hold relief for the photo-real overlay of the 3D wall view (wall3d-outlines.js, wall3d-holds.js).
// A hold stands 10–150 mm out of its facet, so an outline drawn ON the facet sits behind the real hold
// and slides off it from any angle but straight on (parallax). Each hold's `protrusion` (measured
// from the splat, Wall3DHoldProtrusion; estimated from its size otherwise) lifts its overlay: a CAP
// contour at CAP_FRACTION of the way from the surface the hold sits on (the wall, or a volume) to its
// apex, and a faint COLLAR (the contour at the base plus a few uprights) that ties the cap to the wall.
// The cap is also where the boulder rings and the pick prism's top go. A hold on a volume is drawn where
// it really sits (`shiftA` / `shiftB`: its flat-mapped plane centre moved onto the volume; auto,
// unreviewed); the Photos mode keeps it at the flat spot, where the rectified photo shows it.
import * as THREE from '../lib/three/three.module.min.js';

/** Share of the way from the base to the apex where the cap contour is drawn. */
export const CAP_FRACTION = 0.65;
/** Lift of the collar's base contour above the surface the hold sits on (over the splat's wall). */
const BASE_LIFT = 2;
/** Uprights of the collar per hold. */
const UPRIGHTS = 6;
/** The lowest cap: a hold never reads as flatter than this. */
const MIN_CAP = 8;

/** A size estimate for payloads without `protrusion` (same fit as HoldProtrusion.Estimate). */
function estimate(h) {
    const o = h.shape && h.shape.outline && h.shape.outline.length >= 3 ? h.shape.outline : null;
    const w = o ? Math.max(...o.map(p => p[0])) - Math.min(...o.map(p => p[0])) : h.widthMm;
    const hh = o ? Math.max(...o.map(p => p[1])) - Math.min(...o.map(p => p[1])) : h.heightMm;
    const body = Math.min(90, Math.max(8, 0.16 * Math.sqrt(Math.max(w, 1) * Math.max(hh, 1)) + 14));
    return { baseMm: 0, heightMm: body, apexA: 0, apexB: 0, apexMm: body * 1.3, measured: false, onVolume: false, shiftA: 0, shiftB: 0 };
}

/**
 * { base, cap, apex: [a, b, h], shift: [a, b], onVolume }: heights in mm above the facet plane (base = the
 * wall or the volume under the hold); apex and shift relative to the hold's plane centre.
 */
export function reliefOf(h) {
    const p = h.protrusion || estimate(h);
    const base = Math.max(0, p.baseMm) + BASE_LIFT;
    const top = Math.max(p.apexMm, p.heightMm, base);
    const cap = Math.max(base + MIN_CAP, base + CAP_FRACTION * (top - base));
    return { base, cap, apex: [p.apexA, p.apexB, Math.max(top, cap)], shift: [p.shiftA || 0, p.shiftB || 0], onVolume: !!p.onVolume };
}

/**
 * Line segments of the raised overlay for `list`: `cap` (the contour at cap height, plus pocket holes)
 * and `collar` (the base contour and UPRIGHTS uprights up to the cap), in world mm, coloured per vertex
 * and tagged with the facet slot (wall3d-sides.js). `frameOf(h, lift, shift)` is wall3d-holds.js' holdFrame.
 */
export function raisedSegments(list, frameOf, colorOf, slotOf, rings) {
    const cap = { pos: [], col: [], slot: [] };
    const collar = { pos: [], col: [], slot: [] };
    const p = new THREE.Vector3();
    const push = (into, m, a, b, c, s) => {
        p.set(a[0], a[1], 0).applyMatrix4(m);
        into.pos.push(p.x, p.y, p.z);
        p.set(b[0], b[1], 0).applyMatrix4(m);
        into.pos.push(p.x, p.y, p.z);
        into.col.push(c.r, c.g, c.b, c.r, c.g, c.b);
        into.slot.push(s, s);
    };
    for (const h of list) {
        const r = reliefOf(h);
        const top = frameOf(h, r.cap, r.shift);
        const bottom = frameOf(h, r.base, r.shift);
        const c = colorOf(h);
        const s = slotOf(h);
        const all = rings(h);
        for (const ring of all) {
            for (let i = 0; i < ring.length; i++) push(cap, top, ring[i], ring[(i + 1) % ring.length], c, s);
        }
        const outer = all[0];
        for (let i = 0; i < outer.length; i++) push(collar, bottom, outer[i], outer[(i + 1) % outer.length], c, s);
        const step = Math.max(1, Math.floor(outer.length / UPRIGHTS));
        for (let i = 0; i < outer.length; i += step) {
            const q = outer[i];
            const lo = new THREE.Vector3(q[0], q[1], 0).applyMatrix4(bottom);
            const hi = new THREE.Vector3(q[0], q[1], 0).applyMatrix4(top);
            collar.pos.push(lo.x, lo.y, lo.z, hi.x, hi.y, hi.z);
            collar.col.push(c.r, c.g, c.b, c.r, c.g, c.b);
            collar.slot.push(s, s);
        }
    }
    return { cap: geometry(cap), collar: geometry(collar) };
}

function geometry({ pos, col, slot }) {
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
    g.setAttribute('color', new THREE.Float32BufferAttribute(col, 3));
    g.setAttribute('facetIndex', new THREE.Float32BufferAttribute(slot, 1));
    return g;
}
