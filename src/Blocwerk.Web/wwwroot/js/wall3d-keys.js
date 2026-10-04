// Keyboard control of the 3D wall view (wall3d.js), for when the stage itself has focus:
//   arrow keys orbit, + / - zoom, Shift+arrows or PageUp / PageDown pan, Home resets the view,
//   ] / [ (or N / P) step to the next / previous hold (halo + spoken description), Enter opens its card,
//   Escape clears the selection. Tab is never trapped: it moves on to the presets and the mode switch.
// The stage gets role="application", a visually hidden instruction text and a live region that
// announces the current hold (styled by wall3d.css, with the focus ring).
import * as THREE from '../lib/three/three.module.min.js';
import { createHoldNav } from './wall3d-hold-nav.js';

const UP = new THREE.Vector3(0, 0, 1);
const ORBIT_RAD = THREE.MathUtils.degToRad(5);
const POLAR_MIN = 0.1;
const ZOOM_STEP = 0.88;
const PAN_FRACTION = 0.06;
const ARROWS = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, 1], ArrowDown: [0, -1] };
const CAMERA_KEYS = ['+', '=', '-', '_', 'PageUp', 'PageDown', 'Home'];

const HELP = 'Use the arrow keys to orbit, plus and minus to zoom, Shift with the arrow keys or Page Up and Page Down '
    + 'to pan, and Home to reset the view. Press the right or left square bracket, or N and P, to move to the next or '
    + 'previous hold, Enter to open its details and Escape to close them.';
let instances = 0;

function hidden(tag, id, live) {
    const e = document.createElement(tag);
    e.className = 'w3d-sr-only';
    if (id) {
        e.id = id;
    }
    if (live) {
        e.setAttribute('role', 'status');
        e.setAttribute('aria-live', 'polite');
        e.setAttribute('aria-atomic', 'true');
    }
    return e;
}

/**
 * `o`: { container, camera, controls, holds, picker, boulder, frame, ui } plus the host's callbacks:
 * moved() (the camera was moved by hand), reset(), select(hold) (halo), open(hold) (card), clear(),
 * reveal(hold) (bring an off-screen hold into view). Returns { sync(hold | null), dispose() };
 * `sync` keeps the keyboard's current hold equal to a tapped one.
 */
export function createKeyboard(o) {
    const { container, camera, controls, ui } = o;
    const nav = createHoldNav({
        holds: o.holds, picker: o.picker, camera, canvas: container.querySelector('canvas'),
        boulder: o.boulder, floorZ: o.frame.floorZ, right: o.frame.right,
    });
    const helpId = `w3d-help-${++instances}`;
    const help = hidden('p', helpId, false);
    help.textContent = HELP;
    const live = hidden('div', null, true);
    container.append(help, live);
    container.tabIndex = 0;
    container.setAttribute('role', 'application');
    container.setAttribute('aria-label', '3D wall view');
    container.setAttribute('aria-describedby', helpId);
    let current = null;
    let sayTimer = 0;

    // Clearing first makes a repeated message announce again.
    const say = text => {
        live.textContent = '';
        clearTimeout(sayTimer);
        sayTimer = setTimeout(() => { live.textContent = text; }, 30);
    };

    function orbit(dx, dy) {
        const offset = camera.position.clone().sub(controls.target);
        offset.applyAxisAngle(UP, dx * ORBIT_RAD);
        const polar = offset.angleTo(UP);
        const turn = polar - THREE.MathUtils.clamp(polar - dy * ORBIT_RAD, POLAR_MIN, Math.PI - POLAR_MIN);
        const axis = new THREE.Vector3().crossVectors(offset, UP);
        if (axis.lengthSq() > 1e-9) {
            offset.applyAxisAngle(axis.normalize(), turn);
        }
        camera.position.copy(controls.target).add(offset);
    }

    function zoom(factor) {
        const offset = camera.position.clone().sub(controls.target);
        offset.setLength(THREE.MathUtils.clamp(offset.length() * factor, controls.minDistance, controls.maxDistance));
        camera.position.copy(controls.target).add(offset);
    }

    function pan(dx, dy) {
        const step = camera.position.distanceTo(controls.target) * PAN_FRACTION;
        const move = new THREE.Vector3().setFromMatrixColumn(camera.matrix, 0).multiplyScalar(dx * step)
            .addScaledVector(new THREE.Vector3().setFromMatrixColumn(camera.matrix, 1), dy * step);
        camera.position.add(move);
        controls.target.add(move);
    }

    /** Camera keys; true when `e` was one. */
    function cameraKey(e) {
        const a = ARROWS[e.key];
        if (!a && !CAMERA_KEYS.includes(e.key)) {
            return false;
        }
        if (e.key === 'Home') {
            current = null;
            o.reset();
            say('View reset');
            return true;
        }
        o.moved();
        if (a && e.shiftKey) {
            pan(a[0], a[1]);
        } else if (a) {
            orbit(a[0], a[1]);
        } else if (e.key === 'PageUp' || e.key === 'PageDown') {
            pan(0, e.key === 'PageUp' ? 1 : -1);
        } else {
            zoom(e.key === '-' || e.key === '_' ? 1 / ZOOM_STEP : ZOOM_STEP);
        }
        return true;
    }

    function cycle(dir) {
        const list = nav.list();
        if (!list.length) {
            say('No holds in view');
            return;
        }
        const at = list.findIndex(h => h.id === current?.id);
        const i = at < 0 ? (dir > 0 ? 0 : list.length - 1) : (at + dir + list.length) % list.length;
        const cardOpen = !ui.card.hidden;
        current = list[i];
        if (!nav.onScreen(current)) {
            o.reveal(current);
        }
        if (cardOpen) {
            o.open(current);
        } else {
            o.select(current);
        }
        say(nav.describe(current, i, list.length));
    }

    function onKeyDown(e) {
        if (e.key === 'Escape' && (current || !ui.card.hidden)) {
            current = null;
            o.clear();
            e.preventDefault();
            e.stopPropagation();
            say('Selection cleared');
            return;
        }
        if (e.target !== container) {
            return;
        }
        const bracket = e.key === '[' || e.key === ']';   // AltGr / Option on many layouts
        if (!bracket && (e.ctrlKey || e.metaKey || e.altKey)) {
            return;
        }
        const k = e.key.toLowerCase();
        let handled = true;
        if (k === ']' || k === 'n') {
            cycle(1);
        } else if (k === '[' || k === 'p') {
            cycle(-1);
        } else if (e.key === 'Enter' && current) {
            o.open(current);
        } else {
            handled = cameraKey(e);
        }
        if (handled) {
            e.preventDefault();
        }
    }
    container.addEventListener('keydown', onKeyDown);

    return {
        sync(hold) { current = hold; },
        dispose() {
            clearTimeout(sayTimer);
            container.removeEventListener('keydown', onKeyDown);
            for (const a of ['tabindex', 'role', 'aria-label', 'aria-describedby']) {
                container.removeAttribute(a);
            }
        },
    };
}
