// Keeps the photo-real splat (wall3d-splat.js) out of the camera's way. The mats, the floor and the
// side structures are part of the capture, not of the modelled facets, so they are faded in Spark's
// own splat vertex shader, per splat and after its frustum cull (a few multiply-adds, no re-sort and
// no regeneration, so it stays cheap on phones):
//   near fade   — splats within ~0.5–1 m of the camera fade out (closer when zoomed in);
//   floor       — splats below the mats' top (floor plane + ~33 cm) vanish when they sit between
//                 the camera and a facet (the ray through them hits a facet behind them), and all of
//                 them while the camera is low (crouched on the mats, the Below preset);
//   ghosts      — splats within a slab around a facet ghosted in free orbit (wall3d-ghost.js) fade
//                 like the facet does.
// Spark 2.2's SplatEdit SDFs could hide boxes too, but every edit change regenerates the whole splat
// set; this runs in the draw instead. The injection goes into the SparkRenderer material's source
// (already the iOS-patched one, tools/vendor/patch-spark-ios.py) before its first compile; the
// splat draws without depth test so the overlay's depth pre-pass (wall3d-overlay.js) cannot cut it.
import * as THREE from '../lib/three/three.module.min.js';
import { FLOOR_CLEARANCE_MM } from './wall3d-clearance.js';

const MAX_FACETS = 8;
/**
 * Splats up to this far above the floor plane count as the floor / mats. The floor plane is the
 * kickboard's bottom edge; the mats in front of it are ~30 cm thick (The Attic: their top is between
 * 15 and 33 cm up), and the kickboard itself is spared (ON_FACET_MM).
 */
const MAT_TOP_MM = 330;
/** A splat within this of a facet's plane (inside its outline) is the wall, never the floor. */
const ON_FACET_MM = 80;
/** A camera this low (crouched on the mats, looking up) hides every floor / mat splat. */
const LOW_CAMERA_MM = 1000;
const FADE_NEAR_MM = 500;
const FADE_FAR_MM = 1000;
/** A ghosted facet fades the splats from this far behind it to this far in front of it. */
const GHOST_BACK_MM = 350;
const GHOST_FRONT_MM = 150;
const GHOST_MARGIN_MM = 120;
const GHOST_ALPHA = 0.12;
const ANCHOR = 'vRgba = rgba;';

const GLSL_DECLS = `
uniform mat4 bwViewToWorld;
uniform vec2 bwFade;
uniform float bwFloorZ;
uniform bool bwFloorAll;
uniform int bwFacetCount;
uniform mat4 bwFacetXf[${MAX_FACETS}];
uniform vec4 bwFacetBox[${MAX_FACETS}];
uniform float bwFacetGhost[${MAX_FACETS}];
`;

// Runs where Spark sets vRgba, after its frustum cull: `viewCenter` is the splat in view space.
const GLSL_BODY = `
    {
        vec3 bwWorld = (bwViewToWorld * vec4(viewCenter, 1.0)).xyz;
        vec3 bwCam = bwViewToWorld[3].xyz;
        bool bwFloor = bwWorld.z < bwFloorZ;
        bool bwOnFacet = false;
        bool bwBetween = false;
        rgba.a *= smoothstep(bwFade.x, bwFade.y, length(viewCenter));
        for (int i = 0; i < ${MAX_FACETS}; i++) {
            if (i >= bwFacetCount) {
                break;
            }
            vec4 box = bwFacetBox[i];
            vec3 l = (bwFacetXf[i] * vec4(bwWorld, 1.0)).xyz;
            if (bwFacetGhost[i] > 0.5 && l.x > box.x - ${GHOST_MARGIN_MM.toFixed(1)} && l.x < box.y + ${GHOST_MARGIN_MM.toFixed(1)}
                && l.y > box.z - ${GHOST_MARGIN_MM.toFixed(1)} && l.y < box.w + ${GHOST_MARGIN_MM.toFixed(1)}
                && l.z > -${GHOST_BACK_MM.toFixed(1)} && l.z < ${GHOST_FRONT_MM.toFixed(1)}) {
                rgba.a *= ${GHOST_ALPHA.toFixed(2)};
            }
            bool inside = l.x > box.x && l.x < box.y && l.y > box.z && l.y < box.w;
            bwOnFacet = bwOnFacet || (inside && abs(l.z) < ${ON_FACET_MM.toFixed(1)});
            if (bwFloor && !bwBetween) {
                vec3 c = (bwFacetXf[i] * vec4(bwCam, 1.0)).xyz;
                float dz = c.z - l.z;
                float t = abs(dz) > 1e-3 ? c.z / dz : -1.0;
                vec2 h = c.xy + t * (l.xy - c.xy);
                bwBetween = t > 1.0 && h.x > box.x && h.x < box.y && h.y > box.z && h.y < box.w;
            }
        }
        if (bwFloor && !bwOnFacet && (bwFloorAll || bwBetween)) {
            return;
        }
        if (rgba.a < minAlpha) {
            return;
        }
    }
    `;

