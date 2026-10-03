// Holds of the 3D wall view (wall3d.js) as their REAL outlines: each hold's facet-space polygon
// (Wall3DHoldShape, mm relative to its plane centre, pocket holes included) becomes a shallow
// bevelled slab lying on its facet. ~880 holds would be ~880 draw calls as separate meshes, so every
// slab is baked into world space and merged into ONE geometry per state (lit / dimmed) with
// per-vertex colour; the boulder rings stay one InstancedMesh. Picking goes through a separate,
// never-drawn geometry: each hold's outer outline WITHOUT its holes (a tap inside a pocket still
// selects the hold, as in the 2D views) as a prism from the surface it sits on up to its cap
// (wall3d-relief.js), the shape the photo-real overlay draws.
import * as THREE from '../lib/three/three.module.min.js';
import { reliefOf } from './wall3d-relief.js';
import { facetBasis, v3 } from './wall3d-scene.js';

const BASE_LIFT = 3.5;            // slab base above the facet: over the photo (3 mm) and markers (1.5 mm)
/** Height of the Photos-mode outline lines above their facet (wall3d-outlines.js). */
export const OUTLINE_LIFT = BASE_LIFT + 1.5;
const FOOT_DARKEN = 0.28;         // feet read darker than hand holds
const RING_SCALE = 1.3;           // boulder-role ring, relative to the outline's bounding box
const CIRCLE_SEGMENTS = 24;
/** Opacity of a ghosted facet's holds (wall3d-ghost.js). */
export const GHOST_OPACITY = 0.18;

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

/**
 * Facet-local frame of a hold: x/y along u/v with the origin at its plane centre (moved by `shift`
 * [da, db] mm when given: a hold on a volume, wall3d-relief.js), z along the normal. A hold placed on a
 * detected volume (`protrusion.normal`, with a shift) is tilted to the volume's surface there: `lift` still
 * counts from the facet plane, the part above the volume (`protrusion.baseMm`) along the tilted normal.
 */
export function holdFrame(h, facet, lift, shift) {
    const [sa, sb] = shift || [0, 0];
    const at = v3(facet.origin).addScaledVector(v3(facet.u), h.planeA + sa).addScaledVector(v3(facet.v), h.planeB + sb);
    const n = shift && h.protrusion && h.protrusion.normal;
    if (!n) return facetBasis(facet).setPosition(at.addScaledVector(v3(facet.normal), lift));
    const u = v3(facet.u);
    const up = u.clone().multiplyScalar(n[0]).addScaledVector(v3(facet.v), n[1]).addScaledVector(v3(facet.normal), n[2]).normalize();
    const x = u.addScaledVector(up, -u.dot(up)).normalize();
    const y = new THREE.Vector3().crossVectors(up, x);
    const base = h.protrusion.baseMm || 0;
    at.addScaledVector(v3(facet.normal), base).addScaledVector(up, lift - base);
    return new THREE.Matrix4().makeBasis(x, y, up).setPosition(at);
}

/** [da, db] of a hold placed on a detected volume (drawn there in every mode), else null. */
export function volumeShift(h) {
    const p = h.protrusion;
    return p && p.volumeId ? [p.shiftA || 0, p.shiftB || 0] : null;
}

/** Height of the volume surface under a hold placed on one, else 0 (mm above the facet plane). */
export function volumeBase(h) {
    return volumeShift(h) ? Math.max(0, h.protrusion.baseMm || 0) : 0;
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
    // The bevel reaches `bevel` below z = 0: sit the slab's underside on the lifted base (on its volume, if any).
    geo.applyMatrix4(holdFrame(h, facet, volumeBase(h) + BASE_LIFT + bevel, volumeShift(h)));
    return geo.index ? geo.toNonIndexed() : geo;
}

/** Top of a hold's overlay: its slab (Schematic) or its relief's cap (photo-real), whichever is higher. */
function topOf(h) {
    return Math.max(volumeBase(h) + BASE_LIFT + slabDepth(h), reliefOf(h).cap);
}

/**
 * The outer outline (holes filled) as a prism from the hold's base to its top: what a tap hits. A hold
 * moved onto a volume also keeps a flat face at its photo spot (Photos, Schematic).
 */
function pickPrism(h, facet) {
    const r = reliefOf(h);
    const moved = r.shift[0] !== 0 || r.shift[1] !== 0;
    const base = moved ? r.base : Math.min(BASE_LIFT, r.base);
    const geo = new THREE.ExtrudeGeometry(toShape(h, false), { depth: Math.max(1, topOf(h) - base), curveSegments: 1, steps: 1, bevelEnabled: false });
    geo.applyMatrix4(holdFrame(h, facet, base, r.shift));
    const prism = geo.index ? geo.toNonIndexed() : geo;
    if (!moved || volumeShift(h)) return prism;
    const flat = new THREE.ShapeGeometry(toShape(h, false), 1);
    flat.applyMatrix4(holdFrame(h, facet, BASE_LIFT + slabDepth(h)));
    return concat(prism, flat.index ? flat.toNonIndexed() : flat);
}

/** Two non-indexed geometries as one (positions and normals). */
function concat(a, b) {
    const g = new THREE.BufferGeometry();
    for (const name of ['position', 'normal']) {
        const x = a.attributes[name].array, y = b.attributes[name].array;
        const out = new Float32Array(x.length + y.length);
        out.set(x);
        out.set(y, x.length);
        g.setAttribute(name, new THREE.BufferAttribute(out, 3));
    }
    a.dispose();
    b.dispose();
    return g;
}

