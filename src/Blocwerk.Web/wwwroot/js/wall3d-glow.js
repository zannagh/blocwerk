// The highlighted boulder's holds in the 3D wall view (wall3d.js), drawn like the 2D boulder view's
// HoldShine: no fill, a glowing outline in the hold's role colour (a soft glow, a dark casing, the colour
// and a thin light line; hold-tap.css), corners lightly rounded for display (HoldOutlineSmoothing.cs).
//
// Cheap on purpose: every ring of every boulder hold is ONE triangle strip of screen-space ribbons, all
// of them in one geometry per mode, so the whole boulder is a single draw call. The vertex shader widens
// each ring to a fixed width in CSS pixels (mitred joins, so no overlap and no beads where segments
// meet) and the fragment shader paints the four layers across it; no blur pass, no render target.
// Three variants, one showing at a time: on the traced outline (Schematic), where the facet photo shows
// the hold (Photos) and raised to the hold's cap (photo-real, wall3d-relief.js).
import * as THREE from '../lib/three/three.module.min.js';
import { holdFrame, OUTLINE_LIFT, volumeBase, volumeShift } from './wall3d-holds.js';
import { photoRings, rings } from './wall3d-outlines.js';
import { reliefOf } from './wall3d-relief.js';

/** Layer the glow (and the selection) is drawn on, so the boulder focus can draw it over the dimmed scene. */
export const OVERLAY_LAYER = 1;
/** Half the ribbon's width in CSS px: the 11 px glow plus its 2 px blur. */
const HALF_PX = 7.5;
/** Chaikin passes, as HoldOutlineSmoothing.Passes. */
const SMOOTH_PASSES = 2;
/** A mitre never grows past this many half widths (a sharp corner of a traced outline). */
const MITRE_LIMIT = 2;

const VERTEX = `
attribute vec3 prev;
attribute vec3 next;
attribute float side;
attribute vec3 glowColor;
uniform vec2 uResolution;
uniform float uHalfWidth;
varying vec3 vColor;
varying float vAcross;
vec2 screenOf(vec4 c) { return c.xy / c.w * 0.5 * uResolution; }
void main() {
    mat4 mvp = projectionMatrix * modelViewMatrix;
    vec4 c = mvp * vec4(position, 1.0);
    vec2 s = screenOf(c);
    vec2 d1 = s - screenOf(mvp * vec4(prev, 1.0));
    vec2 d2 = screenOf(mvp * vec4(next, 1.0)) - s;
    if (dot(d1, d1) < 1e-8) d1 = d2;
    if (dot(d2, d2) < 1e-8) d2 = d1;
    d1 = normalize(d1);
    d2 = normalize(d2);
    vec2 t = d1 + d2;
    t = dot(t, t) < 1e-8 ? d1 : normalize(t);
    vec2 mitre = vec2(-t.y, t.x);
    float len = uHalfWidth / max(dot(mitre, vec2(-d1.y, d1.x)), 1.0 / ${MITRE_LIMIT.toFixed(1)});
    c.xy += mitre * len * side / (0.5 * uResolution) * c.w;
    gl_Position = c;
    vColor = glowColor;
    vAcross = side;
}`;

const FRAGMENT = `
varying vec3 vColor;
varying float vAcross;
vec4 over(vec4 top, vec4 under) {
    float a = top.a + under.a * (1.0 - top.a);
    return a <= 0.0 ? vec4(0.0) : vec4((top.rgb * top.a + under.rgb * under.a * (1.0 - top.a)) / a, a);
}
float band(float t, float edge, float aa) { return 1.0 - smoothstep(edge - aa, edge + aa, t); }
void main() {
    float t = abs(vAcross) * ${HALF_PX.toFixed(1)};
    float aa = max(fwidth(t), 0.35);
    vec4 col = vec4(vColor, 0.38 * (1.0 - smoothstep(3.0, ${HALF_PX.toFixed(1)}, t)));
    col = over(vec4(0.0, 0.0, 0.0, 0.6 * band(t, 2.75, aa)), col);
    col = over(vec4(vColor, band(t, 1.75, aa)), col);
    col = over(vec4(1.0, 1.0, 1.0, 0.75 * band(t, 0.5, aa)), col);
    if (col.a < 0.004) discard;
    gl_FragColor = col;
    #include <colorspace_fragment>
}`;

/** One Chaikin pass over a closed ring of [a, b] points. */
function cutCorners(ring) {
    const out = [];
    for (let i = 0; i < ring.length; i++) {
        const [a, b] = [ring[i], ring[(i + 1) % ring.length]];
        out.push([0.75 * a[0] + 0.25 * b[0], 0.75 * a[1] + 0.25 * b[1]], [0.25 * a[0] + 0.75 * b[0], 0.25 * a[1] + 0.75 * b[1]]);
    }
    return out;
}

