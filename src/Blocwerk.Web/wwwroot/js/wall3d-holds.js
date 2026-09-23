// Holds of the 3D wall view (wall3d.js) as their REAL outlines: each hold's facet-space polygon
// (Wall3DHoldShape, mm relative to its plane centre, pocket holes included) becomes a shallow
// bevelled slab lying on its facet. ~880 holds would be ~880 draw calls as separate meshes, so every
// slab is baked into world space and merged into ONE geometry per state (lit / dimmed) with
// per-vertex colour; the boulder rings stay one InstancedMesh. Picking goes through a separate,
// never-drawn geometry of the holds' outer outlines WITHOUT their holes, so a tap inside a pocket
// still selects the hold (as in the 2D views).
import * as THREE from '../lib/three/three.module.min.js';
import { facetBasis, v3 } from './wall3d-scene.js';

const BASE_LIFT = 3.5;            // slab base above the facet: over the photo (3 mm) and markers (1.5 mm)
/** Height of the Photos-mode outline lines above their facet (wall3d-outlines.js). */
export const OUTLINE_LIFT = BASE_LIFT + 1.5;
const FOOT_DARKEN = 0.28;         // feet read darker than hand holds
const RING_SCALE = 1.3;           // boulder-role ring, relative to the outline's bounding box
const CIRCLE_SEGMENTS = 24;

/** Outline of a hold as [[da, db], …]; an ellipse of its mm size when the payload carries none. */
export function outlineOf(h) {
    if (h.shape && h.shape.outline && h.shape.outline.length >= 3) return h.shape.outline;
    const out = [];
    for (let i = 0; i < CIRCLE_SEGMENTS; i++) {
        const t = (2 * Math.PI * i) / CIRCLE_SEGMENTS;
        out.push([(h.widthMm / 2) * Math.cos(t), (h.heightMm / 2) * Math.sin(t)]);
    }
    return out;
}

function holesOf(h) {
    return (h.shape && h.shape.holes ? h.shape.holes : []).filter(r => r.length >= 3);
}

/** Bounding box of the outline around the plane centre: { w, h, ca, cb } (centre offset in mm). */
export function footprint(h) {
    const o = outlineOf(h);
    let aMin = Infinity, aMax = -Infinity, bMin = Infinity, bMax = -Infinity;
    for (const [a, b] of o) {
        aMin = Math.min(aMin, a); aMax = Math.max(aMax, a);
        bMin = Math.min(bMin, b); bMax = Math.max(bMax, b);
    }
    return { w: Math.max(aMax - aMin, 1), h: Math.max(bMax - bMin, 1), ca: (aMin + aMax) / 2, cb: (bMin + bMax) / 2 };
}

/** Slab thickness: a hold's relief, proportional to its size but never a wafer or a brick. */
export function slabDepth(h) {
    const f = footprint(h);
    return Math.max(5, Math.min(22, 0.14 * Math.min(f.w, f.h)));
}

/** Facet-local frame of a hold: x/y along u/v with the origin at its plane centre, z along the normal. */
export function holdFrame(h, facet, lift) {
    const m = facetBasis(facet);
    const p = v3(facet.origin).addScaledVector(v3(facet.u), h.planeA).addScaledVector(v3(facet.v), h.planeB)
        .addScaledVector(v3(facet.normal), lift);
    return m.setPosition(p);
}

function toShape(h, withHoles) {
    const shape = new THREE.Shape(outlineOf(h).map(([a, b]) => new THREE.Vector2(a, b)));
    if (withHoles) {
        for (const ring of holesOf(h)) shape.holes.push(new THREE.Path(ring.map(([a, b]) => new THREE.Vector2(a, b))));
    }
    return shape;
}

/** One hold's slab, baked into world space; non-indexed. */
function slab(h, facet) {
    const depth = slabDepth(h);
    const bevel = Math.min(2.5, depth / 3);
    const geo = new THREE.ExtrudeGeometry(toShape(h, true), {
        depth: depth - 2 * bevel, curveSegments: 1, steps: 1,
        bevelEnabled: true, bevelThickness: bevel, bevelSize: bevel, bevelOffset: -bevel, bevelSegments: 2,
    });
    // The bevel reaches `bevel` below z = 0: sit the slab's underside on the lifted base.
    geo.applyMatrix4(holdFrame(h, facet, BASE_LIFT + bevel));
    return geo.index ? geo.toNonIndexed() : geo;
}

/** The outer outline (holes filled), flat at the slab's top: what a tap hits. */
function pickFace(h, facet) {
    const geo = new THREE.ShapeGeometry(toShape(h, false), 1);
    geo.applyMatrix4(holdFrame(h, facet, BASE_LIFT + slabDepth(h)));
    return geo.index ? geo.toNonIndexed() : geo;
}

/**
 * Concatenates non-indexed geometries into one, painting each part in its colour. Returns the
 * merged geometry and `starts`: the first triangle of every part, for face → part lookups.
 */
