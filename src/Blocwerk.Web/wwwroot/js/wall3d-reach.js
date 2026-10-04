// Where the camera of the 3D wall view (wall3d.js) may go: never into or behind the climbing surface.
// The camera is kept CAMERA_GAP_MM in front of the main facet's plane (not over the top of an overhang,
// not behind it), above the floor and out of the body block (wall3d-body.js) of every facet that carries
// holds; a camera inside one is pushed out through the nearest face. Closing panels, caps and the
// like are NOT obstacles to the camera: when one is in the way it is hidden instead (wall3d-ghost.js,
// wall3d-occlude.js), so the view never jumps closer because of them.
//
// Zoom: the orbit target is anchored on the wall surface the camera looks at. A preset frames the
// wall around a point that often floats behind it, and zooming to the cursor keeps the target at the
// old distance, so the camera flew through the wall and the splat's near fade (wall3d-splat-clip.js,
// relative to the target) faded the wall away before it came close. With the target on the surface,
// OrbitControls' minDistance (MIN_ZOOM_MM) is the closest the camera gets to the wall. Only facets
// with holds anchor the target: a bare panel standing in front would otherwise pull the orbit target
// (and with it the camera) onto itself.
import * as THREE from '../lib/three/three.module.min.js';
import { polygonDistance } from './wall3d-body.js';

/** Closest zoom: the camera stops this far from the surface it looks at. */
export const MIN_ZOOM_MM = 150;
const CAMERA_GAP_MM = 80;
const SIDE_PAD_MM = 40;
/** Nor below the floor (the mats' surface is ~33 cm up; a camera on them may look up). */
const FLOOR_GAP_MM = 200;
/** Nor above the ceiling cap (wall3d-body.js), nor this close under it. */
const CEILING_GAP_MM = 150;
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

/** The main facet's plane as the one half-space the camera stays in front of, everywhere. */
function roomLimits(prisms, main) {
    const wall = prisms.find(p => p.id === main?.id);
    return wall ? [{ n: wall.n, d: wall.d, until: Infinity }] : [];
}

/**
 * `pieces`: buildBody's pieces, `main`: the main facet, `floorZ`: the floor plane, `front`: the
 * horizontal direction toward the climber (wallFrame), `ceilingZ`: the ceiling cap, `solid`: ids of
 * the facets that carry holds (the only ones the camera keeps out of and anchors on). Returns
 * { minDistance, keepOut(position) → moved, anchor(position, target) → moved, step(camera, controls, tweening) }.
 */
export function createReach(pieces, main, floorZ, front, ceilingZ = null, solid = null) {
    const prisms = pieces.filter(p => !solid || solid.has(p.id)).map(prism);
    const limits = roomLimits(prisms, main);
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
