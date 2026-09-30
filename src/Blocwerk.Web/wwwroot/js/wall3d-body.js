// The solid wall body of the 3D wall view (wall3d.js): the wall is not a set of floating boards. Each
// facet's outline (its triangle cut included, wall3d-scene.js facetOutline) is extruded backwards, level,
// into a plain plywood block whose front face sits BODY_FRONT_MM behind the facet plane, so the facet, its holds
// and the captured wall surface in photo-real stay in front of it. In photo-real the splat draws with
// depth test (wall3d-splat-clip.js), so the opaque body hides whatever the capture put behind the wall.
//
// Two fixes keep the block closed and honest:
//   trim — a facet that pokes a margin past a neighbour's plane at an outer edge (a board ending where a
//          side panel turns back) is cut BODY_FRONT_MM behind that plane, so its block does not cover
//          the neighbour's surface and the two blocks meet at the edge;
//   slot — the back of a slot between two facing side panels (The Attic's corner, whose markers sit
//          only near the floor) is derived up to the panels' height, so the slot has a back wall.
// A few dozen triangles, one material: the camera keep-out (wall3d-reach.js) uses the same pieces.
import * as THREE from '../lib/three/three.module.min.js';
import { facetOutline, v3 } from './wall3d-scene.js';

export const BODY_FRONT_MM = 60;
const BODY_DEPTH_MM = 1500;
/** A facet reaching at most this far past a neighbour's plane (and lying behind it otherwise) is trimmed. */
const TRIM_SLACK_MM = 150;
const TRIM_BEHIND_MM = 300;
const ADJACENT_MM = 300;
/** Slot: facing side panels at most this far apart; their corners this near the back's plane count. */
const SLOT_GAP_MM = 1500;
const SLOT_NEAR_MM = 200;
const BODY_WOOD = 0xb99d72;

function frameOf(f) {
    return { id: f.id, o: v3(f.origin), u: v3(f.u), v: v3(f.v), n: v3(f.normal), outline: facetOutline(f).map(p => [p[0], p[1]]) };
}

export function pointOf(fr, a, b, h = 0) {
    return fr.o.clone().addScaledVector(fr.u, a).addScaledVector(fr.v, b).addScaledVector(fr.n, h);
}

const heightOf = (fr, p) => fr.n.dot(p) - fr.n.dot(fr.o);
const cornersOf = fr => fr.outline.map(([a, b]) => pointOf(fr, a, b));

/** Keeps the part of convex polygon `poly` ([a, b]) where ka·a + kb·b ≤ c. */
function clipHalf(poly, ka, kb, c) {
    const out = [];
    poly.forEach((p, i) => {
        const q = poly[(i + 1) % poly.length];
        const sp = c - ka * p[0] - kb * p[1], sq = c - ka * q[0] - kb * q[1];
        if (sp >= 0) out.push(p);
        if ((sp >= 0) !== (sq >= 0)) {
            const t = sp / (sp - sq);
            out.push([p[0] + t * (q[0] - p[0]), p[1] + t * (q[1] - p[1])]);
        }
    });
    return out.length >= 3 ? out : poly;
}

/** Signed distance from 2D point `p` to convex CCW polygon `poly` (negative inside). */
export function polygonDistance(poly, p) {
    let signed = -Infinity, nearest = Infinity;
    poly.forEach((a, i) => {
        const b = poly[(i + 1) % poly.length];
        const ex = b[0] - a[0], ey = b[1] - a[1], len = Math.hypot(ex, ey) || 1;
        signed = Math.max(signed, (ey * (p[0] - a[0]) - ex * (p[1] - a[1])) / len);   // > 0 outside this edge
        const t = Math.max(0, Math.min(1, ((p[0] - a[0]) * ex + (p[1] - a[1]) * ey) / (len * len)));
        nearest = Math.min(nearest, Math.hypot(p[0] - a[0] - t * ex, p[1] - a[1] - t * ey));
    });
    return signed > 0 ? nearest : signed;
}

function distanceToFacet(fr, p) {
    const d = p.clone().sub(fr.o);
    const lateral = Math.max(0, polygonDistance(fr.outline, [d.dot(fr.u), d.dot(fr.v)]));
    return Math.hypot(lateral, heightOf(fr, p));
}

const adjacent = (f, g) => Math.min(...cornersOf(f).map(p => distanceToFacet(g, p)), ...cornersOf(g).map(p => distanceToFacet(f, p))) <= ADJACENT_MM;