/**
 * Concatenates non-indexed geometries into one, painting each part in its colour and tagging it
 * with its facet `slot` (wall3d-sides.js hides a facet's parts from behind). Returns the
 * merged geometry and `starts`: the first triangle of every part, for face → part lookups.
 */
function merge(parts) {
    const count = parts.reduce((n, p) => n + p.geo.attributes.position.count, 0);
    const pos = new Float32Array(count * 3);
    const nrm = new Float32Array(count * 3);
    const col = new Float32Array(count * 3);
    const slot = new Float32Array(count);
    const starts = new Int32Array(parts.length);
    let at = 0;
    parts.forEach((p, i) => {
        const g = p.geo;
        const n = g.attributes.position.count;
        starts[i] = at / 3;
        pos.set(g.attributes.position.array, at * 3);
        if (g.attributes.normal) nrm.set(g.attributes.normal.array, at * 3);
        slot.fill(p.slot ?? 0, at, at + n);
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
    merged.setAttribute('facetIndex', new THREE.BufferAttribute(slot, 1));
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

/**
 * A flat ring matrix around a hold's outline, `scale` × its bounding box, just above its slab / cap;
 * `raised`: around the photo-real overlay's cap instead (a hold on a volume moved onto it).
 */
export function ringMatrix(h, facet, scale, raised = false) {
    const f = footprint(h);
    const r = reliefOf(h);
    const m = raised || volumeShift(h) ? holdFrame(h, facet, (raised ? r.cap : topOf(h)) + 1.5, r.shift) : holdFrame(h, facet, topOf(h) + 1.5);
    m.multiply(new THREE.Matrix4().makeTranslation(f.ca, f.cb, 0));
    return m.scale(new THREE.Vector3(f.w * scale, f.h * scale, 1));
}

/**
 * Holds as outline slabs: `lit` (normal, or the highlighted boulder's holds), `dim` (everything else
 * while a boulder is highlighted), `rings` (boulder roles) and `pick` (never drawn). `holdAt(intersection)` maps a pick hit to its hold.
 * `sides` (wall3d-sides.js) hides the slabs of facets the camera is behind.
 */
export function buildHolds(view, roleColors, sides) {
    const facets = new Map(view.facets.map(f => [f.id, f]));
    const holds = view.holds.filter(h => facets.has(h.facetId));
    const highlighting = !!view.boulderId;
    const lit = holds.filter(h => !highlighting || h.role);
    const dim = highlighting ? holds.filter(h => !h.role) : [];

    const slabs = (list, dimmed) => merge(list.map(h => ({
        geo: slab(h, facets.get(h.facetId)), color: holdColor(h, dimmed), slot: sides.index.get(h.facetId),
    })));
    const litGeo = slabs(lit, false).geometry;
    const litMesh = new THREE.Mesh(litGeo, sides.cullBehind(
        new THREE.MeshStandardMaterial({ vertexColors: true, roughness: 0.55, metalness: 0.02, side: THREE.DoubleSide }), 'solid'));
    // The same slabs, faded, for a facet ghosted out of the camera's way (wall3d-ghost.js).
    litMesh.add(new THREE.Mesh(litGeo, sides.cullBehind(new THREE.MeshStandardMaterial({
        vertexColors: true, roughness: 0.7, transparent: true, opacity: GHOST_OPACITY, depthWrite: false,
    }), 'ghost')));
    const dimMesh = new THREE.Mesh(slabs(dim, true).geometry, sides.cullBehind(new THREE.MeshStandardMaterial({
        vertexColors: true, roughness: 0.7, transparent: true, opacity: 0.22, depthWrite: false,
    })));
    dimMesh.renderOrder = 2;

    const ringGeo = new THREE.RingGeometry(0.44, 0.5, 40);
    // Front side only: a ring lies flat on its facet, facing out, so from behind the wall it is culled.
    // Transparent (drawn late): in photo-real the rings go over the splat (wall3d-overlay.js).
    const ringMesh = new THREE.InstancedMesh(ringGeo, new THREE.MeshBasicMaterial({ side: THREE.FrontSide, transparent: true }),
        Math.max(1, highlighting ? lit.length : 0));
    ringMesh.renderOrder = 6;
    ringMesh.count = highlighting ? lit.length : 0;
    const color = new THREE.Color();
    // Photo-real puts the rings around the raised overlay (wall3d-overlay.js); the other modes around the slabs.
    ringMesh.userData.setRaised = raised => {
        if (!highlighting) return;
        lit.forEach((h, i) => ringMesh.setMatrixAt(i, ringMatrix(h, facets.get(h.facetId), RING_SCALE, raised)));
        ringMesh.instanceMatrix.needsUpdate = true;
        ringMesh.computeBoundingSphere();
    };
    if (highlighting) {
        lit.forEach((h, i) => ringMesh.setColorAt(i, color.set(roleColors[h.role] || roleColors.Hand)));
        if (ringMesh.instanceColor) ringMesh.instanceColor.needsUpdate = true;
        ringMesh.userData.setRaised(false);
    }

    const ordered = [...lit, ...dim];
    const picked = merge(ordered.map(h => ({ geo: pickPrism(h, facets.get(h.facetId)) })));
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
    mesh.userData.facet = facet;
    mesh.visible = true;
}
