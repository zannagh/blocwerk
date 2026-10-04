// "Show on wall": marks one spot of the 3D wall view (a volume or a proposed hold from the wall settings) with a
// ring and a pin standing out of the surface, and flies the camera in front of it. The spot arrives as
// options.highlight = { facetId, a, b, h } (mm along the facet's u and v, h above its plane).
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
    return { normal, point: base.addScaledVector(normal, Math.max(0, spot.h || 0)) };
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
    return { group, point: at.point, normal: at.normal };
}

/** The camera pose that looks straight at the spot. */
export function poseFor(locator) {
    return { target: locator.point.clone(), position: locator.point.clone().addScaledVector(locator.normal, DISTANCE) };
}