/** Cuts `f` BODY_FRONT_MM behind `g`'s plane where it only pokes a margin past it (an outer edge). */
function trim(f, before, g) {
    const hs = cornersOf(before).map(p => heightOf(g, p));
    const max = Math.max(...hs), min = Math.min(...hs);
    if (max <= 0 || max > TRIM_SLACK_MM || min > -TRIM_BEHIND_MM || !adjacent(before, g)) return;
    const front = pointOf(f, 0, 0, -BODY_FRONT_MM);
    f.outline = clipHalf(f.outline, f.u.dot(g.n), f.v.dot(g.n), -BODY_FRONT_MM - heightOf(g, front));
}

const vertical = fr => Math.abs(fr.n.z) < 0.3;

/** The a coordinate on `b`'s plane (at b = 0) where it meets `side`'s plane. */
function meetA(back, side) {
    const k = back.u.dot(side.n);
    return Math.abs(k) < 1e-3 ? null : -heightOf(side, back.o) / k;
}

/** Grows the back of a slot between two facing side panels up to the panels' height. */
function closeSlot(back, frames) {
    if (!vertical(back)) return false;
    const sides = frames.filter(s => s !== back && vertical(s) && Math.abs(s.n.dot(back.n)) < 0.3);
    for (const l of sides) {
        for (const r of sides) {
            if (l.n.dot(r.n) > -0.9 || heightOf(l, r.o) <= 0 || heightOf(r, l.o) <= 0 || heightOf(l, r.o) > SLOT_GAP_MM) continue;
            const aL = meetA(back, l), aR = meetA(back, r);
            const as = back.outline.map(p => p[0]);
            if (aL == null || aR == null || Math.min(aL, aR) < Math.min(...as) - 100 || Math.max(aL, aR) > Math.max(...as) + 100) continue;
            const near = [...cornersOf(l), ...cornersOf(r)].filter(p => Math.abs(heightOf(back, p)) <= SLOT_NEAR_MM);
            const bs = [...back.outline.map(p => p[1]), ...near.map(p => p.clone().sub(back.o).dot(back.v))];
            const lo = Math.min(aL, aR) - BODY_FRONT_MM, hi = Math.max(aL, aR) + BODY_FRONT_MM;
            const bMin = Math.min(...bs), bMax = Math.max(...bs);
            if (bMax <= Math.max(...back.outline.map(p => p[1])) + 100) continue;
            back.outline = [[lo, bMin], [hi, bMin], [hi, bMax], [lo, bMax]];
            back.derived = true;
            return true;
        }
    }
    return false;
}

/** The body pieces: per facet { id, o, u, v, n, outline ([a, b], convex CCW), derived }. */
export function bodyPieces(facets) {
    const frames = facets.filter(f => (f.corners || []).length >= 3).map(frameOf);
    frames.forEach(b => closeSlot(b, frames));
    const original = frames.map(f => ({ ...f, outline: f.outline.slice() }));
    frames.forEach((f, i) => original.forEach((g, j) => { if (i !== j) trim(f, original[i], g); }));
    return frames;
}

/** Backwards and level (an overhang's block must not rise over its top edge), or straight back for a roof. */
function backwards(p) {
    const level = p.n.clone().negate().setZ(0);
    return Math.abs(p.n.z) < 0.95 && level.lengthSq() > 1e-6 ? level.normalize() : p.n.clone().negate();
}

function pieceGeometry(p) {
    const front = p.outline.map(([a, b]) => pointOf(p, a, b, -BODY_FRONT_MM));
    const back = front.map(f => f.clone().addScaledVector(backwards(p), BODY_DEPTH_MM));
    const tris = [];
    for (let i = 1; i < front.length - 1; i++) {
        tris.push(front[0], front[i], front[i + 1], back[0], back[i + 1], back[i]);
    }
    front.forEach((f, i) => {
        const j = (i + 1) % front.length;
        tris.push(f, back[i], back[j], f, back[j], front[j]);
    });
    const g = new THREE.BufferGeometry().setFromPoints(tris);
    g.computeVertexNormals();
    return g;
}

/** Builds the body: { group, pieces, setGhosted(ids) } (a ghosted facet's block hides with it). */
export function buildBody(view) {
    const pieces = bodyPieces(view.facets || []);
    const material = new THREE.MeshStandardMaterial({ color: BODY_WOOD, roughness: 0.95, metalness: 0, side: THREE.DoubleSide });
    const group = new THREE.Group();
    for (const p of pieces) {
        const mesh = new THREE.Mesh(pieceGeometry(p), material);
        mesh.userData.facetId = p.id;
        group.add(mesh);
    }
    return {
        group,
        pieces,
        setGhosted(ids) {
            for (const m of group.children) m.visible = !ids.includes(m.userData.facetId);
        },
    };
}
