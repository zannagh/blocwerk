// Hold outlines as thin lines lying on the facets, for the Photos mode of the 3D wall view: the
// rectified photo already shows the holds themselves, so it gets their traced outlines (and pocket
// holes) drawn over it instead of opaque slabs. One LineSegments per state (lit / dimmed), coloured
// per vertex, so the whole wall is two draw calls (plus the faded twin of a ghosted facet's lines).
// Photo-real shows them RAISED over the splat instead (wall3d-relief.js, wall3d-overlay.js): the
// photo is flat, so its outlines stay on the facet, but the splat has the holds' real relief.
import * as THREE from '../lib/three/three.module.min.js';
import { holdColor, holdFrame, outlineOf, volumeBase, volumeShift } from './wall3d-holds.js';
import { raisedSegments } from './wall3d-relief.js';

function rings(h) {
    const holes = (h.shape && h.shape.holes ? h.shape.holes : []).filter(r => r.length >= 3);
    return [outlineOf(h), ...holes];
}

const centroid = ring => ring.reduce((s, p) => [s[0] + p[0] / ring.length, s[1] + p[1] / ring.length], [0, 0]);

/**
 * Photos mode: the hold where its facet photo shows it (`photoOutline`, from the texture's source-view
 * map: a protruding hold is seen from the photo that painted it), its pocket holes moved along; the
 * traced shape when the payload has none (older textures).
 */
function photoRings(h) {
    const o = h.photoOutline;
    if (!o || o.length < 3) return rings(h);
    const [c0, c1] = [centroid(outlineOf(h)), centroid(o)];
    const [da, db] = [c1[0] - c0[0], c1[1] - c0[1]];
    return [o, ...rings(h).slice(1).map(r => r.map(([a, b]) => [a + da, b + db]))];
}

function segments(list, facets, lift, dimmed, sides) {
    const pos = [];
    const col = [];
    const slots = [];
    const p = new THREE.Vector3();
    for (const h of list) {
        // A hold on a detected volume: on the textured volume, where the photo now shows it.
        const m = holdFrame(h, facets.get(h.facetId), lift + volumeBase(h), volumeShift(h));
        const c = holdColor(h, dimmed);
        const slot = sides.index.get(h.facetId) ?? 0;
        for (const ring of photoRings(h)) {
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
 * The outline layer: `flat` (two LineSegments, lit / dimmed, `lift` mm above the facets: Photos mode)
 * and `raised` (cap contours and faint collars at each hold's relief: photo-real), one showing at a
 * time through `group.userData.setRaised(on)`. `sides` (wall3d-sides.js) hides a facet's outlines
 * while the camera is behind it.
 */
export function buildOutlines(lit, dim, facets, lift, sides) {
    const group = new THREE.Group();
    // Transparent, drawn after the photo-real splat, so the outlines lie over it (wall3d-overlay.js).
    const line = (geo, opacity, pass) => {
        const l = new THREE.LineSegments(geo, sides.cullBehind(
            new THREE.LineBasicMaterial({ vertexColors: true, transparent: true, opacity, depthWrite: opacity >= 1 }), pass));
        l.renderOrder = 5;
        return l;
    };
    const flat = new THREE.Group();
    const litGeo = segments(lit, facets, lift, false, sides);
    flat.add(line(litGeo, 1, 'solid'), line(litGeo, 0.3, 'ghost'));
    if (dim.length) {
        flat.add(line(segments(dim, facets, lift, true, sides), 0.45, 'all'));
    }
    const raised = new THREE.Group();
    const frameOf = (h, l, shift) => holdFrame(h, facets.get(h.facetId), l, shift);
    const slotOf = h => sides.index.get(h.facetId) ?? 0;
    const up = raisedSegments(lit, frameOf, h => holdColor(h, false), slotOf, rings);
    raised.add(line(up.cap, 1, 'solid'), line(up.collar, 0.4, 'solid'), line(up.cap, 0.3, 'ghost'));
    if (dim.length) {
        const down = raisedSegments(dim, frameOf, h => holdColor(h, true), slotOf, rings);
        raised.add(line(down.cap, 0.45, 'all'), line(down.collar, 0.2, 'all'));
    }
    raised.visible = false;
    group.add(flat, raised);
    group.userData.setRaised = on => {
        flat.visible = !on;
        raised.visible = on;
    };
    group.visible = false;
    return group;
}
