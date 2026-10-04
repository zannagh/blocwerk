// Keyboard route to the holds of the 3D wall view (wall3d.js, wall3d-keys.js): which holds the
// cycling keys visit, in which order, and how the current one is spoken. A boulder's view cycles that
// boulder's holds bottom to top; the wall view cycles the holds the camera can see, row by row.
import * as THREE from '../lib/three/three.module.min.js';
import { screenPointOf } from './wall3d-pick.js';

/** Holds this close (px) to the same height read as one row when sorting by screen position. */
const ROW_PX = 48;
const ROLE_WORDS = { Start: 'start', Top: 'top', Hand: 'hand', Foot: 'foot', ColorFoot: 'foot' };

/** World point (mm) of a hold's centre on its facet. */
export function holdWorld(hold, facet) {
    return new THREE.Vector3(...facet.origin).addScaledVector(new THREE.Vector3(...facet.u), hold.planeA)
        .addScaledVector(new THREE.Vector3(...facet.v), hold.planeB);
}

const metres = mm => `${(Math.max(0, mm) / 1000).toFixed(1)} m`;

/**
 * `holds`: buildHolds' result, `picker`: createPicker's, `boulder`: whether the view highlights one
 * boulder, `floorZ` / `right`: the wall frame's floor height and the climber's rightward direction.
 * Returns { list(), onScreen(hold), describe(hold, index, total) }.
 */
export function createHoldNav({ holds, picker, camera, canvas, boulder, floorZ, right }) {
    const screen = hold => screenPointOf(hold, holds.facets.get(hold.facetId), camera, canvas);
    const seen = hold => {
        const p = screen(hold);
        return p && picker.pickAt(p.x, p.y)?.holdId === hold.id ? p : null;
    };

    return {
        /** The holds to cycle through, in reading order. */
        list() {
            if (boulder) {
                const z = h => holdWorld(h, holds.facets.get(h.facetId)).z;
                return [...holds.litHolds].sort((p, q) => z(p) - z(q));
            }
            return holds.all.map(h => ({ h, p: seen(h) })).filter(x => x.p)
                .sort((p, q) => Math.round(p.p.y / ROW_PX) - Math.round(q.p.y / ROW_PX) || p.p.x - q.p.x)
                .map(x => x.h);
        },
        onScreen: hold => !!screen(hold),
        /** "3 of 12: blue hand hold, 1.4 m above the floor, 0.6 m from the panel's left edge". */
        describe(hold, index, total) {
            const facet = holds.facets.get(hold.facetId);
            const kind = ROLE_WORDS[hold.role] || (hold.isFoot ? 'foot' : 'hand');
            const up = holdWorld(hold, facet).z - floorZ;
            const e = facet.extent;
            const rightward = new THREE.Vector3(...facet.u).dot(right) >= 0;
            const across = rightward ? hold.planeA - e.aMin : e.aMax - hold.planeA;
            return `${index + 1} of ${total}: ${hold.colorName} ${kind} hold, ${metres(up)} above the floor, `
                + `${metres(across)} from the panel's left edge`;
        },
    };
}
