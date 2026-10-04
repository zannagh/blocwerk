// Camera presets and tweens for the 3D wall view (wall3d.js). World is z-up, millimetres.
import * as THREE from '../lib/three/three.module.min.js';
import { v3 } from './wall3d-scene.js';
import { facetQuads } from './wall3d-clearance.js';
import { createReach } from './wall3d-reach.js';

export const PRESETS = ['front', 'below', 'left', 'right', 'top'];

const UP = new THREE.Vector3(0, 0, 1);

/**
 * The frame every preset is expressed in: the wall's bounding box, the floor height, the
 * "front" direction (horizontal, from the wall toward the climber — the main facet's normal
 * flattened) and the spot a climber stands on in front of it.
 */
export function wallFrame(view, facetGroup, body = {}) {
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
    // Facets with holds are the climbing surface: never hidden, never anchored through. The rest (closing
    // panels, caps) may be hidden when in the way (wall3d-ghost.js, wall3d-occlude.js).
    const holdFacets = new Set((view.holds || []).map(h => h.facetId));
    if (main) holdFacets.add(main.id);
    const bare = new Set(view.facets.filter(f => !holdFacets.has(f.id)).map(f => f.id));
    const reach = createReach(body.pieces || [], main, floorZ, front, body.ceilingZ, holdFacets);
    const climbing = view.facets.filter(f => holdFacets.has(f.id));
    const aims = climbing.length
        ? climbing.flatMap(f => (f.id === main?.id ? wallAims(f, 4, 3) : wallAims(f, 3, 2)))
        : [center.clone()];
    return { box, center, front, right, floorZ, stand, under, points, quads, reach, holdFacets, bare, aims, mainId: main?.id ?? null, radius: box.getBoundingSphere(new THREE.Sphere()).radius };
}

/**
 * World points spread over facet `f` (its extent sampled cols x rows, edges included) that the camera must
 * be able to see (wall3d-ghost.js, wall3d-occlude.js).
 */
export function wallAims(f, cols, rows) {
    const e = f.extent;
    const points = [];
    for (let i = 0; i < cols; i++) {
        for (let j = 0; j < rows; j++) {
            const a = e.aMin + (e.aMax - e.aMin) * i / (cols - 1);
            const b = e.bMin + (e.bMax - e.bMin) * j / (rows - 1);
            points.push(v3(f.origin).addScaledVector(v3(f.u), a).addScaledVector(v3(f.v), b));
        }
    }

    return points;
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
 * { position, target } for a named preset, framed into the stage's free area (see fitPose) and kept
 * clear of the floor and the wall (wall3d-reach.js). What stands between the camera and the wall is
 * not avoided: it is hidden (wall3d-ghost.js, wall3d-occlude.js).
 */
export function presetPose(name, frame, camera, insets, size) {
    const along = dir => fitPose(camera, frame.points, dir, insets, size);
    const pose = designedPose(name, frame, along);
    frame.reach?.keepOut(pose.position);
    return pose;
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
            // From high up in front, under the wall's top edge: above it is the ceiling (the capture
            // has only floaters there) and over an overhang there is no room at all.
            return underCeiling(along(front.clone().addScaledVector(UP, 0.4)), frame.box.max.z - CEILING_GAP_MM);
        default: // front, from about eye height
            return along(front.clone().addScaledVector(UP, 0.1));
    }
}

/** The Top preset's camera stays this far below the wall's highest edge. */
const CEILING_GAP_MM = 300;

/** Keeps no more than this share of the framed distance when the camera comes down under the ceiling. */
const MIN_KEPT_DISTANCE = 0.6;

/**
 * Brings the camera down to `maxZ`: slides it toward its target, or, where that would come too close
 * (a narrow phone frames from far away), keeps MIN_KEPT_DISTANCE of the distance and looks down less.
 */
function underCeiling(pose, maxZ) {
    const p = pose.position, t = pose.target;
    if (p.z <= maxZ || p.z <= t.z) return pose;
    const k = (p.z - maxZ) / (p.z - t.z);
    if (k <= 1 - MIN_KEPT_DISTANCE) {
        p.lerp(t, k);
        return pose;
    }
    const keep = MIN_KEPT_DISTANCE * p.distanceTo(t);
    const flat = p.clone().sub(t).setZ(0).normalize();
    const rise = Math.min(maxZ - t.z, keep);
    p.copy(t).addScaledVector(flat, Math.sqrt(keep * keep - rise * rise)).setZ(t.z + rise);
    return pose;
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
