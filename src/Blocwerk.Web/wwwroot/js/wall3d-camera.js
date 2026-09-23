// Camera presets and tweens for the 3D wall view (wall3d.js). World is z-up, millimetres.
import * as THREE from '../lib/three/three.module.min.js';
import { v3 } from './wall3d-scene.js';

export const PRESETS = ['front', 'below', 'left', 'right', 'top'];

const UP = new THREE.Vector3(0, 0, 1);

/**
 * The frame every preset is expressed in: the wall's bounding box, the floor height, the
 * "front" direction (horizontal, from the wall toward the climber — the main facet's normal
 * flattened) and the spot a climber stands on in front of it.
 */
export function wallFrame(view, facetGroup) {
    const box = new THREE.Box3().setFromObject(facetGroup);
    if (box.isEmpty()) box.set(new THREE.Vector3(-1000, -1000, 0), new THREE.Vector3(1000, 1000, 2000));
    const main = view.facets.find(f => f.id === '0')
        || [...view.facets].sort((p, q) => area(q) - area(p))[0];
    const front = main ? v3(main.normal).setZ(0) : new THREE.Vector3(0, -1, 0);
    if (front.lengthSq() < 1e-6) front.set(0, -1, 0);
    front.normalize();
    const right = new THREE.Vector3().crossVectors(UP, front).normalize();
    const floorZ = box.min.z;
    const center = box.getCenter(new THREE.Vector3());
    // Stand 1.5 m out from the lowest point of the main facet's bottom edge, on the floor.
    let stand = center.clone();
    let under = center.clone();
    if (main) {
        const e = main.extent;
        const bottom = v3(main.origin).addScaledVector(v3(main.u), (e.aMin + e.aMax) / 2).addScaledVector(v3(main.v), e.bMin);
        const top = v3(main.origin).addScaledVector(v3(main.u), (e.aMin + e.aMax) / 2).addScaledVector(v3(main.v), e.bMax);
        // Under an overhang the top edge sticks out further than the bottom; stand clear of both.
        const out = Math.max(bottom.dot(front), top.dot(front));
        // Off to the left quarter, so the figure never blocks the straight-on view.
        stand = bottom.clone()
            .addScaledVector(v3(main.u), -(e.aMax - e.aMin) * 0.3)
            .addScaledVector(front, out - bottom.dot(front) + 1200);
        // Crouched a step outside the overhang's lip, where the whole underside is in view.
        under = bottom.clone().addScaledVector(front, out - bottom.dot(front) + 2600);
    }
    stand.z = floorZ;
    under.z = floorZ;
    return { box, center, front, right, floorZ, stand, under, radius: box.getBoundingSphere(new THREE.Sphere()).radius };
}

function area(f) {
    return (f.extent.aMax - f.extent.aMin) * (f.extent.bMax - f.extent.bMin);
}

/**
 * Smallest distance along `dir` (target → camera) at which every corner of the wall's box is
 * inside the frustum, plus a small margin for the overlay chrome.
 */
function fitDistance(camera, box, target, dir) {
    const vfov = THREE.MathUtils.degToRad(camera.fov);
    const tanV = Math.tan(vfov / 2) * 0.9;
    const tanH = tanV * camera.aspect;
    const back = dir.clone().normalize();
    const side = new THREE.Vector3().crossVectors(UP, back);
    if (side.lengthSq() < 1e-6) side.set(1, 0, 0);
    side.normalize();
    const up = new THREE.Vector3().crossVectors(back, side);
    let d = 0;
    for (const x of [box.min.x, box.max.x]) for (const y of [box.min.y, box.max.y]) for (const z of [box.min.z, box.max.z]) {
        const p = new THREE.Vector3(x, y, z).sub(target);
        const depth = p.dot(back);
        d = Math.max(d, depth + Math.abs(p.dot(side)) / tanH, depth + Math.abs(p.dot(up)) / tanV);
    }
    return d;
}

/** { position, target } for a named preset. */
export function presetPose(name, frame, camera) {
    const { center, front, right, floorZ, under, box } = frame;
    const target = center.clone();
    const along = dir => target.clone().addScaledVector(dir, fitDistance(camera, box, target, dir));
    switch (name) {
        case 'below': {
            // Crouch on the mat at the overhang's lip and look up into it.
            const p = under.clone();
            p.z = floorZ + 600;
            const t = center.clone().addScaledVector(front, -0.15 * (center.clone().sub(p).dot(front)));
            t.x = p.x = center.x;
            return { position: p, target: t };
        }
        case 'left':
            return { position: along(right.clone().multiplyScalar(-0.8).addScaledVector(front, 0.6).addScaledVector(UP, 0.15)), target };
        case 'right':
            return { position: along(right.clone().multiplyScalar(0.8).addScaledVector(front, 0.6).addScaledVector(UP, 0.15)), target };
        case 'top':
            // Not exactly overhead: OrbitControls degenerates at the pole.
            return { position: along(UP.clone().addScaledVector(front, 0.25)), target };
        default: // front, from about eye height
            return { position: along(front.clone().addScaledVector(UP, 0.1)), target };
    }
}

const ease = t => (t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2);

/**
 * Smooth camera moves. `step(now)` advances the running tween and reports whether one is
 * active; `cancel()` stops it (a user grabbing the view wins over an animation).
 */
export function createTweener(camera, controls, reducedMotion) {
    let tween = null;
    return {
        to(pose, ms = 750) {
            if (reducedMotion()) {
                camera.position.copy(pose.position);
                controls.target.copy(pose.target);
                controls.update();
                tween = null;
                return;
            }
            tween = {
                p0: camera.position.clone(), t0: controls.target.clone(),
                p1: pose.position.clone(), t1: pose.target.clone(), start: performance.now(), ms,
            };
        },
        step(now) {
            if (!tween) return false;
            const k = Math.min(1, (now - tween.start) / tween.ms);
            const e = ease(k);
            camera.position.lerpVectors(tween.p0, tween.p1, e);
            controls.target.lerpVectors(tween.t0, tween.t1, e);
            if (k >= 1) tween = null;
            return true;
        },
        cancel() { tween = null; },
        get active() { return !!tween; },
    };
}
