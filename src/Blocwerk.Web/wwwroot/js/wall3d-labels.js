// Screen-space de-collision of the facet labels (wall3d-scene.js buildLabels). Run once per drawn
// frame: projects each label's anchor, places the labels nearest-first and nudges a later one
// vertically out of whatever it overlaps — an earlier label or an overlay control (photo-real
// toggle, hint bubble, plan map). A nudged label gets a thin leader line back to its anchor; one
// that cannot be freed within MAX_SHIFT label heights stays put and fades instead. The shift is a
// sprite `center` offset (pure screen space), so the anchors never move. No per-frame allocation.
import * as THREE from '../lib/three/three.module.min.js';

const GAP_PX = 4;              // breathing room between boxes
const EDGE_PX = 6;             // keep labels this far inside the canvas
const MAX_SHIFT = 2.5;         // in label heights; beyond this a label fades instead of moving
const MAX_PASSES = 6;          // bounded, deterministic resolution loop
const FADED_OPACITY = 0.35;
const MAX_OBSTACLES = 8;

/** Creates the layout for `group` (sprites) and adds its leader lines to `group`. */
export function createLabelLayout(group, camera, canvas) {
    const sprites = group.children.slice();
    const n = sprites.length;
    const anchors = sprites.map(s => s.position.clone());
    const box = new Float32Array(n * 4);          // x0, y0, x1, y1 in canvas px, after placement
    const depth = new Float32Array(n);
    const order = Int32Array.from({ length: n }, (_, i) => i);
    const placed = new Uint8Array(n);
    const obstacles = new Float32Array(MAX_OBSTACLES * 4);
    let obstacleCount = 0;
    const tmp = new THREE.Vector3();

    const linePos = new Float32Array(Math.max(1, n) * 6);
    const lineGeo = new THREE.BufferGeometry();
    lineGeo.setAttribute('position', new THREE.BufferAttribute(linePos, 3));
    const leaders = new THREE.LineSegments(lineGeo, new THREE.LineBasicMaterial({
        color: 0x16162a, transparent: true, opacity: 0.7, depthTest: false }));
    leaders.renderOrder = 19;
    leaders.frustumCulled = false;
    group.add(leaders);

    /** Records the overlay elements' rectangles (canvas px). Call on resize / overlay changes. */
    function measure(elements) {
        const c = canvas.getBoundingClientRect();
        obstacleCount = 0;
        for (const el of elements) {
            if (!el || obstacleCount >= MAX_OBSTACLES || el.hidden || el.classList.contains('gone')) {
                continue;
            }
            const r = el.getBoundingClientRect();
            if (r.width === 0 || r.height === 0) {
                continue;
            }
            obstacles.set([r.left - c.left, r.top - c.top, r.right - c.left, r.bottom - c.top], obstacleCount * 4);
            obstacleCount++;
        }
    }

    function overlaps(x0, y0, x1, y1, list, k) {
        const o = k * 4;
        return !(x1 + GAP_PX <= list[o] || list[o + 2] + GAP_PX <= x0 || y1 + GAP_PX <= list[o + 1] || list[o + 3] + GAP_PX <= y0);
    }

    /** Whether box i overlaps anything placed so far; the blocker's top/bottom land in `hit`. */
    const hit = new Float32Array(2);
    function blocker(i) {
        const o = i * 4;
        for (let k = 0; k < obstacleCount; k++) {
            if (overlaps(box[o], box[o + 1], box[o + 2], box[o + 3], obstacles, k)) {
                hit[0] = obstacles[k * 4 + 1];
                hit[1] = obstacles[k * 4 + 3];
                return true;
            }
        }
        for (let j = 0; j < n; j++) {
            if (placed[j] && overlaps(box[o], box[o + 1], box[o + 2], box[o + 3], box, j)) {
                hit[0] = box[j * 4 + 1];
                hit[1] = box[j * 4 + 3];
                return true;
            }
        }
        return false;
    }

    /** Projects every anchor into box (canvas px) and depth (along the view axis; ≤ 0 is behind). */
    function project(w, h) {
        const pxPerUnit = camera.projectionMatrix.elements[5] * h / 2;   // sizeAttenuation off
        for (let i = 0; i < n; i++) {
            const s = sprites[i];
            tmp.copy(anchors[i]).applyMatrix4(camera.matrixWorldInverse);
            depth[i] = -tmp.z;
            tmp.applyMatrix4(camera.projectionMatrix);
            const sx = (tmp.x + 1) / 2 * w;
            const sy = (1 - tmp.y) / 2 * h;
            const bw = s.scale.x * pxPerUnit;
            const bh = s.scale.y * pxPerUnit;
            const o = i * 4;
            box[o] = sx - bw / 2;
            box[o + 1] = sy - bh / 2;
            box[o + 2] = sx + bw / 2;
            box[o + 3] = sy + bh / 2;
        }
    }

    function sortByDepth() {
        for (let i = 1; i < n; i++) {                 // insertion sort: tiny n, stable, no allocation
            const v = order[i];
            let j = i - 1;
            while (j >= 0 && (depth[order[j]] > depth[v] || (depth[order[j]] === depth[v] && order[j] > v))) {
                order[j + 1] = order[j];
                j--;
            }
            order[j + 1] = v;
        }
    }

    /** Clamps box i horizontally into the canvas; returns the x shift. */
    function clampX(i, w) {
        const o = i * 4;
        let dx = 0;
        if (box[o + 2] > w - EDGE_PX) {
            dx = w - EDGE_PX - box[o + 2];
        }
        if (box[o] + dx < EDGE_PX) {
            dx = EDGE_PX - box[o];
        }
        box[o] += dx;
        box[o + 2] += dx;
        return dx;
    }

    /** Moves box i vertically until free; returns the y shift, or NaN when it cannot be freed. */
    function resolveY(i, h) {
        const o = i * 4;
        const bh = box[o + 3] - box[o + 1];
        const y0 = box[o + 1];
        for (let pass = 0; pass < MAX_PASSES && blocker(i); pass++) {
            const up = hit[0] - GAP_PX - box[o + 3];          // negative: move above the blocker
            const down = hit[1] + GAP_PX - box[o + 1];        // positive: move below it
            const canUp = box[o + 1] + up >= EDGE_PX;
            const canDown = box[o + 3] + down <= h - EDGE_PX;
            const dy = canUp && (!canDown || -up <= down) ? up : canDown ? down : 0;
            if (dy === 0) {
                break;
            }
            box[o + 1] += dy;
            box[o + 3] += dy;
        }
        const shift = box[o + 1] - y0;
        if (blocker(i) || Math.abs(shift) > MAX_SHIFT * bh) {
            box[o + 1] = y0;
            box[o + 3] = y0 + bh;
            return NaN;
        }
        return shift;
    }

    function setLeader(slot, i, sx, sy, w, h) {
        // The label's shifted centre, unprojected at the anchor's depth.
        tmp.copy(anchors[i]).project(camera);
        tmp.set(sx / w * 2 - 1, 1 - sy / h * 2, tmp.z).unproject(camera);
        const a = anchors[i];
        const o = slot * 6;
        linePos[o] = a.x; linePos[o + 1] = a.y; linePos[o + 2] = a.z;
        linePos[o + 3] = tmp.x; linePos[o + 4] = tmp.y; linePos[o + 5] = tmp.z;
    }

    /** Lays out every label for the camera's current pose. */
    function update() {
        const w = canvas.clientWidth || 1;
        const h = canvas.clientHeight || 1;
        project(w, h);
        sortByDepth();
        placed.fill(0);
        let lines = 0;
        for (let r = 0; r < n; r++) {
            const i = order[r];
            const s = sprites[i];
            const o = i * 4;
            const bw = box[o + 2] - box[o];
            const bh = box[o + 3] - box[o + 1];
            const cx = (box[o] + box[o + 2]) / 2;
            const cy = (box[o + 1] + box[o + 3]) / 2;
            const dx = clampX(i, w);
            const dy = depth[i] > 0 ? resolveY(i, h) : 0;
            const faded = Number.isNaN(dy);
            s.center.set(0.5 - dx / bw, 0.5 + (faded ? 0 : dy) / bh);
            s.material.opacity = faded ? FADED_OPACITY : 1;
            s.renderOrder = 20 + (n - r);              // nearer labels draw on top
            placed[i] = !faded && depth[i] > 0 ? 1 : 0;
            if (!faded && Math.abs(dy) > 1) {
                setLeader(lines++, i, cx + dx, cy + dy + (dy < 0 ? bh / 2 : -bh / 2), w, h);
            }
        }
        lineGeo.setDrawRange(0, lines * 2);
        lineGeo.attributes.position.needsUpdate = true;
    }

    return { update, measure };
}
