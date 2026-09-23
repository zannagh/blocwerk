// DOM overlay of the 3D wall view (wall3d.js): preset buttons, hold info card, scale bar, the
// "you stand here" plan map and the photo-real toggle. Plain DOM, styled by /css/wall3d.css.

const PRESET_LABELS = { front: 'Front', below: 'Below', left: 'Left', right: 'Right', top: 'Top' };
const ROLE_LABELS = { Start: 'Start hold', Top: 'Top hold', Hand: 'Hand hold', Foot: 'Foot hold', ColorFoot: 'Foot (colour rule)' };
const NICE_MM = [50, 100, 200, 250, 500, 1000, 2000, 5000, 10000];

function el(tag, cls, text) {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text != null) e.textContent = text;
    return e;
}

const PHOTO_LABELS = { off: 'Photo-real (beta)', loading: 'Loading…', on: 'Facet model' };

/**
 * Builds the overlay into `root`. `on` carries the callbacks: preset(name), reset(), closeCard(),
 * photoReal() (toggle). `photoAvailable` enables the photo-real toggle.
 */
export function buildOverlay(root, view, on, photoAvailable) {
    const hint = el('div', 'w3d-hint', 'Drag to orbit · pinch or scroll to zoom · two fingers / right-drag to pan');
    const presets = el('div', 'w3d-presets');
    presets.setAttribute('role', 'toolbar');
    presets.setAttribute('aria-label', 'Camera views');
    for (const [name, text] of Object.entries(PRESET_LABELS)) {
        const b = el('button', 'w3d-btn', text);
        b.type = 'button';
        b.dataset.preset = name;
        b.addEventListener('click', () => on.preset(name));
        presets.append(b);
    }
    const reset = el('button', 'w3d-btn w3d-btn-reset', 'Reset');
    reset.type = 'button';
    reset.addEventListener('click', () => on.reset());
    presets.append(reset);

    const photo = el('button', 'w3d-btn w3d-photoreal', PHOTO_LABELS.off);
    photo.type = 'button';
    photo.disabled = !photoAvailable;
    photo.setAttribute('aria-pressed', 'false');
    photo.title = photoAvailable ? 'Show the photo-real capture of the wall' : 'No photo-real capture of this wall yet';
    photo.addEventListener('click', () => photoAvailable && on.photoReal());

    const scale = el('div', 'w3d-scale');
    const bar = el('div', 'w3d-scale-bar');
    const scaleText = el('span', 'w3d-scale-text');
    scale.append(bar, scaleText);

    const map = el('div', 'w3d-map');
    map.setAttribute('aria-hidden', 'true');

    const card = el('div', 'w3d-card');
    card.hidden = true;
    card.setAttribute('role', 'status');

    root.append(hint, photo, map, scale, card, presets);
    return {
        hint, presets, card, map, photo,
        setActive(name) {
            presets.querySelectorAll('[data-preset]').forEach(b => b.classList.toggle('active', b.dataset.preset === name));
        },
        hideHint() { hint.classList.add('gone'); },
        /** Photo-real toggle state: 'off' | 'loading' | 'on'. */
        setPhotoReal(state) {
            photo.textContent = PHOTO_LABELS[state];
            photo.disabled = !photoAvailable || state === 'loading';
            photo.setAttribute('aria-pressed', state === 'on' ? 'true' : 'false');
            photo.classList.toggle('active', state === 'on');
            root.classList.toggle('w3d-photo-on', state === 'on');
        },
        /** Shows a short message in the hint bubble. */
        say(text) { hint.textContent = text; hint.classList.remove('gone'); },
        /** Scale bar for `pxPerMm` pixels per millimetre at the orbit centre. */
        setScale(pxPerMm) {
            if (!(pxPerMm > 0)) return;
            const mm = NICE_MM.find(n => n * pxPerMm >= 56) ?? NICE_MM[NICE_MM.length - 1];
            bar.style.width = `${Math.round(mm * pxPerMm)}px`;
            scaleText.textContent = mm >= 1000 ? `${mm / 1000} m` : `${mm / 10} cm`;
        },
        showHold(h) { showCard(card, h, on.closeCard); },
        hideCard() { card.hidden = true; },
    };
}

