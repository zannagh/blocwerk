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
import { availableModes, createModeController, normalizeMode } from './wall3d-modes.js';
import { createLabelLayout } from './wall3d-labels.js';
import { createTweener, presetPose, wallFrame } from './wall3d-camera.js';
import { buildOverlay, createPlanMap } from './wall3d-ui.js';
import { createPhotoReal, PhotoRealUnsupportedError } from './wall3d-splat.js';

/** Colours of a boulder's hold roles; the page passes BoulderHoldColors so they match the 2D views. */
const DEFAULT_ROLE_COLORS = { Start: '#4CAF50', Top: '#9C27B0', Hand: '#2196F3', Foot: '#FF9800', ColorFoot: '#FF9800' };
const TAP_SLOP_PX = 8;

function ensureStylesheet() {
    const href = new URL('../css/wall3d.css', import.meta.url).href;
    if (![...document.querySelectorAll('link[rel="stylesheet"]')].some(l => l.href === href)) {
        const link = document.createElement('link');
        link.rel = 'stylesheet';
        link.href = href;
        document.head.append(link);
    }
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
    ensureStylesheet();
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

    const facets = buildFacets(view, renderer);
    const holds = buildHolds(view, roleColors);
    const outlines = buildOutlines(holds.litHolds, holds.dimHolds, holds.facets, OUTLINE_LIFT);
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
    }, modes);
    const modeCtl = createModeController({
        modes, photo, ui, request: () => request(), PhotoRealUnsupportedError,
        parts: { textures, outlines, slabs: [holds.lit, holds.dim] },
    });
    const plan = createPlanMap(ui.map, view, frame);
    const labelLayout = createLabelLayout(labels, camera, renderer.domElement);
    // Labels keep out of the overlay controls; re-measured when one appears, goes or resizes.
    let obstaclesDirty = true;
    const hintObserver = new MutationObserver(() => { obstaclesDirty = true; request(); });
    hintObserver.observe(ui.hint, { attributes: true, attributeFilter: ['class'], childList: true, characterData: true });

    function goTo(name) {
        // The hint pill sits over the top-left of the stage, where presets such as "Below" put the
        // facet labels — it hid the leading digits of "44.7° overhang". Any camera move dismisses it.
        ui.hideHint();
        tweener.to(presetPose(name, frame, camera));
        ui.setActive(name);
        request();
    }

    function tick(now) {
        raf = 0;
        const tweening = tweener.step(now);
        const moving = controls.update();
        if (labels.visible) {
            if (obstaclesDirty) {
                labelLayout.measure([ui.modes, ui.hint, ui.map]);
                obstaclesDirty = false;
            }
            camera.updateMatrixWorld();
            labelLayout.update();
        }
        renderer.render(scene, camera);
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
        obstaclesDirty = true;
        request();
    }

    // ── picking: a tap (not a drag) on a hold opens its card ─────────────────────────────────
    const raycaster = new THREE.Raycaster();
    let down = null;
    const onDown = e => { down = { x: e.clientX, y: e.clientY }; };
    const onUp = e => {
        if (!down || Math.hypot(e.clientX - down.x, e.clientY - down.y) > TAP_SLOP_PX) { down = null; return; }
        down = null;
        const r = renderer.domElement.getBoundingClientRect();
        const ndc = new THREE.Vector2(((e.clientX - r.left) / r.width) * 2 - 1, -((e.clientY - r.top) / r.height) * 2 + 1);
        raycaster.setFromCamera(ndc, camera);
        const hold = holds.holdAt(raycaster.intersectObject(holds.pick, false)[0]);
        if (hold) {
            placeSelection(selection, hold, holds.facets.get(hold.facetId));
            ui.showHold(hold);
        } else {
            selection.visible = false;
            ui.hideCard();
        }
        request();
    };
    const onStart = () => { tweener.cancel(); ui.hideHint(); ui.setActive(null); };
    renderer.domElement.addEventListener('pointerdown', onDown);
    renderer.domElement.addEventListener('pointerup', onUp);
    controls.addEventListener('start', onStart);
    controls.addEventListener('change', request);

    const ro = new ResizeObserver(resize);
    ro.observe(container);
    window.addEventListener('orientationchange', resize);

    resize();
    const initial = presetPose(options.initialPreset || 'front', frame, camera);
    camera.position.copy(initial.position);
    controls.target.copy(initial.target);
    ui.setActive(options.initialPreset || 'front');
    const startMode = normalizeMode(options.initialMode);
    modeCtl.set(startMode && modes.includes(startMode) && startMode !== 'photoreal' ? startMode : 'schematic');
    if (startMode === 'photoreal') modeCtl.set('photoreal');
    request();

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
            const hold = holds.all.find(h => h.id === id);
            const f = hold && holds.facets.get(hold.facetId);
            if (!f) return;
            const target = new THREE.Vector3(...f.origin).addScaledVector(new THREE.Vector3(...f.u), hold.planeA)
                .addScaledVector(new THREE.Vector3(...f.v), hold.planeB);
            tweener.to({ target, position: target.clone().addScaledVector(new THREE.Vector3(...f.normal), distanceMm) });
            request();
        },
        selectHold(id) {
            const hold = holds.all.find(h => h.id === id);
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
            renderer.domElement.removeEventListener('pointerdown', onDown);
            renderer.domElement.removeEventListener('pointerup', onUp);
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
