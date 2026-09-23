// Camera presets and tweens for the 3D wall view (wall3d.js). World is z-up, millimetres.
import * as THREE from '../lib/three/three.module.min.js';
import { v3 } from './wall3d-scene.js';
import { clearPose, facetQuads } from './wall3d-clearance.js';

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
    // What the presets frame: the facets' real corners (the axis-aligned box of a leaning wall is
    // mostly air, and fitting its corners left the wall a third of the frame on a phone).
    const points = view.facets.flatMap(f => (f.corners || []).map(c => v3(c)));
    if (points.length < 2) {
        for (const x of [box.min.x, box.max.x]) for (const y of [box.min.y, box.max.y]) for (const z of [box.min.z, box.max.z]) points.push(new THREE.Vector3(x, y, z));
    }
    // floorZ is the lowest facet edge (the kickboard's bottom ≈ the mats' top): the floor plane.
    const quads = facetQuads(view.facets);
    return { box, center, front, right, floorZ, stand, under, points, quads, radius: box.getBoundingSphere(new THREE.Sphere()).radius };
}

function area(f) {
    return (f.extent.aMax - f.extent.aMin) * (f.extent.bMax - f.extent.bMin);
}

/** Share of the free area left empty around the fitted wall, per side. */
const FIT_MARGIN = 0.04;

/**
 * Camera pose looking along -`dir` that shows every frame point inside the stage's free area:
 * `insets` (px: top, bottom, left, right) are the overlay chrome to keep clear, `size` the stage in
 * px. Solved exactly for a perspective camera: the closest distance at which the points' spread fits
 * both the width and the height (whichever binds for the stage's aspect), then the target is slid
 * sideways / up so the wall sits centred in the free area rather than in the whole canvas.
 */
export function fitPose(camera, points, dir, insets = {}, size = null) {
    const w = size?.w || 1, h = size?.h || 1;
    const tanV = Math.tan(THREE.MathUtils.degToRad(camera.fov) / 2);
    const tanH = tanV * camera.aspect;
    // Free area as NDC bounds, then as tangents of the view angle.
    const ndc = (inset, full) => Math.max(0.2, 1 - (2 * (inset || 0)) / full - 2 * FIT_MARGIN);
    const hiY = tanV * ndc(insets.top, h), loY = -tanV * ndc(insets.bottom, h);
    const hiX = tanH * ndc(insets.right, w), loX = -tanH * ndc(insets.left, w);

    const back = dir.clone().normalize();
    const side = new THREE.Vector3().crossVectors(UP, back);
    if (side.lengthSq() < 1e-6) side.set(1, 0, 0);
    side.normalize();
    const up = new THREE.Vector3().crossVectors(back, side);
    const origin = points.reduce((acc, p) => acc.add(p), new THREE.Vector3()).divideScalar(points.length);
    const rel = points.map(p => p.clone().sub(origin));
    const a = rel.map(p => p.dot(side)), b = rel.map(p => p.dot(up)), q = rel.map(p => p.dot(back));

    // Point i (lateral x_i - s, depth d - q_i) is in view when lo·(d - q_i) ≤ x_i - s ≤ hi·(d - q_i);
    // a shift s exists for every pair (i, j) once d ≥ (x_i - x_j + hi·q_i - lo·q_j) / (hi - lo).
    let d = 0;
    for (let i = 0; i < rel.length; i++) {
        for (let j = 0; j < rel.length; j++) {
            d = Math.max(d, (a[i] - a[j] + hiX * q[i] - loX * q[j]) / (hiX - loX),
                (b[i] - b[j] + hiY * q[i] - loY * q[j]) / (hiY - loY));
        }
    }
    d = Math.max(d, Math.max(...q) + camera.near * 2);
    const shift = (x, hi, lo) => {
        let min = -Infinity, max = Infinity;
        x.forEach((xi, i) => {
            min = Math.max(min, xi - hi * (d - q[i]));
            max = Math.min(max, xi - lo * (d - q[i]));
        });
        return (min + max) / 2;
    };
    const target = origin.clone().addScaledVector(side, shift(a, hiX, loX)).addScaledVector(up, shift(b, hiY, loY));
    return { position: target.clone().addScaledVector(back, d), target };
}

/**
 * { position, target } for a named preset, framed into the stage's free area (see fitPose) and
 * moved out of the way of facets (wall3d-clearance.js): a camera behind a side piece, under the
 * floor clearance or with a facet between it and the wall turns around the target until clear.
 */
export function presetPose(name, frame, camera, insets, size) {
    const along = dir => fitPose(camera, frame.points, dir, insets, size);
    const base = designedPose(name, frame, along);
    const distance = base.position.distanceTo(base.target);
    // 'below' is placed, not fitted: turning it keeps its distance to the target.
    const poseAlong = name === 'below'
        ? dir => ({ target: base.target.clone(), position: base.target.clone().addScaledVector(dir, distance) })
        : along;
    return clearPose(base, poseAlong, frame.quads || [], frame.floorZ);
}

function designedPose(name, frame, along) {
    const { center, front, right, floorZ, under } = frame;
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
            return along(right.clone().multiplyScalar(-0.8).addScaledVector(front, 0.6).addScaledVector(UP, 0.15));
        case 'right':
            return along(right.clone().multiplyScalar(0.8).addScaledVector(front, 0.6).addScaledVector(UP, 0.15));
        case 'top':
            // Not exactly overhead: OrbitControls degenerates at the pole.
            return along(UP.clone().addScaledVector(front, 0.25));
        default: // front, from about eye height
            return along(front.clone().addScaledVector(UP, 0.1));
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
