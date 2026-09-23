// Hold picking of the 3D wall view (wall3d.js): a tap (not a drag) on a hold opens its card. Only a
// hold on the side of the wall the camera is on can be hit: the camera must be in front of the
// hold's facet, and no facet (front or plywood back) may sit between the camera and the hold; a
// facet ghosted out of the way (wall3d-ghost.js) does not count. Every mode picks the same way,
// photo-real included: the never-drawn pick outlines (wall3d-holds.js) are what a tap hits.
//
// A pick is { holdId, hold, facetId, point: [x, y, z] (world mm, on the hold's pick face),
// plane: { a, b } (mm in the facet's u / v frame) } — what a 3D boulder editor needs later.
import * as THREE from '../lib/three/three.module.min.js';

const TAP_SLOP_PX = 8;
/** A facet this much nearer than the hold's pick face is in the way (the face floats ~5–25 mm up). */
const OCCLUDE_MM = 1;

function pickOf(hold, facet, point) {
    const rel = point.clone().sub(new THREE.Vector3(...facet.origin));
    return {
        holdId: hold.id,
        hold,
        facetId: facet.id,
        point: point.toArray(),
        plane: { a: rel.dot(new THREE.Vector3(...facet.u)), b: rel.dot(new THREE.Vector3(...facet.v)) },
    };
}

/** Client coordinates of `hold`'s centre on `facet` as `camera` sees it; null when off screen. */
export function screenPointOf(hold, facet, camera, canvas) {
    if (!facet) {
        return null;
    }
    const p = new THREE.Vector3(...facet.origin).addScaledVector(new THREE.Vector3(...facet.u), hold.planeA)
        .addScaledVector(new THREE.Vector3(...facet.v), hold.planeB).project(camera);
    if (Math.abs(p.x) > 1 || Math.abs(p.y) > 1 || p.z > 1) {
        return null;
    }
    const r = canvas.getBoundingClientRect();
    return { x: r.left + ((p.x + 1) / 2) * r.width, y: r.top + ((1 - p.y) / 2) * r.height };
}

/**
 * Wires pointer taps on `canvas`. `onPick(pick)` / `onMiss()` get the result; `onDown()` runs on every
 * press. `walls()` returns the facet meshes a tap may not pass through. Returns
 * { pickAt(clientX, clientY) → pick | null, dispose() }.
 */
export function createPicker({ canvas, camera, holds, walls, sides, onPick, onMiss, onDown }) {
    const raycaster = new THREE.Raycaster();
    const camPos = new THREE.Vector3();
    let down = null;

    function pickAt(clientX, clientY) {
        const r = canvas.getBoundingClientRect();
        const ndc = new THREE.Vector2(((clientX - r.left) / r.width) * 2 - 1, -((clientY - r.top) / r.height) * 2 + 1);
        raycaster.setFromCamera(ndc, camera);
        const hit = raycaster.intersectObject(holds.pick, false)[0];
        const hold = holds.holdAt(hit);
        if (!hold) {
            return null;
        }
        const facet = holds.facets.get(hold.facetId);
        camPos.setFromMatrixPosition(camera.matrixWorld);
        if (!sides.inFront(facet, camPos)) {
            return null;
        }
        const wall = raycaster.intersectObjects(walls(), false)[0];
        return wall && wall.distance < hit.distance - OCCLUDE_MM ? null : pickOf(hold, facet, hit.point);
    }

    const onPointerDown = e => { down = { x: e.clientX, y: e.clientY }; onDown(); };
    const onPointerUp = e => {
        if (!down || Math.hypot(e.clientX - down.x, e.clientY - down.y) > TAP_SLOP_PX) {
            down = null;
            return;
        }
        down = null;
        const pick = pickAt(e.clientX, e.clientY);
        if (pick) {
            onPick(pick);
        } else {
            onMiss();
        }
    };
    canvas.addEventListener('pointerdown', onPointerDown);
    canvas.addEventListener('pointerup', onPointerUp);
    return {
        pickAt,
        dispose() {
            canvas.removeEventListener('pointerdown', onPointerDown);
            canvas.removeEventListener('pointerup', onPointerUp);
        },
    };
}
