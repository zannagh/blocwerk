// Focus on a boulder in the 3D wall view (wall3d.js): the same lightbulb and the same per-device state as
// the boulder's photo (boulder-focus.js puts `bw-boulder-focus` on <html>), and the same level: the rest of
// the scene at brightness 0.4 and saturation 0.475 (boulder-focus.css), the boulder's glowing outlines
// (wall3d-glow.js, on OVERLAY_LAYER) and the selection halo at full strength over it.
//
// While focus is on a frame is three steps: the scene without the overlay layer, one copy of the drawn
// frame into a texture and a full-screen triangle that writes it back dimmed (the CSS filter's colour
// matrix, exactly), then the overlay layer on top, depth-tested against the first step's depth so the wall
// still hides a hold behind it. Off, it is the one plain render. Frames are on demand (wall3d-loop.js), so
// a still view costs nothing either way.
import * as THREE from '../lib/three/three.module.min.js';
import { OVERLAY_LAYER } from './wall3d-glow.js';

const FOCUS_CLASS = 'bw-boulder-focus';
const BRIGHTNESS = 0.4;
const SATURATION = 0.475;

const DIM_VERTEX = `
varying vec2 vUv;
void main() {
    vUv = position.xy * 0.5 + 0.5;
    gl_Position = vec4(position.xy, 0.0, 1.0);
}`;

// CSS saturate() is a mix towards the Rec. 709 luma, brightness() a plain factor; both on the stored
// (sRGB-encoded) values, as the browser filters the photo.
const DIM_FRAGMENT = `
uniform sampler2D tFrame;
uniform float uBrightness;
uniform float uSaturation;
varying vec2 vUv;
void main() {
    vec3 c = texture2D(tFrame, vUv).rgb;
    float luma = dot(c, vec3(0.2126, 0.7152, 0.0722));
    gl_FragColor = vec4(mix(vec3(luma), c, uSaturation) * uBrightness, 1.0);
}`;

function dimPass() {
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.Float32BufferAttribute([-1, -1, 0, 3, -1, 0, -1, 3, 0], 3));
    const material = new THREE.ShaderMaterial({
        vertexShader: DIM_VERTEX, fragmentShader: DIM_FRAGMENT,
        uniforms: { tFrame: { value: null }, uBrightness: { value: BRIGHTNESS }, uSaturation: { value: SATURATION } },
        depthTest: false, depthWrite: false,
    });
    const mesh = new THREE.Mesh(geo, material);
    mesh.frustumCulled = false;
    const scene = new THREE.Scene();
    scene.add(mesh);
    return { scene, camera: new THREE.OrthographicCamera(-1, 1, 1, -1, 0, 1), material, geo };
}

/**
 * `enabled`: this view shows a boulder and has the lightbulb. `request()` asks for a frame when the
 * stored focus changes. Returns { render(scene, camera), get on, dispose() }.
 */
export function createFocus({ renderer, enabled, request }) {
    const root = document.documentElement;
    const read = () => enabled && root.classList.contains(FOCUS_CLASS);
    let on = read();
    let pass = null;
    let frame = null;
    const size = new THREE.Vector2();

    const observer = enabled ? new MutationObserver(() => {
        const next = read();
        if (next !== on) {
            on = next;
            request();
        }
    }) : null;
    observer?.observe(root, { attributes: true, attributeFilter: ['class'] });

    function frameTexture() {
        renderer.getDrawingBufferSize(size);
        if (!frame || frame.image.width !== size.x || frame.image.height !== size.y) {
            frame?.dispose();
            frame = new THREE.FramebufferTexture(size.x, size.y);
        }
        return frame;
    }

    function renderFocused(scene, camera) {
        pass ??= dimPass();
        camera.layers.disable(OVERLAY_LAYER);
        renderer.render(scene, camera);
        const texture = frameTexture();
        renderer.copyFramebufferToTexture(texture);
        pass.material.uniforms.tFrame.value = texture;
        // A colour background clears the frame on every render, autoClear or not: off for the overlay pass.
        const autoClear = renderer.autoClear;
        const background = scene.background;
        renderer.autoClear = false;
        try {
            renderer.render(pass.scene, pass.camera);
            camera.layers.set(OVERLAY_LAYER);
            scene.background = null;
            renderer.render(scene, camera);
        } finally {
            scene.background = background;
            renderer.autoClear = autoClear;
            camera.layers.enable(0);
            camera.layers.enable(OVERLAY_LAYER);
        }
    }

    return {
        get on() { return on; },
        /** Draws one frame of `scene`; the camera sees layer 0 and the overlay layer. */
        render(scene, camera) {
            camera.layers.enable(OVERLAY_LAYER);
            if (on) {
                renderFocused(scene, camera);
            } else {
                renderer.render(scene, camera);
            }
        },
        dispose() {
            observer?.disconnect();
            frame?.dispose();
            if (pass) {
                pass.material.dispose();
                pass.geo.dispose();
            }
        },
    };
}
