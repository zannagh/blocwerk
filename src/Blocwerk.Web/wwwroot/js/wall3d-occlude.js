// Hides what stands between the camera and the wall in the 3D wall view (wall3d.js), instead of pulling the
// camera closer. Each frame the camera moves, a handful of sight lines (camera -> orbit target, and
// camera -> a small grid over every facet that carries holds, about 30 in all, "aims" from wall3d-camera.js wallAims) is tested against the geometry that is not the climbing surface:
//   - the body (wall3d-body.js): the plywood blocks behind each facet, the ceiling / floor caps and the
//     ceiling lips. A block, cap or lip a sight line crosses is hidden (also drawn in photo-real, where the
//     body stays). The main facet's own block is never touched, and neither is any hold: holds are
//     separate geometry that these tests never see.
//   - volumes (wall3d-volumes.js): when the camera sees their facet at a grazing angle and a sight line
//     crosses their bounding box, they turn translucent (they carry holds, so they must not vanish).
//   - bare facets (no holds on them) are faded by wall3d-ghost.js on the same sight lines.
// A facet's block is also hidden whenever the camera is behind that facet's plane (a view from the side
// or the back), which no sampling of sight lines can miss.
// Cost: about 30 rays against a few dozen body triangles (three.js Raycaster, bounding-sphere first) and
// a handful of boxes; skipped altogether while the camera has not moved. No allocation per frame.
import * as THREE from '../lib/three/three.module.min.js';

const GHOST_OPACITY = 0.15;
/** A hit this close to a sight line's far end is the wall being looked at, not something in front of it. */
const END_SLACK_MM = 60;
/** A volume only counts as in the way when its facet is seen this edge-on (|cos| of view vs normal). */
const GRAZING_COS = 0.35;
const MOVE_EPSILON_MM = 1;

/**
 * `body`: buildBody's result, `volumes`: buildVolumes' result, `facets`: the view's facets, `sides`: createFacetSides,
 * `aims`: world points on the main facet, `wallId`: the main facet's id (its block is never hidden).
 * Returns { update(cameraPos, target, ghostIds) -> true when something changed, dispose() }.
 */
export function createOccluders({ body, volumes, facets, sides, aims, wallId }) {
    const raycaster = new THREE.Raycaster();
    const pieces = body.group.children;
    const normals = new Map(facets.map(f => [f.id, new THREE.Vector3(...f.normal)]));
    const facetById = new Map(facets.map(f => [f.id, f]));
    const volumeMeshes = [...volumes.plain.children, ...volumes.photo.children];
    for (const m of volumeMeshes) {
        m.geometry.computeBoundingBox();
        m.userData.solidMaterial = m.material;
    }

    const aimList = [null, ...aims];
    const dir = new THREE.Vector3();
    const ray = new THREE.Ray();
    const hit = new THREE.Vector3();
    const lastCam = new THREE.Vector3(Infinity, 0, 0);
    const lastTarget = new THREE.Vector3(Infinity, 0, 0);
    let key = '';
    let hiddenPieces = new Set();
    let translucent = new Set();
    let ghostKey = '';

    /** Whether any sight line from `cam` crosses mesh `m` short of its end. */
    function crossesMesh(m, cam) {
        for (const aim of aimList) {
            const len = dir.subVectors(aim, cam).length();
            if (len < 1) {
                continue;
            }

            raycaster.set(cam, dir.divideScalar(len));
            raycaster.far = len - END_SLACK_MM;
            if (raycaster.intersectObject(m, false).length > 0) {
                return true;
            }
        }

        return false;
    }

    function crossesBox(box, cam) {
        for (const aim of aimList) {
            const len = dir.subVectors(aim, cam).length();
            if (len < 1) {
                continue;
            }

            ray.set(cam, dir.divideScalar(len));
            if (ray.intersectBox(box, hit) && hit.distanceTo(cam) < len - END_SLACK_MM) {
                return true;
            }
        }

        return false;
    }

    function ghostMaterial(m) {
        m.userData.ghostMaterial ??= Object.assign(m.userData.solidMaterial.clone(), {
            transparent: true, opacity: GHOST_OPACITY, depthWrite: false,
        });
        return m.userData.ghostMaterial;
    }

    /** A facet's block (behind it) is in the way when the camera is behind that facet, or a sight line crosses it. */
    function pieceHidden(m, cam) {
        const id = m.userData.facetId;
        if (id === wallId) {
            return false;
        }

        return (id !== undefined && !sides.inFront(facetById.get(id), cam)) || crossesMesh(m, cam);
    }

    function volumeFaded(m, cam, view) {
        const n = normals.get(m.userData.facetId);
        return !!n && Math.abs(n.dot(view)) < GRAZING_COS && crossesBox(m.geometry.boundingBox, cam);
    }

    function apply(ghostIds) {
        const ghosted = new Set(ghostIds);
        for (const m of pieces) {
            m.visible = !hiddenPieces.has(m) && !(m.userData.facetId && ghosted.has(m.userData.facetId));
        }

        for (const m of volumeMeshes) {
            const faded = translucent.has(m);
            m.material = faded ? ghostMaterial(m) : m.userData.solidMaterial;
            for (const child of m.children) {
                child.visible = !faded;
            }
        }
    }

    return {
        /** Re-tests the sight lines when the camera or the ghosted set moved; true when the drawn set changed. */
        update(cam, target, ghostIds) {
            const nextGhost = ghostIds.join('|');
            const still = lastCam.distanceTo(cam) < MOVE_EPSILON_MM && lastTarget.distanceTo(target) < MOVE_EPSILON_MM;
            if (still && nextGhost === ghostKey) {
                return false;
            }

            lastCam.copy(cam);
            lastTarget.copy(target);
            aimList[0] = target;
            const view = dir.subVectors(target, cam).normalize().clone();
            const hide = new Set(pieces.filter(m => pieceHidden(m, cam)));
            const fade = new Set(volumeMeshes.filter(m => volumeFaded(m, cam, view)));
            const next = `${nextGhost}#${[...hide].map(m => pieces.indexOf(m)).join(',')}#${[...fade].map(m => volumeMeshes.indexOf(m)).join(',')}`;
            ghostKey = nextGhost;
            if (next === key) {
                return false;
            }

            key = next;
            hiddenPieces = hide;
            translucent = fade;
            apply(ghostIds);
            return true;
        },
        /** Pieces currently hidden and volumes currently translucent (tests, probes). */
        get state() {
            return { hidden: hiddenPieces.size, translucent: translucent.size };
        },
        dispose() {
            for (const m of volumeMeshes) {
                m.userData.ghostMaterial?.dispose();
            }
        },
    };
}
