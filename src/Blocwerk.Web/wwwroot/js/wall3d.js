// Opt-in 3D wall view. Loaded with JS.InvokeAsync<IJSObjectReference>("import", "/js/wall3d.js");
// `mount(container, view, options)` renders the Wall3DView payload (Blocwerk.Core.Geometry.View3D)
// into `container` and returns a handle whose `dispose()` frees every GPU resource.
//
// The world frame is wall-geometry.json's: millimetres, z up. Rendering is on demand — a frame is
// drawn only while something moves (damping, a tween, a resize), so an idle view costs nothing —
// except in photo-real mode (wall3d-splat.js), which renders continuously while it is on.
import * as THREE from '../lib/three/three.module.min.js';
import { OrbitControls } from '../lib/three/controls/OrbitControls.js';
import { buildFacets, buildLabels, buildMarkers, buildTextures, fitLabels } from './wall3d-scene.js';
import { buildHolds, buildSelection, OUTLINE_LIFT, placeSelection } from './wall3d-holds.js';
import { buildOutlines } from './wall3d-outlines.js';
import { availableModes, createModeController, normalizeMode, PHOTO_REAL_FAILED, watchRenderFailures } from './wall3d-modes.js';
import { createLabelLayout } from './wall3d-labels.js';
import { createTweener, presetPose, wallFrame } from './wall3d-camera.js';
import { buildOverlay, chromeInsets, createPlanMap } from './wall3d-ui.js';
import { createPhotoReal, PhotoRealUnsupportedError } from './wall3d-splat.js';
import { createFacetSides } from './wall3d-sides.js';
import { createPicker } from './wall3d-pick.js';

/** Colours of a boulder's hold roles; the page passes BoulderHoldColors so they match the 2D views. */
const DEFAULT_ROLE_COLORS = { Start: '#4CAF50', Top: '#9C27B0', Hand: '#2196F3', Foot: '#FF9800', ColorFoot: '#FF9800' };

/** Injects wall3d.css once; returns the link while it is still loading (null when already there). */
function ensureStylesheet() {
    const href = new URL('../css/wall3d.css', import.meta.url).href;
    if ([...document.querySelectorAll('link[rel="stylesheet"]')].some(l => l.href === href)) return null;
    const link = document.createElement('link');
    link.rel = 'stylesheet';
    link.href = href;
    document.head.append(link);
    return link;
}

function themeColor(el, name, fallback) {
    const v = getComputedStyle(el).getPropertyValue(name).trim();
    return v || fallback;
}

/** Floor, a 1.75 m person where a climber stands (scale at a glance), and lights. Returns [floor, person]. */
function buildSurroundings(scene, frame, bg) {
    scene.background = new THREE.Color(bg);
    // The overhang faces the floor, so the ground bounce has to be bright or it reads as a cave.
    scene.add(new THREE.HemisphereLight(0xffffff, 0xd8cfc4, 2.0));
    const sun = new THREE.DirectionalLight(0xffffff, 1.5);
    sun.position.copy(frame.center).addScaledVector(frame.front, 6000).add(new THREE.Vector3(-2000, 0, 5000));
    sun.target.position.copy(frame.center);
    scene.add(sun, sun.target);

    // A mat a shade off the page background, so it grounds the wall without a hard horizon.
    const floor = new THREE.Mesh(
        new THREE.CircleGeometry(frame.radius * 1.6, 64),
        new THREE.MeshStandardMaterial({ color: new THREE.Color(bg).lerp(new THREE.Color(0x3a4050), 0.18), roughness: 1 }));
    floor.position.set(frame.center.x, frame.center.y, frame.floorZ - 2);
    scene.add(floor);

    const person = new THREE.Mesh(
        new THREE.CapsuleGeometry(150, 1750 - 300, 6, 16),
        new THREE.MeshStandardMaterial({ color: 0x8a93a6, roughness: 0.8, transparent: true, opacity: 0.4, depthWrite: false }));
    person.rotation.x = Math.PI / 2;           // capsule axis is y; stand it on z
    person.position.set(frame.stand.x, frame.stand.y, frame.floorZ + 875);
    scene.add(person);
    return [floor, person];
}

