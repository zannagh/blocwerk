// The hold overlay of the photo-real mode (wall3d.js): the outline layer (wall3d-outlines.js), raised
// to each hold's relief (wall3d-relief.js), and the boulder role rings drawn over the splat, with a
// "Show holds" toggle (on by default, remembered per browser).
//
// Occlusion: the splat draws without depth test (wall3d-splat-clip.js), so it is never cut by the
// modelled facets. The facets themselves go into the depth buffer first, invisibly (colour writes off,
// depth writes on, both sides), so an outline or ring behind another facet fails its depth test;
// outlines of a facet the camera is behind are dropped already (wall3d-sides.js). A facet ghosted
// out of the camera's way (wall3d-ghost.js) writes no depth, so the holds behind it show.
import * as THREE from '../lib/three/three.module.min.js';

const SHOW_KEY = 'bw.wall3d.photoHolds';

function storedShow() {
    try {
        return localStorage.getItem(SHOW_KEY) !== '0';
    } catch {
        return true;
    }
}

function storeShow(on) {
    try {
        localStorage.setItem(SHOW_KEY, on ? '1' : '0');
    } catch {
        // private mode / storage off: the toggle just resets next time
    }
}

/** Invisible copies of the facet quads that only write depth; drawn before everything else. */
function buildDepthPrepass(facets) {
    const group = new THREE.Group();
    const material = new THREE.MeshBasicMaterial({ colorWrite: false, depthWrite: true, side: THREE.DoubleSide });
    for (const mesh of facets.meshes.values()) {
        const m = new THREE.Mesh(mesh.geometry, material);
        m.userData.facetId = mesh.userData.facet.id;
        m.renderOrder = -10;
        group.add(m);
    }
    group.visible = false;
    return group;
}

/**
 * `root`: the view's container (the toggle goes in its overlay), `facets`: buildFacets' result,
 * `outlines`: buildOutlines' group, `rings`: the boulder role rings, `request`: asks for a frame.
 * Returns { prepass (add it to the scene), apply(mode), setGhosted(ids), get showing }.
 */
export function createPhotoOverlay({ root, facets, outlines, rings, request }) {
    const prepass = buildDepthPrepass(facets);
    let show = storedShow();
    let mode = null;

    const toggle = document.createElement('button');
    toggle.type = 'button';
    toggle.className = 'w3d-btn w3d-holds-toggle';
    toggle.textContent = 'Show holds';
    toggle.title = 'Draw the hold outlines over the photo-real view';
    toggle.hidden = true;
    toggle.addEventListener('click', () => {
        show = !show;
        storeShow(show);
        apply(mode);
        request();
    });
    root.append(toggle);

    function apply(next) {
        mode = next;
        const photoReal = mode === 'photoreal';
        const holds = !photoReal || show;
        outlines.visible = mode === 'photos' || (photoReal && show);
        outlines.userData.setRaised?.(photoReal);
        rings.userData.setRaised?.(photoReal);
        prepass.visible = photoReal && show;
        rings.visible = holds;
        toggle.hidden = !photoReal;
        toggle.setAttribute('aria-pressed', show ? 'true' : 'false');
        toggle.classList.toggle('active', show);
    }

    return {
        prepass,
        apply,
        /** Ghosted facets write no depth, so the holds behind them stay visible. */
        setGhosted(ids) {
            for (const m of prepass.children) {
                m.visible = !ids.includes(m.userData.facetId);
            }
        },
        get showing() { return mode === 'photoreal' && show; },
        dispose() { toggle.remove(); },
    };
}
