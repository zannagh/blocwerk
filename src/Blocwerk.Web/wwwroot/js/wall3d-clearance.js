// Line of sight in the 3D wall view (wall3d.js) against its facets: which facets sit between two
// points (free-orbit ghosting, wall3d-ghost.js), and camera presets moved into free space with a
// clear view of the wall (wall3d-camera.js). Four to a dozen facets: plain segment/triangle tests,
// cheap enough to run every frame.
import * as THREE from '../lib/three/three.module.min.js';
import { v3 } from './wall3d-scene.js';

/**
 * No preset camera goes lower than this above the floor plane: the lowest facet edge (the kickboard's
 * bottom, at the floor), so this is the mats' ~33 cm plus a margin.
 */
export const FLOOR_CLEARANCE_MM = 550;
/** A facet crossed this close to a sight line's far end is the one looked at, not in the way. */
const END_SLACK_MM = 60;
/** A pulled-in camera stops this far past the facet in its way. */
const PULL_PAST_MM = 150;
const MIN_DISTANCE_MM = 300;
const DEG = Math.PI / 180;
/** Preset search: azimuth / elevation offsets tried, cheapest (smallest turn) first. */
const YAWS = [0, 15, -15, 30, -30, 45, -45, 60, -60, 75, -75];
const PITCHES = [0, 10, -10, 20, -20, 30, -30, 40, -40];
const MAX_ELEVATION = 84 * DEG;

/** Facet quads for the segment tests: { id, facet, a, b, c, d, center }. */
export function facetQuads(facets) {
    return facets.filter(f => (f.corners || []).length === 4).map(f => {
        const e = f.extent;
        const center = v3(f.origin).addScaledVector(v3(f.u), (e.aMin + e.aMax) / 2).addScaledVector(v3(f.v), (e.bMin + e.bMax) / 2);
        const [a, b, c, d] = f.corners.map(v3);
        return { id: f.id, facet: f, a, b, c, d, center, normal: v3(f.normal) };
    });
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
function crossing(q, from, to) {
    if (inFront(q, from)) {
        return null;
    }
    dir.subVectors(to, from);
    const len = dir.length();
    if (len < 1e-6) {
        return null;
    }
    ray.set(from, dir.divideScalar(len));
    const p = ray.intersectTriangle(q.a, q.b, q.c, false, hitPoint) || ray.intersectTriangle(q.a, q.c, q.d, false, hitPoint);
    if (!p) {
        return null;
    }
    const t = from.distanceTo(p);
    return t < len - END_SLACK_MM ? t : null;
}

/** Ids of the facets in the way from `from` to `to`: crossed from behind (the end facet does not count). */
export function blockersBetween(quads, from, to) {
    const ids = [];
    for (const q of quads) {
        if (crossing(q, from, to) != null) {
            ids.push(q.id);
        }
    }
    return ids;
}

function nearestCrossing(quads, from, to) {
    let best = null;
    for (const q of quads) {
        const t = crossing(q, from, to);
        if (t != null && (best == null || t < best)) {
            best = t;
        }
    }
    return best;
}

/**
 * Whether a camera at `position` is in free space with a clear view: above the floor clearance,
 * no facet in the way of the target, nor of the centre of any facet whose front it faces (at least one).
 */
export function poseIsClear(pose, quads, floorZ) {
    if (pose.position.z < floorZ + FLOOR_CLEARANCE_MM) {
        return false;
    }
    if (nearestCrossing(quads, pose.position, pose.target) != null) {
        return false;
    }
    const facing = quads.filter(q => inFront(q, pose.position));
    return facing.length > 0 && facing.every(q => nearestCrossing(quads, pose.position, q.center) == null);
}

function turned(base, yaw, pitch) {
    const h = Math.hypot(base.x, base.y);
    const az = Math.atan2(base.y, base.x) + yaw * DEG;
    const el = Math.max(-MAX_ELEVATION, Math.min(MAX_ELEVATION, Math.atan2(base.z, h) + pitch * DEG));
    return new THREE.Vector3(Math.cos(el) * Math.cos(az), Math.cos(el) * Math.sin(az), Math.sin(el));
}

/**
 * Moves a preset pose out of the way of facets: `base` is the pose as designed, `poseAlong(dir)`
 * re-frames it for another viewing direction (unit vector from target to camera). Tries the
 * smallest turns around the target first; when none is clear, pulls the camera in along its sight
 * line past the facet in the way and lifts it over the floor clearance.
 */
export function clearPose(base, poseAlong, quads, floorZ) {
    if (quads.length === 0 || poseIsClear(base, quads, floorZ)) {
        return base;
    }
    const dir0 = base.position.clone().sub(base.target).normalize();
    const tries = [];
    for (const yaw of YAWS) {
        for (const pitch of PITCHES) {
            tries.push({ yaw, pitch, cost: Math.abs(yaw) + 1.2 * Math.abs(pitch) });
        }
    }
    tries.sort((p, q) => p.cost - q.cost);
    for (const t of tries) {
        if (t.yaw === 0 && t.pitch === 0) {
            continue;
        }
        const pose = poseAlong(turned(dir0, t.yaw, t.pitch));
        if (poseIsClear(pose, quads, floorZ)) {
            return pose;
        }
    }
    return pulledIn(base, quads, floorZ);
}

function pulledIn(base, quads, floorZ) {
    const position = base.position.clone();
    const t = nearestCrossing(quads, position, base.target);
    if (t != null) {
        const toTarget = base.target.clone().sub(position);
        const room = toTarget.length() - MIN_DISTANCE_MM;
        position.addScaledVector(toTarget.normalize(), Math.min(room, t + PULL_PAST_MM));
    }
    position.z = Math.max(position.z, floorZ + FLOOR_CLEARANCE_MM);
    return { position, target: base.target.clone() };
}
