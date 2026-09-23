// Hold picking of the 3D wall view (wall3d.js): a tap (not a drag) on a hold opens its card. Only a
// hold on the side of the wall the camera is on can be hit: the camera must be in front of the
// hold's facet, and no facet (front or plywood back) may sit between the camera and the hold.
import * as THREE from '../lib/three/three.module.min.js';

const TAP_SLOP_PX = 8;
/** A facet this much nearer than the hold's pick face is in the way (the face floats ~5–25 mm up). */
const OCCLUDE_MM = 1;

/**
 * Wires pointer taps on `canvas`. `onHold(hold)` / `onMiss()` get the result; `onDown()` runs on every
 * press. Returns `dispose()`.
 */
export function createPicker({ canvas, camera, holds, walls, sides, onHold, onMiss, onDown }) {
    const raycaster = new THREE.Raycaster();
    const camPos = new THREE.Vector3();
    let down = null;

    function holdAt(e) {
        const r = canvas.getBoundingClientRect();
        const ndc = new THREE.Vector2(((e.clientX - r.left) / r.width) * 2 - 1, -((e.clientY - r.top) / r.height) * 2 + 1);
        raycaster.setFromCamera(ndc, camera);
        const hit = raycaster.intersectObject(holds.pick, false)[0];
        const hold = holds.holdAt(hit);
        if (!hold) {
            return null;
        }
        camPos.setFromMatrixPosition(camera.matrixWorld);
        if (!sides.inFront(holds.facets.get(hold.facetId), camPos)) {
            return null;
        }
        const wall = raycaster.intersectObjects(walls, false)[0];
        return wall && wall.distance < hit.distance - OCCLUDE_MM ? null : hold;
    }

    const onPointerDown = e => { down = { x: e.clientX, y: e.clientY }; onDown(); };
    const onPointerUp = e => {
        if (!down || Math.hypot(e.clientX - down.x, e.clientY - down.y) > TAP_SLOP_PX) {
            down = null;
            return;
        }
        down = null;
        const hold = holdAt(e);
        if (hold) {
            onHold(hold);
        } else {
            onMiss();
        }
    };
    canvas.addEventListener('pointerdown', onPointerDown);
    canvas.addEventListener('pointerup', onPointerUp);
    return {
        dispose() {
            canvas.removeEventListener('pointerdown', onPointerDown);
            canvas.removeEventListener('pointerup', onPointerUp);
        },
    };
}
