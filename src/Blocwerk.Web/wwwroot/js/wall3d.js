// Opt-in 3D wall view. Loaded with JS.InvokeAsync<IJSObjectReference>("import", "/js/wall3d.js");
// `mount(container, view, options)` renders the Wall3DView payload (Blocwerk.Core.Geometry.View3D)
// into `container` and returns a handle whose `dispose()` frees every GPU resource.
//
// The world frame is wall-geometry.json's: millimetres, z up. Rendering is on demand in every mode
// (wall3d-loop.js): a frame is drawn only while something moves or changes, and never while the page
// is hidden or the view off screen, so an idle view costs nothing.
//
// Nothing blocks the view: presets pick camera spots in free space with a clear sight line
// (wall3d-clearance.js), a facet between the orbiting camera and its target is ghosted
// (wall3d-ghost.js), and the splat fades what is near the camera, the mats in the way and ghosted
// facets' surroundings (wall3d-splat-clip.js). Photo-real draws the hold outlines over the splat (wall3d-overlay.js);
// taps pick holds the same way in every mode (wall3d-pick.js). Volumes: wall3d-volumes.js.
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
import { createPicker, screenPointOf } from './wall3d-pick.js';
import { buildSurroundings, disposeScene, ensureStylesheet, themeColor } from './wall3d-stage.js';
import { createGhosting } from './wall3d-ghost.js';
import { createPhotoOverlay } from './wall3d-overlay.js';
import { createSplatClip } from './wall3d-splat-clip.js';
import { buildVolumes } from './wall3d-volumes.js';
import { createRenderLoop } from './wall3d-loop.js';