/** Injects the clip into Spark's splat vertex shader source; unchanged (and false) if Spark moved. */
function inject(source) {
    const at = source.indexOf(ANCHOR);
    const main = source.indexOf('void main()');
    if (at < 0 || main < 0 || source.includes('bwViewToWorld')) {
        return null;
    }
    const withBody = source.slice(0, at) + GLSL_BODY + source.slice(at);
    return withBody.slice(0, main) + GLSL_DECLS + withBody.slice(main);
}

/** World → facet-local (a along u, b along v, height along the normal) for each facet quad. */
function facetFrames(quads) {
    return quads.slice(0, MAX_FACETS).map(q => {
        const f = q.facet;
        const toWorld = new THREE.Matrix4().makeBasis(
            new THREE.Vector3(...f.u), new THREE.Vector3(...f.v), new THREE.Vector3(...f.normal))
            .setPosition(new THREE.Vector3(...f.origin));
        const e = f.extent;
        return { id: q.id, xf: toWorld.invert(), box: new THREE.Vector4(e.aMin, e.aMax, e.bMin, e.bMax) };
    });
}

/**
 * `quads`: facetQuads(view.facets), `floorZ`: the floor plane (wallFrame). `rendererOptions` go into
 * the SparkRenderer; `install(sparkRenderer)` patches its shader; `update(camera, target, ghostIds)`
 * once per frame while photo-real shows.
 */
export function createSplatClip(quads, floorZ) {
    const frames = facetFrames(quads);
    const pad = (list, fill) => [...list, ...Array.from({ length: MAX_FACETS - list.length }, fill)];
    const uniforms = {
        bwViewToWorld: { value: new THREE.Matrix4() },
        bwFade: { value: new THREE.Vector2(FADE_NEAR_MM, FADE_FAR_MM) },
        bwFloorZ: { value: floorZ + MAT_TOP_MM },
        bwFloorAll: { value: false },
        bwFacetCount: { value: frames.length },
        bwFacetXf: { value: pad(frames.map(f => f.xf), () => new THREE.Matrix4()) },
        bwFacetBox: { value: pad(frames.map(f => f.box), () => new THREE.Vector4()) },
        bwFacetGhost: { value: pad(frames.map(() => 0), () => 0) },
    };
    let installed = false;
    return {
        rendererOptions: { extraUniforms: uniforms, depthTest: false },
        get installed() { return installed; },
        install(sparkRenderer) {
            const m = sparkRenderer.material;
            const source = m && inject(m.vertexShader);
            if (!source) {
                console.warn('wall3d: splat clip not installed (Spark shader changed?)');
                return;
            }
            m.vertexShader = source;
            m.needsUpdate = true;
            installed = true;
        },
        update(camera, target, ghostIds) {
            uniforms.bwViewToWorld.value.copy(camera.matrixWorld);
            const camPos = uniforms.bwViewToWorld.value.elements;
            // Zoomed in close, the fade shrinks with the distance so the wall itself stays.
            const far = Math.min(FADE_FAR_MM, 0.5 * camera.position.distanceTo(target));
            uniforms.bwFade.value.set(0.5 * far, far);
            uniforms.bwFloorAll.value = camPos[14] < floorZ + Math.max(LOW_CAMERA_MM, FLOOR_CLEARANCE_MM);
            frames.forEach((f, i) => { uniforms.bwFacetGhost.value[i] = ghostIds.includes(f.id) ? 1 : 0; });
        },
    };
}
