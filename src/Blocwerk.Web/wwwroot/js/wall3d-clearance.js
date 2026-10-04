// Line of sight in the 3D wall view (wall3d.js) against its facets: which facets sit between the camera
// and the wall (free-orbit ghosting, wall3d-ghost.js). Four to a dozen facets: plain segment/triangle
// tests, cheap enough to run every frame. The camera is never moved for what is in its way; the
// facets in the way fade instead.
import * as THREE from '../lib/three/three.module.min.js';
import { v3 } from './wall3d-scene.js';

/**
 * No preset camera goes lower than this above the floor plane: the lowest facet edge (the kickboard's
 * bottom, at the floor), so this is the mats' ~33 cm plus a margin.
 */
export const FLOOR_CLEARANCE_MM = 550;
/** A facet crossed this close to a sight line's far end is the one looked at, not in the way. */
const END_SLACK_MM = 60;

/** Facet polygons for the segment tests: { id, facet, points (convex, world), center }. */
export function facetQuads(facets) {
    return facets.filter(f => (f.corners || []).length >= 3).map(f => {
        const points = f.corners.map(v3);
        const center = points.reduce((s, p) => s.add(p), new THREE.Vector3()).divideScalar(points.length);
        return { id: f.id, facet: f, points, center, normal: v3(f.normal) };
    });
}

/** Where the ray hits polygon `q` (a triangle fan), or null. */
function hitPolygon(q) {
    const p = q.points;
    for (let i = 1; i < p.length - 1; i++) {
        const hit = ray.intersectTriangle(p[0], p[i], p[i + 1], false, hitPoint);
        if (hit) {
            return hit;
        }
    }
    return null;
}

function inFront(q, p) {
    return q.normal.dot(p) - q.normal.dot(q.center) > 0;
}

const ray = new THREE.Ray();
const hitPoint = new THREE.Vector3();
const dir = new THREE.Vector3();

/**
 * Distance from `from` at which the segment from→to crosses quad `q` (short of its end) while `from`
 * is behind it, or null. Crossing a facet from its front is looking AT the wall (a preset's framed
 * target often floats in the air behind the wall); crossing one from behind is a side piece, the
 * plywood back or the overhang seen from above in the way.
 */
function crossing(q, from, to, anySide = false) {
    if (!anySide && inFront(q, from)) {
        return null;
    }
    dir.subVectors(to, from);
    const len = dir.length();
    if (len < 1e-6) {
        return null;
    }
    ray.set(from, dir.divideScalar(len));
    const p = hitPolygon(q);
    if (!p) {
        return null;
    }
    const t = from.distanceTo(p);
    return t < len - END_SLACK_MM ? t : null;
}

/** Ids of the facets in the way from `from` to `to`: crossed from behind (the end facet does not count). */
export function blockersBetween(quads, from, to) {
    return facetsInTheWay(quads, from, [to]);
}

const sample = new THREE.Vector3();
const toward = new THREE.Vector3();

/** Sample points of polygon `q`: its centre, its corners and edge midpoints pulled in toward the centre. */
function samplePoints(q) {
    const pts = [q.center.clone()];
    const n = q.points.length;
    for (let i = 0; i < n; i++) {
        const a = q.points[i], b = q.points[(i + 1) % n];
        pts.push(a.clone().lerp(q.center, 0.15), a.clone().add(b).multiplyScalar(0.5).lerp(q.center, 0.15));
    }

    return pts;
}

/**
 * Whether polygon `q` hides part of a wall polygon from `from`: some point of `q`, seen from there, has a
 * wall polygon (other than `q`) behind it. Sampling the occluder (not the wall) finds even a thin
 * closing panel that no sight line to the wall's own points happens to cross.
 */
function overlapsWall(q, from, wallQuads) {
    for (const s of q.samples ??= samplePoints(q)) {
        toward.subVectors(s, from);
        const near = toward.length();
        if (near < 1e-6) {
            continue;
        }

        ray.set(from, toward.divideScalar(near));
        for (const w of wallQuads) {
            if (w === q) {
                continue;
            }

            const p = hitPolygon(w);
            if (p && from.distanceTo(p) > near + END_SLACK_MM) {
                return true;
            }
        }
    }

    return false;
}

/**
 * Ids of the facets between `from` and the wall: crossed from behind by a sight line to any of `aims` (the
 * orbit target and sample points of the wall); a facet in `anySide` (no holds on it: a closing panel)
 * also when crossed from its front or when it covers any of `wallQuads` as seen from `from`; a facet with
 * holds also when it covers the wall and the camera is behind it. `skip` names facets that are the wall
 * itself and never count.
 */
export function facetsInTheWay(quads, from, aims, anySide = null, skip = null, wallQuads = null) {
    const ids = [];
    for (const q of quads) {
        if (skip?.has(q.id)) {
            continue;
        }

        const any = !!anySide?.has(q.id);
        const crossed = aims.some(to => crossing(q, from, to, any) != null);
        const covers = !crossed && wallQuads && (any || !inFront(q, from)) && overlapsWall(q, from, wallQuads);
        if (crossed || covers) {
            ids.push(q.id);
        }
    }

    return ids;
}