/** The ring rounded like HoldOutlineSmoothing.Smooth (display only). */
export function smoothRing(ring) {
    let r = ring;
    for (let i = 0; r.length >= 3 && i < SMOOTH_PASSES; i++) r = cutCorners(r);
    return r;
}

/** Appends one closed ring (world points) as a strip: N + 1 points (the first again), two vertices each. */
function pushRing(buf, pts, color, slot) {
    const n = pts.length;
    const base = buf.pos.length / 3;
    for (let i = 0; i <= n; i++) {
        const [p, prev, next] = [pts[i % n], pts[(i - 1 + n) % n], pts[(i + 1) % n]];
        for (const side of [-1, 1]) {
            buf.pos.push(p.x, p.y, p.z);
            buf.prev.push(prev.x, prev.y, prev.z);
            buf.next.push(next.x, next.y, next.z);
            buf.side.push(side);
            buf.col.push(color.r, color.g, color.b);
            buf.slot.push(slot);
        }
        if (i < n) {
            const k = base + 2 * i;
            buf.index.push(k, k + 1, k + 2, k + 1, k + 3, k + 2);
        }
    }
}

function ribbonGeometry(list, frameOf, ringsOf, colorOf, slotOf) {
    const buf = { pos: [], prev: [], next: [], side: [], col: [], slot: [], index: [] };
    for (const h of list) {
        const m = frameOf(h);
        for (const ring of ringsOf(h)) {
            if (ring.length < 3) continue;
            const pts = smoothRing(ring).map(([a, b]) => new THREE.Vector3(a, b, 0).applyMatrix4(m));
            pushRing(buf, pts, colorOf(h), slotOf(h));
        }
    }
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.Float32BufferAttribute(buf.pos, 3));
    g.setAttribute('prev', new THREE.Float32BufferAttribute(buf.prev, 3));
    g.setAttribute('next', new THREE.Float32BufferAttribute(buf.next, 3));
    g.setAttribute('side', new THREE.Float32BufferAttribute(buf.side, 1));
    g.setAttribute('glowColor', new THREE.Float32BufferAttribute(buf.col, 3));
    g.setAttribute('facetIndex', new THREE.Float32BufferAttribute(buf.slot, 1));
    g.setIndex(buf.index);
    g.computeBoundingSphere();
    return g;
}

function ribbonMaterial(sides, uniforms) {
    return sides.cullBehind(new THREE.ShaderMaterial({
        vertexShader: VERTEX, fragmentShader: FRAGMENT, uniforms,
        transparent: true, depthWrite: false, side: THREE.DoubleSide,
    }));
}

/**
 * The glow group for the boulder's holds (`list`: the lit holds of a highlighted boulder), coloured by role
 * (`roleColors`). `group.userData.setMode(mode)` shows the variant of a view mode (wall3d-overlay.js calls it
 * on every mode switch). Everything sits on OVERLAY_LAYER.
 */
export function buildGlow(list, facets, roleColors, sides) {
    const group = new THREE.Group();
    const uniforms = { uResolution: { value: new THREE.Vector2(1, 1) }, uHalfWidth: { value: HALF_PX } };
    const size = new THREE.Vector2();
    const colorOf = h => new THREE.Color(roleColors[h.role] || roleColors.Hand);
    const slotOf = h => sides.index.get(h.facetId) ?? 0;
    const flatFrame = h => holdFrame(h, facets.get(h.facetId), OUTLINE_LIFT + volumeBase(h), volumeShift(h));
    const capFrame = h => {
        const r = reliefOf(h);
        return holdFrame(h, facets.get(h.facetId), r.cap, r.shift);
    };
    const variant = (frameOf, ringsOf) => {
        const mesh = new THREE.Mesh(ribbonGeometry(list, frameOf, ringsOf, colorOf, slotOf), ribbonMaterial(sides, uniforms));
        mesh.renderOrder = 7;
        mesh.layers.set(OVERLAY_LAYER);
        mesh.onBeforeRender = renderer => {
            renderer.getDrawingBufferSize(size);
            uniforms.uResolution.value.copy(size);
            uniforms.uHalfWidth.value = HALF_PX * renderer.getPixelRatio();
        };
        group.add(mesh);
        return mesh;
    };
    const parts = { schematic: variant(flatFrame, rings), photos: variant(flatFrame, photoRings), photoreal: variant(capFrame, rings) };
    group.layers.set(OVERLAY_LAYER);
    group.userData.setMode = mode => {
        const shown = parts[mode] ? mode : 'schematic';
        for (const [name, mesh] of Object.entries(parts)) mesh.visible = name === shown;
    };
    group.userData.setMode('schematic');
    return group;
}
