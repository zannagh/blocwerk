// The hold overlay of the photo-real mode (wall3d.js): the outline layer (wall3d-outlines.js), raised
// to each hold's relief (wall3d-relief.js), and the boulder role rings drawn over the splat, with a
// "Show holds" toggle (on by default, remembered per browser) and the "Detail: Auto / High / Ultra"
// toggle of the photo-real ladder (wall3d-splat-detail.js; remembered per browser; High only where
// it lifts a phone's cap). Its label names the level drawn, e.g. "Ultra · 1.6M".
//
// Occlusion: the splat draws without depth test (wall3d-splat-clip.js), so it is never cut by the
// modelled facets. The facets themselves go into the depth buffer first, invisibly (colour writes off,
// depth writes on, both sides), so an outline or ring behind another facet fails its depth test;
// outlines of a facet the camera is behind are dropped already (wall3d-sides.js). A facet ghosted
// out of the camera's way (wall3d-ghost.js) writes no depth, so the holds behind it show.
import * as THREE from '../lib/three/three.module.min.js';
import { detailChoices, detailLabel, nextDetail } from './wall3d-splat-detail.js';

const SHOW_KEY = 'bw.wall3d.photoHolds';
/** How long the phone's "Full detail" hint stays after choosing Ultra. */
const HINT_MS = 6000;

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

function button(cls, text, title, onClick) {
    const b = document.createElement('button');
    b.type = 'button';
    b.className = `w3d-btn ${cls}`;
    b.textContent = text;
    b.title = title;
    b.addEventListener('click', onClick);
    return b;
}

/**
 * `root`: the view's container (the toggles go in its overlay), `facets`: buildFacets' result,
 * `outlines`: buildOutlines' group, `rings`: the boulder role rings, `request`: asks for a frame,
 * `photo`: the photo-real controller (its detail choice).
 * Returns { prepass (add it to the scene), apply(mode), setGhosted(ids), get showing }.
 */
export function createPhotoOverlay({ root, facets, outlines, rings, request, photo }) {
    const prepass = buildDepthPrepass(facets);
    let show = storedShow();
    let mode = null;

    const tools = document.createElement('div');
    tools.className = 'w3d-photo-tools';
    tools.hidden = true;
    const toggle = button('w3d-holds-toggle', 'Show holds', 'Draw the hold outlines over the photo-real view', () => {
        show = !show;
        storeShow(show);
        apply(mode);
        request();
    });
    const detail = button('w3d-detail-toggle', '', 'Detail: Auto keeps the device cool, High loads more, Ultra draws the full scene', () => {
        if (!photo) return;
        const next = nextDetail(photo.detail, detailChoices(photo.highLifts));
        photo.setDetail(next);
        showHint(next === 'ultra' && photo.light);
        apply(mode);
    });
    const hint = document.createElement('div');
    hint.className = 'w3d-detail-hint';
    hint.hidden = true;
    hint.setAttribute('role', 'status');
    hint.textContent = 'Full detail: uses more battery and may get warm';
    let hintTimer = 0;
    function showHint(on) {
        clearTimeout(hintTimer);
        hint.hidden = !on;
        if (on) hintTimer = setTimeout(() => { hint.hidden = true; }, HINT_MS);
    }
    tools.append(toggle, detail, hint);
    root.append(tools);
    if (photo) photo.onLevel = () => labelDetail();

    function labelDetail() {
        const level = photo?.level;
        const splats = level && level.index >= 0 ? level.splats : null;
        detail.textContent = detailLabel(photo?.detail ?? 'auto', splats);
        detail.setAttribute('aria-pressed', photo?.detail && photo.detail !== 'auto' ? 'true' : 'false');
    }

    function apply(next) {
        mode = next;
        const photoReal = mode === 'photoreal';
        const holds = !photoReal || show;
        outlines.visible = mode === 'photos' || (photoReal && show);
        outlines.userData.setRaised?.(photoReal);
        rings.userData.setRaised?.(photoReal);
        prepass.visible = photoReal && show;
        rings.visible = holds;
        tools.hidden = !photoReal;
        toggle.setAttribute('aria-pressed', show ? 'true' : 'false');
        toggle.classList.toggle('active', show);
        detail.hidden = !photo?.detailChoice;
        if (!photoReal) showHint(false);
        labelDetail();
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
        dispose() { clearTimeout(hintTimer); tools.remove(); },
    };
}
