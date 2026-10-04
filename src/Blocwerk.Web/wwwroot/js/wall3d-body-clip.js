// Keeps the solid wall body (wall3d-body.js) behind the real wall: a body block is a prism that reaches back
// from its facet, and where it swallows another real facet (a lip sticking out of a triangle's block) it would
// stand in front of that facet's visible side. Such a block is cut by the plane of every facet it contains, a
// little behind it, so added geometry only ever fills volume BEHIND the real surfaces.
import * as THREE from '../lib/three/three.module.min.js';

/** A block is cut this far behind the plane of a facet it swallows (clear of the facet's own surface). */
export const CLIP_BEHIND_MM = 8;
/** A facet counts as swallowed when a point of it lies this far inside the block (not on its boundary). */
const SWALLOW_MM = 8;
const SAMPLES = 4;

/** The faces of a prism (convex polygons of Vector3): `front` (the outline's points) and `back` (their offsets). */
export function prismFaces(front, back) {
    const faces = [front.slice(), back.slice().reverse()];
    front.forEach((f, i) => {
        const j = (i + 1) % front.length;
        faces.push([f, back[i], back[j], front[j]]);
    });
    return faces;
}

/** Sample points over a convex polygon of Vector3 corners: the corners, their centroid and a grid over its fan. */
function samplePoints(corners) {
    const mid = corners.reduce((s, p) => s.add(p), new THREE.Vector3()).divideScalar(corners.length);
    const out = [mid, ...corners.map(c => c.clone().lerp(mid, 0.02))];
    for (let k = 1; k < corners.length - 1; k++) {
        for (let i = 0; i <= SAMPLES; i++) {
            for (let j = 0; j <= SAMPLES - i; j++) {
                out.push(corners[0].clone().multiplyScalar(1 - (i + j) / SAMPLES)
                    .addScaledVector(corners[k], i / SAMPLES).addScaledVector(corners[k + 1], j / SAMPLES));
            }
        }
    }
    return out;
}

/**
 * The half-spaces ({ n, d }: keep n·x ≤ d) that cut piece `p` clear of the facets it swallows.
 * `p`: { o, u, v, n, outline, depth }, `dir`: its backwards direction, `others`: { n, o, corners } per real facet.
 * `inOutline(outline, [a, b])`: signed distance (negative inside).
 */
export function clipsFor(p, dir, others, frontMm, depthMm, signedDistance) {
    const dn = p.n.dot(dir);
    const clips = [];
    for (const g of others) {
        const inside = samplePoints(g.corners).some(q => {
            const rel = q.clone().sub(p.o);
            const t = (rel.dot(p.n) + frontMm) / dn;           // along dir from the block's front plane
            if (t <= SWALLOW_MM || t >= depthMm) {
                return false;
            }
            const onFront = rel.addScaledVector(dir, -t);
            return signedDistance(p.outline, [onFront.dot(p.u), onFront.dot(p.v)]) < -SWALLOW_MM;
        });
        if (inside) {
            clips.push({ n: g.n, d: g.n.dot(g.o) - CLIP_BEHIND_MM });
        }
    }
    return clips;
}

/** Cuts the closed convex polyhedron `faces` (polygons of Vector3) to the half-space n·x ≤ d, capping the cut. */
export function clipPolyhedron(faces, { n, d }) {
    const out = [], cut = [];
    for (const poly of faces) {
        const kept = [];
        poly.forEach((p, i) => {
            const q = poly[(i + 1) % poly.length];
            const sp = d - n.dot(p), sq = d - n.dot(q);
            if (sp >= 0) {
                kept.push(p);
            }
            if ((sp >= 0) !== (sq >= 0)) {
                const x = p.clone().lerp(q, sp / (sp - sq));
                kept.push(x);
                cut.push(x);
            }
        });
        if (kept.length >= 3) {
            out.push(kept);
        }
    }
    if (cut.length >= 3) {
        const mid = cut.reduce((s, p) => s.add(p), new THREE.Vector3()).divideScalar(cut.length);
        const ref = Math.abs(n.x) < 0.9 ? new THREE.Vector3(1, 0, 0) : new THREE.Vector3(0, 1, 0);
        const e1 = ref.clone().addScaledVector(n, -ref.dot(n)).normalize(), e2 = n.clone().cross(e1);
        const angle = p => Math.atan2(p.clone().sub(mid).dot(e2), p.clone().sub(mid).dot(e1));
        out.push(cut.sort((a, b) => angle(a) - angle(b)));
    }
    return out;
}

/** Triangles (flat array of Vector3, three per triangle) fanned over each polygon. */
export function triangulate(faces) {
    const tris = [];
    for (const poly of faces) {
        for (let i = 1; i < poly.length - 1; i++) {
            tris.push(poly[0], poly[i], poly[i + 1]);
        }
    }
    return tris;
}