function merge(parts) {
    const count = parts.reduce((n, p) => n + p.geo.attributes.position.count, 0);
    const pos = new Float32Array(count * 3);
    const nrm = new Float32Array(count * 3);
    const col = new Float32Array(count * 3);
    const starts = new Int32Array(parts.length);
    let at = 0;
    parts.forEach((p, i) => {
        const g = p.geo;
        const n = g.attributes.position.count;
        starts[i] = at / 3;
        pos.set(g.attributes.position.array, at * 3);
        if (g.attributes.normal) nrm.set(g.attributes.normal.array, at * 3);
        if (p.color) {
            for (let k = (at * 3), end = (at + n) * 3; k < end; k += 3) {
                col[k] = p.color.r; col[k + 1] = p.color.g; col[k + 2] = p.color.b;
            }
        }
        at += n;
        g.dispose();
    });
    const merged = new THREE.BufferGeometry();
    merged.setAttribute('position', new THREE.BufferAttribute(pos, 3));
    merged.setAttribute('normal', new THREE.BufferAttribute(nrm, 3));
    merged.setAttribute('color', new THREE.BufferAttribute(col, 3));
    merged.computeBoundingSphere();
    return { geometry: merged, starts };
}

/** Index of the part a triangle belongs to (binary search over `starts`). */
function partOf(starts, face) {
    let lo = 0, hi = starts.length - 1;
    while (lo < hi) {
        const mid = (lo + hi + 1) >> 1;
        if (starts[mid] <= face) lo = mid; else hi = mid - 1;
    }
    return lo;
}

export function holdColor(h, dimmed) {
    const c = new THREE.Color(h.hex);
    if (h.isFoot) c.lerp(new THREE.Color(0x000000), FOOT_DARKEN);
    if (dimmed) c.lerp(new THREE.Color(0x9a9aa2), 0.5);
    return c;
}

/** A flat ring matrix around a hold's outline, `scale` × its bounding box, just above its slab. */
export function ringMatrix(h, facet, scale) {
    const f = footprint(h);
    const m = holdFrame(h, facet, BASE_LIFT + slabDepth(h) + 1.5);
    m.multiply(new THREE.Matrix4().makeTranslation(f.ca, f.cb, 0));
    return m.scale(new THREE.Vector3(f.w * scale, f.h * scale, 1));
}

/**
 * Holds as outline slabs: `lit` (normal, or the highlighted boulder's holds), `dim` (everything else
 * while a boulder is highlighted), `rings` (boulder roles) and `pick` (never drawn). `holdAt(intersection)` maps a pick hit to its hold.
 */
export function buildHolds(view, roleColors) {
    const facets = new Map(view.facets.map(f => [f.id, f]));
    const holds = view.holds.filter(h => facets.has(h.facetId));
    const highlighting = !!view.boulderId;
    const lit = holds.filter(h => !highlighting || h.role);
    const dim = highlighting ? holds.filter(h => !h.role) : [];

    const slabs = (list, dimmed) => merge(list.map(h => ({ geo: slab(h, facets.get(h.facetId)), color: holdColor(h, dimmed) })));
    const litMesh = new THREE.Mesh(slabs(lit, false).geometry,
        new THREE.MeshStandardMaterial({ vertexColors: true, roughness: 0.55, metalness: 0.02, side: THREE.DoubleSide }));
    const dimMesh = new THREE.Mesh(slabs(dim, true).geometry, new THREE.MeshStandardMaterial({
        vertexColors: true, roughness: 0.7, transparent: true, opacity: 0.22, depthWrite: false,
    }));
    dimMesh.renderOrder = 2;

    const ringGeo = new THREE.RingGeometry(0.44, 0.5, 40);
    const ringMesh = new THREE.InstancedMesh(ringGeo, new THREE.MeshBasicMaterial({ side: THREE.DoubleSide }),
        Math.max(1, highlighting ? lit.length : 0));
    ringMesh.count = highlighting ? lit.length : 0;
    const color = new THREE.Color();
    if (highlighting) {
        lit.forEach((h, i) => {
            ringMesh.setMatrixAt(i, ringMatrix(h, facets.get(h.facetId), RING_SCALE));
            ringMesh.setColorAt(i, color.set(roleColors[h.role] || roleColors.Hand));
        });
        ringMesh.instanceMatrix.needsUpdate = true;
        if (ringMesh.instanceColor) ringMesh.instanceColor.needsUpdate = true;
        ringMesh.computeBoundingSphere();
    }

    const ordered = [...lit, ...dim];
    const picked = merge(ordered.map(h => ({ geo: pickFace(h, facets.get(h.facetId)) })));
    const pick = new THREE.Mesh(picked.geometry, new THREE.MeshBasicMaterial({ side: THREE.DoubleSide, visible: false }));

    return {
        lit: litMesh, dim: dimMesh, rings: ringMesh, pick, facets,
        litHolds: lit, dimHolds: dim, all: ordered,
        holdAt: hit => (hit && hit.faceIndex != null ? ordered[partOf(picked.starts, hit.faceIndex)] : null),
    };
}

/** A selection halo for one hold (reused; moved on each pick). */
export function buildSelection() {
    const mesh = new THREE.Mesh(
        new THREE.RingGeometry(0.46, 0.5, 48),
        new THREE.MeshBasicMaterial({ color: 0xffffff, side: THREE.DoubleSide, depthTest: false, transparent: true }));
    mesh.renderOrder = 10;
    mesh.matrixAutoUpdate = false;
    mesh.visible = false;
    return mesh;
}

export function placeSelection(mesh, hold, facet) {
    mesh.matrix.copy(ringMatrix(hold, facet, 1.55));
    mesh.visible = true;
}