/** Colours of a boulder's hold roles; the page passes BoulderHoldColors so they match the 2D views. */
const DEFAULT_ROLE_COLORS = { Start: '#4CAF50', Top: '#9C27B0', Hand: '#2196F3', Foot: '#FF9800', ColorFoot: '#FF9800' };
/** Orbit damping per frame; at the photo-real phone cap (30 fps) the same glide in real time. */
const DAMPING = 0.12;
const CAPPED_DAMPING = 1 - (1 - DAMPING) ** 2;

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
    const markers = buildMarkers(view, sides);
    const volumes = buildVolumes(view, renderer, textures);   // plain: Schematic; photo: Photos (in `textures`)
    scene.add(facets.group, textures, markers, labels, holds.lit, holds.dim, volumes.plain, outlines, holds.rings, holds.pick, selection);
    const frame = wallFrame(view, facets.group);
    const ghosts = createGhosting({ facets, textures, quads: frame.quads, sides });
    const clip = createSplatClip(frame.quads, frame.floorZ);
    const surroundings = buildSurroundings(scene, frame, themeColor(container, '--bg', '#f5f4f1'));

    const controls = new OrbitControls(camera, renderer.domElement);
    controls.enableDamping = true;
    controls.dampingFactor = DAMPING;
    controls.screenSpacePanning = true;
    controls.minDistance = 300;
    controls.maxDistance = frame.radius * 8;
    controls.zoomToCursor = true;
    const tweener = createTweener(camera, controls, () => reducedMq.matches);

    let disposed = false;
    const loop = createRenderLoop({
        container, draw: tick,
        more: () => photo.wantsFrames,
        capped: () => photo.capped,
        onInteract: moving => photo.interact(moving),
    });
    const request = () => { if (!disposed) loop.request(); };
    renderer.__wall3dRequest = request;         // texture loads ask for a redraw when they land

    // Photo-real mode swaps the modelled wall for the captured splat; the hold outlines and boulder
    // rings (wall3d-overlay.js), the selection and hold taps stay.
    const photo = createPhotoReal({
        renderer, scene, view, clip,
        facetParts: [facets.group, textures, markers, labels, holds.lit, holds.dim, volumes.plain, ...surroundings],
        photoTextures: textures,
        onGiveUp: message => modeCtl.fail(message),
        onProgress: f => ui.say(f == null ? 'Loading the photo-real view…' : `Loading the photo-real view… ${Math.round(f * 100)}%`),
        request: () => request(),
    });
    const modes = availableModes(view, photo.available);
    const ui = buildOverlay(container, view, {
        preset: name => goTo(name),
        reset: () => { ui.hideCard(); selection.visible = false; goTo('front'); },
        closeCard: () => { ui.hideCard(); selection.visible = false; request(); },
        mode: name => modeCtl.set(name),
    }, modes, { hintOnce: !!options.hintOnce });
    const overlay = createPhotoOverlay({ root: container, facets, outlines, rings: holds.rings, request: () => request(), photo });
    scene.add(overlay.prepass);
    const modeCtl = createModeController({
        modes, photo, ui, request: () => request(), PhotoRealUnsupportedError, onMode: m => overlay.apply(m),
        parts: { textures, outlines, slabs: [holds.lit, holds.dim, volumes.plain] },
    });
    const failures = watchRenderFailures(renderer, modeCtl, () => request(), photo);
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
        loop.interact();
    }

    function reframe() {
        if (!framed || tweener.active) return;
        const pose = framedPose(framed);
        camera.position.copy(pose.position);
        controls.target.copy(pose.target);
        controls.update();
    }

    /** Draws one frame; true while the camera still moves (a tween, orbit damping). */
    function tick(now) {
        controls.dampingFactor = photo.capped ? CAPPED_DAMPING : DAMPING;
        const tweening = tweener.step(now);
        const moving = controls.update();
        camera.updateMatrixWorld();
        sides.update(camera);
        if (ghosts.update(camera.position, controls.target)) {
            overlay.setGhosted(ghosts.ids);
        }
        if (photo.active) {
            clip.update(camera, controls.target, ghosts.ids);
        }
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
            modeCtl.fail(PHOTO_REAL_FAILED, `render: ${err?.message || err}`);
        }
        const dist = camera.position.distanceTo(controls.target);
        const h = renderer.domElement.clientHeight;
        ui.setScale(h / (2 * dist * Math.tan(THREE.MathUtils.degToRad(camera.fov) / 2)));
        plan.update(camera.position, controls.target);
        if (photo.active) photo.frame(now);
        return tweening || moving;
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

    // Picking: a tap on a hold (on the camera's side of the wall) opens its card; listeners
    // (handle.onPick, a future 3D boulder editor) get the pick: hold id + facet point.
    const pickListeners = new Set();
    const picker = createPicker({
        canvas: renderer.domElement, camera, holds, sides, walls: () => ghosts.occluders(),
        onDown: () => ui.hideHint(),
        onPick: pick => {
            placeSelection(selection, pick.hold, holds.facets.get(pick.facetId));
            ui.showHold(pick.hold);
            request();
            for (const listener of pickListeners) listener(pick);
        },
        onMiss: () => { selection.visible = false; ui.hideCard(); request(); },
    });
    const onStart = () => { tweener.cancel(); ui.hideHint(); ui.setActive(null); framed = null; loop.interact(); };
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
        stats: () => ({
            holds: holds.all.length, drawCalls: renderer.info.render.calls, triangles: renderer.info.render.triangles,
            splat: photo.level, pixelRatio: renderer.getPixelRatio(), frames: loop.frames, paused: loop.paused, running: loop.running,
        }),
        /** Renders synchronously (used by the screenshot harness). */
        renderNow() { tweener.step(performance.now() + 1e6); controls.update(); loop.drawNow(performance.now()); },
        /** Looks straight at one hold from `distanceMm` out along its facet normal (close-ups). */
        focusHold(id, distanceMm = 900) {
            const hold = holdById(id);
            const f = hold && holds.facets.get(hold.facetId);
            if (!f) return;
            const target = new THREE.Vector3(...f.origin).addScaledVector(new THREE.Vector3(...f.u), hold.planeA)
                .addScaledVector(new THREE.Vector3(...f.v), hold.planeB);
            tweener.to({ target, position: target.clone().addScaledVector(new THREE.Vector3(...f.normal), distanceMm) });
            loop.interact();
        },
        /** The hold pick at a client point ({ holdId, hold, facetId, point, plane } or null); no side effects. */
        pickAt: (clientX, clientY) => picker.pickAt(clientX, clientY),
        /** Calls `listener(pick)` on every hold tap; returns the unsubscribe function. */
        onPick(listener) {
            pickListeners.add(listener);
            return () => pickListeners.delete(listener);
        },
        /** Client coordinates of a hold's centre (tests, tutorials); null when it is off screen. */
        holdScreenPoint(id) {
            const hold = holdById(id);
            return hold ? screenPointOf(hold, holds.facets.get(hold.facetId), camera, renderer.domElement) : null;
        },
        /** Puts the camera at `position` looking at `target` ([x, y, z] mm): free orbit, no preset. */
        lookFrom(position, target) {
            onStart();
            tweener.to({ position: new THREE.Vector3(...position), target: new THREE.Vector3(...target) });
            loop.interact();
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
            loop.dispose();
            ro.disconnect();
            hintObserver.disconnect();
            window.removeEventListener('orientationchange', resize);
            picker.dispose();
            pickListeners.clear();
            overlay.dispose();
            ghosts.dispose();
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
