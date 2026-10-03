// Front / back of the facets for the 3D wall view (wall3d.js). Everything painted ON a facet (hold
// slabs, Photos-mode outlines, photos, markers, labels, rings) belongs to its front, the side its
// normal points to. From behind, the wall is plain plywood: those layers must not show through,
// mirrored, and a tap must not pick a hold through the wall.
//
// The merged hold / outline geometries span every facet, so they carry a per-vertex `facetIndex`
// and their materials get a vertex-shader test against that facet's plane: a vertex whose facet the
// camera is behind is moved outside the clip volume, so the whole primitive is dropped before any
// fragment work. The camera position is one shared uniform, copied once per frame (`update`).
//
// Ghosting (wall3d-ghost.js): a facet between the camera and the orbit target is faded. Its layers
// are split by pass: the normal ('solid') material drops a ghosted facet's primitives and a faded
// twin ('ghost') draws only those; a per-facet flag array is the one extra uniform.
import * as THREE from '../lib/three/three.module.min.js';

/** Plane test slack (mm): the camera sitting exactly in a facet's plane counts as in front. */
const SIDE_EPSILON_MM = 0.5;

/**
 * Per-facet planes of the view: `index` (facet id → slot), `planes` (vec4 normal.xyz, normal·origin
 * per slot), `inFront(facet, camPos)` and the shared camera uniform (call `update(camera)` per frame).
 */
export function createFacetSides(facets) {
    const index = new Map();
    const planes = [];
    facets.forEach((f, i) => {
        index.set(f.id, i);
        const n = new THREE.Vector3(...f.normal);
        planes.push(new THREE.Vector4(n.x, n.y, n.z, n.dot(new THREE.Vector3(...f.origin))));
    });
    if (planes.length === 0) {
        planes.push(new THREE.Vector4(0, 0, 0, -1));       // never behind
    }
    const camUniform = { value: new THREE.Vector3() };
    const planeUniform = { value: planes };
    const ghostUniform = { value: planes.map(() => 0) };

    function inFront(facet, camPos) {
        const p = planes[index.get(facet.id)];
        return !p || p.x * camPos.x + p.y * camPos.y + p.z * camPos.z - p.w >= -SIDE_EPSILON_MM;
    }

    /**
     * Makes `material` drop the primitives of facets the camera is behind (needs a `facetIndex`
     * attribute). `pass`: 'all' draws every facet, 'solid' skips ghosted facets, 'ghost' only them.
     */
    function cullBehind(material, pass = 'all') {
        const count = planes.length;
        const ghostTest = pass === 'solid' ? ' || w3dGhost[int(facetIndex + 0.5)] > 0.5'
            : pass === 'ghost' ? ' || w3dGhost[int(facetIndex + 0.5)] < 0.5' : '';
        material.onBeforeCompile = shader => {
            shader.uniforms.w3dFacetPlanes = planeUniform;
            shader.uniforms.w3dCamPos = camUniform;
            shader.uniforms.w3dGhost = ghostUniform;
            shader.vertexShader = shader.vertexShader
                .replace('void main() {', `attribute float facetIndex;
uniform vec4 w3dFacetPlanes[${count}];
uniform vec3 w3dCamPos;
uniform float w3dGhost[${count}];
void main() {`)
                .replace(/}\s*$/, `\tvec4 w3dPlane = w3dFacetPlanes[int(facetIndex + 0.5)];
\tif (dot(w3dPlane.xyz, w3dCamPos) - w3dPlane.w < ${(-SIDE_EPSILON_MM).toFixed(1)}${ghostTest}) gl_Position = vec4(0.0, 0.0, 2.0, 1.0);
}`);
        };
        material.customProgramCacheKey = () => `w3d-facet-sides-${count}-${pass}`;
        material.needsUpdate = true;
        return material;
    }

    /** Marks the facets (ids) that draw as ghosts; the rest draw solid. */
    function setGhosted(ids) {
        ghostUniform.value = planes.map(() => 0);
        for (const id of ids) {
            const slot = index.get(id);
            if (slot != null) ghostUniform.value[slot] = 1;
        }
    }

    return {
        index,
        inFront,
        cullBehind,
        setGhosted,
        /** Copies the camera position into the shared uniform; once per frame, before rendering. */
        update(camera) { camUniform.value.setFromMatrixPosition(camera.matrixWorld); },
    };
}
