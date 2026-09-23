// Hold outlines as thin lines lying on the facets, for the Photos mode of the 3D wall view: the
// rectified photo already shows the holds themselves, so it gets their traced outlines (and pocket
// holes) drawn over it instead of opaque slabs. One LineSegments per state (lit / dimmed), coloured
// per vertex, so the whole wall is two draw calls.
import * as THREE from '../lib/three/three.module.min.js';
import { holdColor, holdFrame, outlineOf } from './wall3d-holds.js';

function rings(h) {
    const holes = (h.shape && h.shape.holes ? h.shape.holes : []).filter(r => r.length >= 3);
    return [outlineOf(h), ...holes];
}

function segments(list, facets, lift, dimmed, sides) {
    const pos = [];
    const col = [];
    const slots = [];
    const p = new THREE.Vector3();
    for (const h of list) {
        const m = holdFrame(h, facets.get(h.facetId), lift);
        const c = holdColor(h, dimmed);
        const slot = sides.index.get(h.facetId) ?? 0;
        for (const ring of rings(h)) {
            for (let i = 0; i < ring.length; i++) {
                const q = ring[(i + 1) % ring.length];
                p.set(ring[i][0], ring[i][1], 0).applyMatrix4(m);
                pos.push(p.x, p.y, p.z);
                p.set(q[0], q[1], 0).applyMatrix4(m);
                pos.push(p.x, p.y, p.z);
                col.push(c.r, c.g, c.b, c.r, c.g, c.b);
                slots.push(slot, slot);
            }
        }
    }
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
    g.setAttribute('color', new THREE.Float32BufferAttribute(col, 3));
    g.setAttribute('facetIndex', new THREE.Float32BufferAttribute(slots, 1));
    return g;
}

/**
 * A group of two LineSegments (lit, dimmed) tracing every hold's outline `lift` mm above its facet.
 * `sides` (wall3d-sides.js) hides a facet's outlines while the camera is behind it.
 */
export function buildOutlines(lit, dim, facets, lift, sides) {
    const group = new THREE.Group();
    group.add(new THREE.LineSegments(segments(lit, facets, lift, false, sides),
        sides.cullBehind(new THREE.LineBasicMaterial({ vertexColors: true }))));
    if (dim.length) {
        group.add(new THREE.LineSegments(segments(dim, facets, lift, true, sides),
            sides.cullBehind(new THREE.LineBasicMaterial({ vertexColors: true, transparent: true, opacity: 0.45 }))));
    }
    group.visible = false;
    return group;
}
