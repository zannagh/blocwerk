// Where the camera of the 3D wall view (wall3d.js) may go: as in the real room, never into or behind
// the wall. The camera is kept CAMERA_GAP_MM in front of the main facet's plane (not over the top of an
// overhang, not behind it) and of the side panels bounding the room, above the floor and out of every
// body block (wall3d-body.js), each taken as reaching back without end and down to the floor; a camera
// inside one is pushed out through the nearest face (the front, or a side it came in by).
//
// Zoom: the orbit target is anchored on the wall surface the camera looks at. A preset frames the
// wall around a point that often floats behind it, and zooming to the cursor keeps the target at the
// old distance, so the camera flew through the wall and the splat's near fade (wall3d-splat-clip.js,
// relative to the target) faded the wall away before it came close. With the target on the surface,
// OrbitControls' minDistance (MIN_ZOOM_MM) is the closest the camera gets to the wall.
import * as THREE from '../lib/three/three.module.min.js';
import { pointOf, polygonDistance } from './wall3d-body.js';

/** Closest zoom: the camera stops this far from the surface it looks at. */
export const MIN_ZOOM_MM = 150;
const CAMERA_GAP_MM = 80;
const SIDE_PAD_MM = 40;
/** Nor below the floor (the mats' surface is ~33 cm up; a camera on them may look up). */
const FLOOR_GAP_MM = 200;
/** Nor above the ceiling cap (wall3d-body.js), nor this close under it. */
const CEILING_GAP_MM = 150;
const SIDE_REACH_MM = 600;
/** A target this much behind the surface on its sight line moves onto it. */
const ANCHOR_SLACK_MM = 5;

function prism(p) {
    const edges = p.outline.map((a, i) => {
        const b = p.outline[(i + 1) % p.outline.length];
        const ex = b[0] - a[0], ey = b[1] - a[1], len = Math.hypot(ex, ey) || 1;
        const out2 = [ey / len, -ex / len];
        return { a, out2, out: p.u.clone().multiplyScalar(out2[0]).addScaledVector(p.v, out2[1]) };
    });
    // A bottom edge reaches down to the floor: nothing slips under a side panel or a kickboard.
    return { ...p, edges: edges.filter(e => e.out.z > -0.9), d: p.n.dot(p.o) };
}

const local = (q, pos) => {
    const d = pos.clone().sub(q.o);
    return [d.dot(q.u), d.dot(q.v), d.dot(q.n)];
};

/** Pushes `pos` out of prism `q` through its nearest face; true when it moved. */
function pushOut(q, pos) {
    const [a, b, h] = local(q, pos);
    if (h >= CAMERA_GAP_MM) return false;
    let best = CAMERA_GAP_MM - h, dir = q.n;
    for (const e of q.edges) {
        const inside = -((a - e.a[0]) * e.out2[0] + (b - e.a[1]) * e.out2[1]) + SIDE_PAD_MM;
        if (inside <= 0) return false;
        if (inside < best) {
            best = inside;
            dir = e.out;
        }
    }
    pos.addScaledVector(dir, best + 0.5);
    return true;
}

/**
 * The room's walls as half-spaces the camera stays in front of: the main facet's plane everywhere, and
 * each side panel that has the main facet in front of it (The Attic's side wall, not the slot's side
 * facing away) alongside the wall, up to SIDE_REACH_MM out in front of the panel's front edge.
 */
function roomLimits(prisms, main, front) {
    const wall = prisms.find(p => p.id === main?.id);
    if (!wall) return [];
    const centre = wall.outline.reduce((s, [a, b]) => s.add(pointOf(wall, a, b)), new THREE.Vector3()).divideScalar(wall.outline.length);
    const sides = prisms.filter(p => Math.abs(p.n.z) < 0.3 && Math.abs(p.n.dot(front)) < 0.5 && p.n.dot(centre) - p.d > 0);
    return [{ n: wall.n, d: wall.d, until: Infinity }, ...sides.map(p => ({
        n: p.n, d: p.d, until: Math.max(...p.outline.map(([a, b]) => pointOf(p, a, b).dot(front))) + SIDE_REACH_MM,
    }))];
}

/**
 * `pieces`: buildBody's pieces, `main`: the main facet, `floorZ`: the floor plane, `front`: the
 * horizontal direction toward the climber (wallFrame), `ceilingZ`: the ceiling cap. Returns
 * { minDistance, keepOut(position) → moved, anchor(position, target) → moved, step(camera, controls, tweening) }.
 */
export function createReach(pieces, main, floorZ, front, ceilingZ = null) {
    const prisms = pieces.map(prism);
    const limits = roomLimits(prisms, main, front);
    const ray = new THREE.Ray();
    const dir = new THREE.Vector3();
    const hit = new THREE.Vector3();

    function keepOut(pos) {
        let moved = false;
        for (let pass = 0; pass < 4; pass++) {
            let any = false;
            for (const l of limits) {
                const h = l.n.dot(pos) - l.d;
                if (h < CAMERA_GAP_MM && pos.dot(front) < l.until) {
                    pos.addScaledVector(l.n, CAMERA_GAP_MM - h);
                    any = true;
                }
            }
            for (const q of prisms) any = pushOut(q, pos) || any;
            if (ceilingZ != null && pos.z > ceilingZ - CEILING_GAP_MM) {
                pos.z = ceilingZ - CEILING_GAP_MM;
                any = true;
            }
            if (floorZ != null && pos.z < floorZ + FLOOR_GAP_MM) {
                pos.z = floorZ + FLOOR_GAP_MM;
                any = true;
            }
            moved = moved || any;
            if (!any) break;
        }
        return moved;
    }

    /** Distance along the ray to the first facet surface the camera faces, or null. */
    function surfaceDistance(pos) {
        let best = null;
        for (const q of prisms) {
            if (q.n.dot(pos) - q.d <= 0 || !ray.intersectPlane(new THREE.Plane(q.n, -q.d), hit)) continue;
            const [a, b] = local(q, hit);
            if (polygonDistance(q.outline, [a, b]) > 0) continue;
            const t = hit.distanceTo(pos);
            if (best == null || t < best) best = t;
        }
        return best;
    }

    /** Moves `target` forward onto the first surface on the sight line when it lies behind it. */
    function anchor(pos, target) {
        dir.subVectors(target, pos);
        const dist = dir.length();
        if (dist < 1e-3) return false;
        ray.set(pos, dir.divideScalar(dist));
        const t = surfaceDistance(pos);
        if (t == null || t >= dist - ANCHOR_SLACK_MM) return false;
        target.copy(pos).addScaledVector(dir, t);
        return true;
    }

    return {
        minDistance: MIN_ZOOM_MM,
        keepOut,
        anchor,
        /** Updates the controls, keeps the camera out of the wall and the target on it; true while moving. */
        step(camera, controls, tweening) {
            const moving = controls.update();
            const pushed = keepOut(camera.position);
            if (pushed) camera.lookAt(controls.target);
            if (!tweening) anchor(camera.position, controls.target);
            return moving || pushed;
        },
    };
}
