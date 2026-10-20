// "Show on wall": marks one spot of the 3D wall view (a volume or a proposed hold from the wall settings) with a
// ring and a pin standing out of the surface, and flies the camera in front of it. The spot arrives as
// options.highlight = { facetId, a, b, h, look? } (mm along the facet's u and v, h above its plane; look "under"
// frames the spot from below, see poseFor).
import * as THREE from '../lib/three/three.module.min.js';

const COLOR = 0xff2d55;
/** Camera distance from the spot along the facet normal, mm. */
const DISTANCE = 1800;

/** World point of the spot and the facet normal, or null when the facet is not in the view. */
function locate(view, spot) {
    const f = spot && view.facets.find(x => x.id === spot.facetId);
    if (!f) return null;
    const v = a => new THREE.Vector3(...a);
    const normal = v(f.normal).normalize();
    const base = v(f.origin).addScaledVector(v(f.u), spot.a).addScaledVector(v(f.v), spot.b);
    const height = Math.max(0, spot.h || 0);
    return { normal, base: base.clone(), height, point: base.addScaledVector(normal, height) };
}

/** Builds the marker (a flat ring at the spot and a pin above it); returns { group, point, normal } or null. */
export function buildLocator(view, spot) {
    const at = locate(view, spot);
    if (!at) return null;
    const mat = new THREE.MeshBasicMaterial({ color: COLOR, depthTest: false, transparent: true, opacity: 0.95, side: THREE.DoubleSide });
    const group = new THREE.Group();
    const ring = new THREE.Mesh(new THREE.RingGeometry(70, 95, 48), mat);
    const pin = new THREE.Mesh(new THREE.CylinderGeometry(6, 6, 260, 10), mat);
    const q = new THREE.Quaternion().setFromUnitVectors(new THREE.Vector3(0, 0, 1), at.normal);
    ring.quaternion.copy(q);
    pin.quaternion.copy(q).multiply(new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(1, 0, 0), Math.PI / 2));
    pin.position.copy(at.normal).multiplyScalar(130);
    ring.renderOrder = 999;
    pin.renderOrder = 999;
    group.add(ring, pin);
    group.position.copy(at.point);
    return { group, point: at.point, normal: at.normal, under: spot.look === 'under', base: at.base, height: at.height };
}

/** Camera height above the floor the "underneath" pose never goes below, mm. */
const UNDER_FLOOR_CLEARANCE = 250;
/** Nearest the "underneath" pose comes to a volume, mm. */
const UNDER_MIN_DISTANCE = 900;

/**
 * The camera pose that looks straight at the spot or, for a spot with look "under" (the hybrid zoom's
 * "Look underneath" on a volume), from in front of and below it up at its underside: aimed at half the
 * volume's height, out along the normal and down towards the floor (never below floorZ + clearance).
 */
export function poseFor(locator, floorZ) {
    if (!locator.under) {
        const out = locator.distance || DISTANCE;
        return { target: locator.point.clone(), position: locator.point.clone().addScaledVector(locator.normal, out) };
    }

    // Closer for a small volume (5× its depth, 0.9-1.8 m), about 60° below it, so its underside fills the view.
    const distance = Math.min(DISTANCE, Math.max(UNDER_MIN_DISTANCE, locator.height * 5));
    const target = locator.base.clone().addScaledVector(locator.normal, locator.height / 2);
    const position = target.clone().addScaledVector(locator.normal, distance * 0.45);
    position.z -= distance * 0.8;
    if (Number.isFinite(floorZ)) position.z = Math.max(position.z, floorZ + UNDER_FLOOR_CLEARANCE);
    return { target, position };
}

/**
 * Pins the orbit around a spot (the hybrid zoom's volume inset): the target stays on the spot (no pan), the
 * camera may swing `yawDeg` either side of the facet normal and `pitchDeg` above and below it, between
 * `minDistance` and `maxDistance` mm. OrbitControls measures its angles about camera.up, so the normal's own
 * azimuth and polar angle are found in that frame and the limits set around them.
 */
export function clampOrbit(controls, camera, locator, spec) {
    const deg = THREE.MathUtils.degToRad;
    const yaw = deg(spec.yawDeg ?? 60);
    const pitch = deg(spec.pitchDeg ?? 60);
    const toYUp = new THREE.Quaternion().setFromUnitVectors(camera.up.clone().normalize(), new THREE.Vector3(0, 1, 0));
    const front = new THREE.Spherical().setFromVector3(locator.normal.clone().applyQuaternion(toYUp));
    controls.enablePan = false;
    controls.zoomToCursor = false;
    controls.minAzimuthAngle = front.theta - yaw;
    controls.maxAzimuthAngle = front.theta + yaw;
    controls.minPolarAngle = Math.max(0.05, front.phi - pitch);
    controls.maxPolarAngle = Math.min(Math.PI - 0.05, front.phi + pitch);
    controls.minDistance = spec.minDistance ?? 300;
    controls.maxDistance = spec.maxDistance ?? 2500;
}
