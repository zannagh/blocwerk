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
/**
 * An enclosed recess (Wall3DRecesses): the closing triangle's block reaches this far outward, so nothing the capture
 * put beyond it shows, and the roof surface between the triangles' hypotenuses gets a block this deep behind it,
 * which fills the pocket up to its back wall.
 */
const RECESS_CLOSING_DEPTH_MM = 6000;
const RECESS_ROOF_DEPTH_MM = 3000;
/** A facet reaching at most this far past a neighbour's plane (and lying behind it otherwise) is trimmed. */
const TRIM_SLACK_MM = 150;
const TRIM_BEHIND_MM = 300;
const ADJACENT_MM = 300;
/** Slot: facing side panels at most this far apart; their corners this near the back's plane count. */
const SLOT_GAP_MM = 1500;
const SLOT_NEAR_MM = 200;
const BODY_WOOD = 0xb99d72;
const CEILING_WOOD = 0xd2bc96;
/**
 * The ceiling cap sits on the wall's highest edge (where the main wall meets the real ceiling), the floor
 * cap this far under its lowest.
 */
const CEILING_GAP_MM = 0;
const FLOOR_GAP_MM = 40;
/**
 * Lip: under the ceiling, in front of each facet whose top edge reaches it, a level strip this far below
 * the ceiling, from LIP_FROM_MM to LIP_TO_MM in front of that edge. It hides the capture's fuzzy fringe
 * along the ceiling line (the dark gap over a top beam, floaters), not the wall: it starts clear of the
 * holds at the top edge, and a camera below the ceiling looks past its near side onto the wall.
 */
const LIP_DROP_MM = 30;
const LIP_FROM_MM = 80;
const LIP_TO_MM = 900;
/** Corners this near the ceiling line form a facet's top edge there. */
const TOP_EDGE_MM = 50;
/** The caps reach this far past the wall on every side (beyond where the camera may go). */
const CAP_REACH_MM = 40000;

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

/**
 * Closes each enclosed recess: its outer closing triangle's block is made deep, and the roof surface between the
 * triangles' hypotenuses (on the main facet's plane, no facet of its own, never holds) becomes a piece of the main frame.
 */
function closeRecesses(frames, recesses) {
    const roofs = [];
    for (const r of recesses || []) {
        const closing = frames.find(f => f.id === r.closingId);
        const main = frames.find(f => f.id === r.mainId);
        if (!closing || !main || (r.roof || []).length < 3) continue;
        closing.depth = RECESS_CLOSING_DEPTH_MM;
        const outline = r.roof.map(c => {
            const d = v3(c).sub(main.o);
            return [d.dot(main.u), d.dot(main.v)];
        });
        roofs.push({ ...main, id: undefined, outline, depth: RECESS_ROOF_DEPTH_MM, roof: true, derived: true });
    }
    return roofs;
}

/** The body pieces: per facet { id, o, u, v, n, outline ([a, b], convex CCW), derived }, plus one roof piece per recess. */
export function bodyPieces(facets, recesses = []) {
    const frames = facets.filter(f => (f.corners || []).length >= 3).map(frameOf);
    frames.forEach(b => closeSlot(b, frames));
    const original = frames.map(f => ({ ...f, outline: f.outline.slice() }));
    frames.forEach((f, i) => original.forEach((g, j) => { if (i !== j) trim(f, original[i], g); }));
    return [...frames, ...closeRecesses(frames, recesses)];
}

/** Backwards and level (an overhang's block must not rise over its top edge), or straight back for a roof. */
function backwards(p) {
    const level = p.n.clone().negate().setZ(0);
    return Math.abs(p.n.z) < 0.95 && level.lengthSq() > 1e-6 ? level.normalize() : p.n.clone().negate();
}

