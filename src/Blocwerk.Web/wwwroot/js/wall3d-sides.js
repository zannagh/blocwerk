// Front / back of the facets for the 3D wall view (wall3d.js). Everything painted ON a facet (hold
// slabs, Photos-mode outlines, photos, markers, labels, rings) belongs to its front, the side its
// normal points to. From behind, the wall is plain plywood: those layers must not show through,
// mirrored, and a tap must not pick a hold through the wall.
//
// The merged hold / outline geometries span every facet, so they carry a per-vertex `facetIndex`
// and their materials get a vertex-shader test against that facet's plane: a vertex whose facet the
// camera is behind is moved outside the clip volume, so the whole primitive is dropped before any
// fragment work. The camera position is one shared uniform, copied once per frame (`update`).
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

    function inFront(facet, camPos) {
        const p = planes[index.get(facet.id)];
        return !p || p.x * camPos.x + p.y * camPos.y + p.z * camPos.z - p.w >= -SIDE_EPSILON_MM;
    }

    /** Makes `material` drop the primitives of facets the camera is behind (needs a `facetIndex` attribute). */
    function cullBehind(material) {
        const count = planes.length;
        material.onBeforeCompile = shader => {
            shader.uniforms.w3dFacetPlanes = planeUniform;
            shader.uniforms.w3dCamPos = camUniform;
            shader.vertexShader = shader.vertexShader
                .replace('void main() {', `attribute float facetIndex;
uniform vec4 w3dFacetPlanes[${count}];
uniform vec3 w3dCamPos;
void main() {`)
                .replace(/}\s*$/, `\tvec4 w3dPlane = w3dFacetPlanes[int(facetIndex + 0.5)];
\tif (dot(w3dPlane.xyz, w3dCamPos) - w3dPlane.w < ${(-SIDE_EPSILON_MM).toFixed(1)}) gl_Position = vec4(0.0, 0.0, 2.0, 1.0);
}`);
        };
        material.customProgramCacheKey = () => `w3d-facet-sides-${count}`;
        material.needsUpdate = true;
        return material;
    }

    return {
        index,
        inFront,
        cullBehind,
        /** Copies the camera position into the shared uniform; once per frame, before rendering. */
        update(camera) { camUniform.value.setFromMatrixPosition(camera.matrixWorld); },
    };
}
