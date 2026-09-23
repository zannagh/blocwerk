// Free-orbit ghosting of the 3D wall view (wall3d.js). Each frame the segment from the camera to the
// orbit target is tested against the facet quads (wall3d-clearance.js); a facet it passes through is
// faded to a see-through ghost: low opacity, no depth write, its edge outline kept. Its holds,
// outlines and markers switch to their faded twins (wall3d-sides.js), its photo fades too, it no
// longer blocks a hold tap, and in photo-real the splat around it fades (wall3d-splat-clip.js).
// Only facets actually in between are ghosted; nothing changes while the set stays the same.
import { blockersBetween } from './wall3d-clearance.js';

const GHOST_OPACITY = 0.12;
const PHOTO_GHOST_OPACITY = 0.15;

function ghostOf(material) {
    const m = material.clone();
    Object.assign(m, { transparent: true, opacity: GHOST_OPACITY, depthWrite: false });
    return m;
}

/**
 * `facets`: buildFacets' result, `textures`: the facet photo group, `quads`: facetQuads(view.facets),
 * `sides`: createFacetSides. Returns { update(cameraPos, target) → true when the set changed, ids,
 * quads (the ghosted ones), occluders() (the facet meshes a tap may not pass through), dispose() }.
 */
export function createGhosting({ facets, textures, quads, sides }) {
    const fronts = [...facets.meshes.values()];
    const solidFront = fronts[0]?.material;
    const solidBack = facets.backs[0]?.material;
    const ghostFront = solidFront ? ghostOf(solidFront) : null;
    const ghostBack = solidBack ? ghostOf(solidBack) : null;
    let ids = [];
    let key = '';

    function applyPhoto(mesh, ghosted) {
        const m = mesh.material;
        mesh.userData.solid ??= { transparent: m.transparent, opacity: m.opacity, depthWrite: m.depthWrite };
        const s = mesh.userData.solid;
        Object.assign(m, ghosted
            ? { transparent: true, opacity: PHOTO_GHOST_OPACITY, depthWrite: false }
            : s);
        m.needsUpdate = true;
    }

    function apply() {
        const set = new Set(ids);
        for (const mesh of fronts) {
            mesh.material = set.has(mesh.userData.facet.id) ? ghostFront : solidFront;
        }
        for (const mesh of facets.backs) {
            mesh.material = set.has(mesh.userData.facet.id) ? ghostBack : solidBack;
        }
        for (const mesh of textures.children) {
            const ghosted = set.has(mesh.userData.facetId);
            if (ghosted || mesh.userData.solid) {
                applyPhoto(mesh, ghosted);
            }
        }
        sides.setGhosted(ids);
    }

    return {
        get ids() { return ids; },
        get quads() { return quads.filter(q => ids.includes(q.id)); },
        /** Re-tests the sight line; true when the ghosted set changed (and was applied). */
        update(cameraPos, target) {
            const next = blockersBetween(quads, cameraPos, target);
            const nextKey = next.join('|');
            if (nextKey === key) {
                return false;
            }
            ids = next;
            key = nextKey;
            apply();
            return true;
        },
        /** Facet meshes (fronts and backs) that still block a tap: every one not ghosted. */
        occluders() {
            return [...fronts, ...facets.backs].filter(m => !ids.includes(m.userData.facet.id));
        },
        dispose() {
            ghostFront?.dispose();
            ghostBack?.dispose();
        },
    };
}