function pieceGeometry(p) {
    const front = p.outline.map(([a, b]) => pointOf(p, a, b, -BODY_FRONT_MM));
    const back = front.map(f => f.clone().addScaledVector(backwards(p), p.depth ?? BODY_DEPTH_MM));
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

/** A level plane at height `z` over the wall's footprint and CAP_REACH_MM around it. */
function cap(corners, z, color) {
    const box = new THREE.Box3().setFromPoints(corners);
    const size = Math.max(box.max.x - box.min.x, box.max.y - box.min.y) + 2 * CAP_REACH_MM;
    const mesh = new THREE.Mesh(new THREE.PlaneGeometry(size, size),
        new THREE.MeshStandardMaterial({ color, roughness: 0.95, metalness: 0, side: THREE.DoubleSide }));
    mesh.position.set((box.min.x + box.max.x) / 2, (box.min.y + box.max.y) / 2, z);
    return mesh;
}

/** The lip strips ([4 corners] each) under a ceiling at `topZ` for the facets whose top edge reaches it. */
export function lipQuads(facets, topZ) {
    const quads = [];
    for (const f of facets) {
        const n = v3(f.normal);
        const forward = n.clone().setZ(0);
        const top = (f.corners || []).map(v3).filter(c => c.z >= topZ - TOP_EDGE_MM);
        if (Math.abs(n.z) > 0.95 || forward.lengthSq() < 1e-6 || top.length < 2) continue;
        forward.normalize();
        const along = new THREE.Vector3(-forward.y, forward.x, 0);
        const ts = top.map(c => c.dot(along));
        const edge = Math.max(...top.map(c => c.dot(forward)));
        const z = topZ - LIP_DROP_MM;
        const at = (t, s) => along.clone().multiplyScalar(t).addScaledVector(forward, edge + s).setZ(z);
        const [t0, t1] = [Math.min(...ts), Math.max(...ts)];
        quads.push([at(t0, LIP_FROM_MM), at(t1, LIP_FROM_MM), at(t1, LIP_TO_MM), at(t0, LIP_TO_MM)]);
    }
    return quads;
}

function lip(quad, material) {
    const [a, b, c, d] = quad;
    const g = new THREE.BufferGeometry().setFromPoints([a, b, c, a, c, d]);
    g.computeVertexNormals();
    return new THREE.Mesh(g, material);
}

/**
 * Builds the body: { group, pieces, ceilingZ, floorZ, setGhosted(ids) } (a ghosted facet's block hides
 * with it). The ceiling and floor caps close the room over the wall's top edge and under its foot: the
 * capture's floaters above the real ceiling and below the floor stay hidden; the lips under the ceiling
 * hide its fringe along the ceiling line.
 */
export function buildBody(view) {
    const pieces = bodyPieces(view.facets || [], view.recesses || []);
    const material = new THREE.MeshStandardMaterial({ color: BODY_WOOD, roughness: 0.95, metalness: 0, side: THREE.DoubleSide });
    const group = new THREE.Group();
    for (const p of pieces) {
        const mesh = new THREE.Mesh(pieceGeometry(p), material);
        mesh.userData.facetId = p.id;
        mesh.userData.roof = !!p.roof;
        group.add(mesh);
    }
    const corners = (view.facets || []).flatMap(f => (f.corners || []).map(v3));
    const ceilingZ = corners.length ? Math.max(...corners.map(c => c.z)) + CEILING_GAP_MM : null;
    const floorZ = corners.length ? Math.min(...corners.map(c => c.z)) - FLOOR_GAP_MM : null;
    if (corners.length) {
        const ceiling = cap(corners, ceilingZ, CEILING_WOOD);
        group.add(ceiling, cap(corners, floorZ, BODY_WOOD));
        lipQuads(view.facets || [], ceilingZ - CEILING_GAP_MM).forEach(q => group.add(lip(q, ceiling.material)));
    }
    return {
        group,
        pieces,
        ceilingZ,
        floorZ,
        setGhosted(ids) {
            for (const m of group.children) m.visible = !m.userData.facetId || !ids.includes(m.userData.facetId);
        },
    };
}