function showCard(card, h, close) {
    card.replaceChildren();
    const head = el('div', 'w3d-card-head');
    const sw = el('span', 'w3d-swatch');
    sw.style.background = h.hex;
    head.append(sw, el('strong', null, `${h.colorName} ${h.isFoot ? 'foot' : 'hand'} hold`));
    const x = el('button', 'w3d-card-close', '×');
    x.type = 'button';
    x.setAttribute('aria-label', 'Close');
    x.addEventListener('click', close);
    head.append(x);
    const size = h.sizeMeasured
        ? `${Math.round(h.widthMm)} × ${Math.round(h.heightMm)} mm`
        : 'size not measured yet';
    const used = h.usageCount === 1 ? 'Used by 1 boulder' : `Used by ${h.usageCount} boulders`;
    card.append(head, el('div', 'w3d-card-line', size), el('div', 'w3d-card-line', used));
    if (h.role) card.append(el('div', `w3d-card-role role-${h.role}`, ROLE_LABELS[h.role] || h.role));
    card.hidden = false;
}

/**
 * Plan (top-down) map: facet outlines, a person where a climber stands in front of the wall,
 * and a wedge showing where the camera looks from. World +y (into the wall) is up on the map,
 * so the climber is at the bottom, as you'd sketch it.
 */
export function createPlanMap(host, view, frame) {
    const NS = 'http://www.w3.org/2000/svg';
    const size = 96;
    const pad = 10;
    const pts = view.facets.flatMap(f => f.corners);
    pts.push([frame.stand.x, frame.stand.y, 0]);
    const xs = pts.map(p => p[0]);
    const ys = pts.map(p => p[1]);
    const minX = Math.min(...xs), maxX = Math.max(...xs), minY = Math.min(...ys), maxY = Math.max(...ys);
    const s = (size - 2 * pad) / Math.max(maxX - minX, maxY - minY, 1);
    const ox = (size - (maxX - minX) * s) / 2;
    const oy = (size - (maxY - minY) * s) / 2;
    const px = x => ox + (x - minX) * s;
    const py = y => oy + (maxY - y) * s;
    const svg = document.createElementNS(NS, 'svg');
    svg.setAttribute('viewBox', `0 0 ${size} ${size}`);
    for (const f of view.facets) {
        const poly = document.createElementNS(NS, 'polygon');
        poly.setAttribute('points', f.corners.map(c => `${px(c[0]).toFixed(1)},${py(c[1]).toFixed(1)}`).join(' '));
        poly.setAttribute('class', 'w3d-map-facet');
        svg.append(poly);
    }
    const cam = document.createElementNS(NS, 'path');
    cam.setAttribute('class', 'w3d-map-cam');
    svg.append(cam);
    const you = document.createElementNS(NS, 'circle');
    you.setAttribute('cx', px(frame.stand.x));
    you.setAttribute('cy', py(frame.stand.y));
    you.setAttribute('r', 4);
    you.setAttribute('class', 'w3d-map-you');
    svg.append(you);
    const youText = document.createElementNS(NS, 'text');
    youText.setAttribute('x', px(frame.stand.x));
    youText.setAttribute('y', Math.min(size - 2, py(frame.stand.y) + 13));
    youText.setAttribute('class', 'w3d-map-label');
    youText.textContent = 'you';
    svg.append(youText);
    host.append(svg);

    const clamp = v => Math.max(4, Math.min(size - 4, v));
    return {
        /** Draws the camera wedge from `position` toward `target` (world vectors). */
        update(position, target) {
            const cx = clamp(px(position.x));
            const cy = clamp(py(position.y));
            const dx = px(target.x) - px(position.x);
            const dy = py(target.y) - py(position.y);
            const len = Math.hypot(dx, dy);
            if (len < 1e-3) {
                cam.setAttribute('d', `M${cx - 5},${cy} a5,5 0 1,0 10,0 a5,5 0 1,0 -10,0`);
                return;
            }
            const ux = dx / len, uy = dy / len;
            const l = 16, w = 8;
            const tx = cx + ux * l, ty = cy + uy * l;
            cam.setAttribute('d', `M${cx},${cy} L${tx - uy * w},${ty + ux * w} L${tx + uy * w},${ty - ux * w} Z`);
        },
    };
}