export function mount(container, view, options = {}) {
    const loadingCss = ensureStylesheet();
    const roleColors = { ...DEFAULT_ROLE_COLORS, ...(options.roleColors || {}) };
    const reducedMq = window.matchMedia('(prefers-reduced-motion: reduce)');
    container.classList.add('w3d-root');

    const renderer = new THREE.WebGLRenderer({ antialias: true, preserveDrawingBuffer: !!options.preserveDrawingBuffer });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
    renderer.domElement.className = 'w3d-canvas';
    container.prepend(renderer.domElement);

    const scene = new THREE.Scene();
    const camera = new THREE.PerspectiveCamera(50, 1, 20, 200000);
    camera.up.set(0, 0, 1);

    const sides = createFacetSides(view.facets);
    const facets = buildFacets(view, renderer);
    const holds = buildHolds(view, roleColors, sides);
    const outlines = buildOutlines(holds.litHolds, holds.dimHolds, holds.facets, OUTLINE_LIFT, sides);
    const selection = buildSelection();
    const labels = buildLabels(view);
    const textures = buildTextures(view, renderer);
    const markers = buildMarkers(view);
    scene.add(facets.group, textures, markers, labels, holds.lit, holds.dim, outlines, holds.rings, holds.pick, selection);
    const frame = wallFrame(view, facets.group);
    const surroundings = buildSurroundings(scene, frame, themeColor(container, '--bg', '#f5f4f1'));

    const controls = new OrbitControls(camera, renderer.domElement);
    controls.enableDamping = true;
    controls.dampingFactor = 0.12;
    controls.screenSpacePanning = true;
    controls.minDistance = 300;
    controls.maxDistance = frame.radius * 8;
    controls.zoomToCursor = true;
    const tweener = createTweener(camera, controls, () => reducedMq.matches);

    let raf = 0;
    let disposed = false;
    const request = () => { if (!raf && !disposed) raf = requestAnimationFrame(tick); };
    renderer.__wall3dRequest = request;         // texture loads ask for a redraw when they land

    // Photo-real mode swaps the modelled wall for the captured splat; the boulder rings, the
    // selection and hold taps stay (the never-drawn pick outlines are still what a tap hits).
    const photo = createPhotoReal({
        renderer, scene, view,
        facetParts: [facets.group, textures, markers, labels, holds.lit, holds.dim, outlines, ...surroundings],
        onProgress: f => ui.say(f == null ? 'Loading the photo-real view…' : `Loading the photo-real view… ${Math.round(f * 100)}%`),
    });
    const modes = availableModes(view, photo.available);
    const ui = buildOverlay(container, view, {
        preset: name => goTo(name),
        reset: () => { ui.hideCard(); selection.visible = false; goTo('front'); },
        closeCard: () => { ui.hideCard(); selection.visible = false; request(); },
        mode: name => modeCtl.set(name),
    }, modes, { hintOnce: !!options.hintOnce });
    const modeCtl = createModeController({
        modes, photo, ui, request: () => request(), PhotoRealUnsupportedError,
        parts: { textures, outlines, slabs: [holds.lit, holds.dim] },
    });
    const failures = watchRenderFailures(renderer, modeCtl, () => request());
    const plan = createPlanMap(ui.map, view, frame);
    const labelLayout = createLabelLayout(labels, camera, renderer.domElement, sides);
    // Labels keep out of the overlay controls; re-measured when one appears, goes or resizes.
    let obstaclesDirty = true;
    const hintObserver = new MutationObserver(() => { obstaclesDirty = true; request(); });
    hintObserver.observe(ui.hint, { attributes: true, attributeFilter: ['class'], childList: true, characterData: true });

    // The preset the camera still shows as framed; a drag clears it. While set, a resize (rotation,
    // the stylesheet landing, the page reflowing) re-frames it for the new stage.
    let framed = null;
    const framedPose = name => presetPose(name, frame, camera, chromeInsets(container, ui),
        { w: container.clientWidth, h: container.clientHeight });

    function goTo(name) {
        // The hint covers part of the stage; any camera move dismisses it.
        ui.hideHint();
        framed = name;
        tweener.to(framedPose(name));
        ui.setActive(name);
        request();
    }

    function reframe() {
        if (!framed || tweener.active) return;
        const pose = framedPose(framed);
        camera.position.copy(pose.position);
        controls.target.copy(pose.target);
        controls.update();
    }

    function tick(now) {
        raf = 0;
        const tweening = tweener.step(now);
        const moving = controls.update();
        camera.updateMatrixWorld();
        sides.update(camera);
        // The selection halo draws over everything (no depth test), so it hides behind its facet.
        if (selection.userData.facet) selection.material.visible = sides.inFront(selection.userData.facet, camera.position);
        if (labels.visible) {
            if (obstaclesDirty) {
                labelLayout.measure([ui.modes, ui.hint, ui.map]);
                obstaclesDirty = false;
            }
            labelLayout.update();
        }
        try {
            renderer.render(scene, camera);
        } catch (err) {
            console.warn('wall3d: render failed', err);
            modeCtl.fail(PHOTO_REAL_FAILED);
        }
        const dist = camera.position.distanceTo(controls.target);
        const h = renderer.domElement.clientHeight;
        ui.setScale(h / (2 * dist * Math.tan(THREE.MathUtils.degToRad(camera.fov) / 2)));
        plan.update(camera.position, controls.target);
        if (tweening || moving || photo.active) request();
    }

    function resize() {
        const w = container.clientWidth || 1;
        const h = container.clientHeight || 1;
        renderer.setSize(w, h, false);
        camera.aspect = w / h;
        camera.updateProjectionMatrix();
        fitLabels(labels, camera.aspect);
        reframe();
        obstaclesDirty = true;
        request();
    }

    // Picking: a tap on a hold (on the camera's side of the wall) opens its card.
    const picker = createPicker({
        canvas: renderer.domElement, camera, holds, sides, walls: [...facets.meshes.values(), ...facets.backs],
        onDown: () => ui.hideHint(),
        onHold: hold => { placeSelection(selection, hold, holds.facets.get(hold.facetId)); ui.showHold(hold); request(); },
        onMiss: () => { selection.visible = false; ui.hideCard(); request(); },
    });
    const onStart = () => { tweener.cancel(); ui.hideHint(); ui.setActive(null); framed = null; };
    controls.addEventListener('start', onStart);
    controls.addEventListener('change', request);

    const ro = new ResizeObserver(resize);
    ro.observe(container);
    window.addEventListener('orientationchange', resize);

    framed = options.initialPreset || 'front';
    resize();
    ui.setActive(framed);
    // Until wall3d.css lands the overlay has no layout, so the chrome measured above was wrong.
    loadingCss?.addEventListener('load', () => { if (!disposed) resize(); }, { once: true });
    const startMode = normalizeMode(options.initialMode);
    modeCtl.set(startMode && modes.includes(startMode) && startMode !== 'photoreal' ? startMode : 'schematic');
    if (startMode === 'photoreal') modeCtl.set('photoreal');
    request();

    // A hold seen on two overlapping panels is drawn once; either panel's id resolves to it.
    const holdById = id => holds.all.find(h => h.id === id || (h.duplicateIds && h.duplicateIds.includes(id)));

    const handle = {
        view,
        preset: goTo,
        /** Switches the view mode ('schematic' | 'photos' | 'photoreal'); resolves when it shows. */
        mode: name => modeCtl.set(name),
        /** Turns the photo-real (splat) mode on or off; resolves when it shows. */
        photoReal: on => modeCtl.set(on ? 'photoreal' : 'schematic'),
        /** Scene statistics for the screenshot harness / perf checks. */
        stats: () => ({ holds: holds.all.length, drawCalls: renderer.info.render.calls, triangles: renderer.info.render.triangles }),
        /** Renders synchronously (used by the screenshot harness). */
        renderNow() { tweener.step(performance.now() + 1e6); controls.update(); tick(performance.now()); },
        /** Looks straight at one hold from `distanceMm` out along its facet normal (close-ups). */
        focusHold(id, distanceMm = 900) {
            const hold = holdById(id);
            const f = hold && holds.facets.get(hold.facetId);
            if (!f) return;
            const target = new THREE.Vector3(...f.origin).addScaledVector(new THREE.Vector3(...f.u), hold.planeA)
                .addScaledVector(new THREE.Vector3(...f.v), hold.planeB);
            tweener.to({ target, position: target.clone().addScaledVector(new THREE.Vector3(...f.normal), distanceMm) });
            request();
        },
        selectHold(id) {
            const hold = holdById(id);
            if (!hold) return;
            placeSelection(selection, hold, holds.facets.get(hold.facetId));
            ui.showHold(hold);
            request();
        },
        dispose() {
            if (disposed) return;
            disposed = true;
            if (raf) cancelAnimationFrame(raf);
            ro.disconnect();
            hintObserver.disconnect();
            window.removeEventListener('orientationchange', resize);
            picker.dispose();
            failures.dispose();
            controls.removeEventListener('start', onStart);
            controls.removeEventListener('change', request);
            controls.dispose();
            photo.dispose();
            disposeScene(scene);
            renderer.dispose();
            renderer.forceContextLoss();
            container.replaceChildren();
            container.classList.remove('w3d-root');
            delete container.__wall3d;
        },
    };
    container.__wall3d = handle;           // for the screenshot / perf scripts
    return handle;
}

function disposeScene(scene) {
    const seen = new Set();
    const free = x => { if (x && !seen.has(x)) { seen.add(x); x.dispose?.(); } };
    scene.traverse(o => {
        free(o.geometry);
        const mats = Array.isArray(o.material) ? o.material : [o.material];
        for (const m of mats) {
            if (!m) continue;
            for (const v of Object.values(m)) if (v && v.isTexture) free(v);
            free(m);
        }
        if (o.isInstancedMesh) o.dispose();
    });
}
