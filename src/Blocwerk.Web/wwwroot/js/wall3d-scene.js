// Scene builders for the 3D wall view (wall3d.js). World frame = wall-geometry.json: millimetres,
// z up, the climber on the side each facet normal points to. Everything here returns plain
// three.js objects; wall3d.js owns their lifetime and disposes them.
import * as THREE from '../lib/three/three.module.min.js';

const GRID_MM = 250;

export const v3 = a => new THREE.Vector3(a[0], a[1], a[2]);

/** Facet basis (u, v, n) as a rotation matrix, so local x/y/z land on the facet's u/v/normal. */
export function facetBasis(f) {
    return new THREE.Matrix4().makeBasis(v3(f.u), v3(f.v), v3(f.normal));
}

/** A tiling plywood tile with a faint 250 mm grid line on its edges, so scale reads at a glance. */
function gridTexture(renderer) {
    const size = 128;
    const c = document.createElement('canvas');
    c.width = c.height = size;
    const g = c.getContext('2d');
    g.fillStyle = '#dcc7a1';
    g.fillRect(0, 0, size, size);
    for (let i = 0; i < 40; i++) {                 // soft wood grain
        g.strokeStyle = `rgba(150,110,60,${0.04 + Math.random() * 0.05})`;
        g.lineWidth = 1 + Math.random() * 2;
        const y = Math.random() * size;
        g.beginPath();
        g.moveTo(0, y);
        g.bezierCurveTo(size / 3, y + 4 - Math.random() * 8, 2 * size / 3, y - 4 + Math.random() * 8, size, y);
        g.stroke();
    }
    g.strokeStyle = 'rgba(90,60,30,0.38)';
    g.lineWidth = 2;
    g.strokeRect(1, 1, size - 2, size - 2);
    const tex = new THREE.CanvasTexture(c);
    tex.wrapS = tex.wrapT = THREE.RepeatWrapping;
    tex.colorSpace = THREE.SRGBColorSpace;
    tex.anisotropy = Math.min(8, renderer.capabilities.getMaxAnisotropy());
    return tex;
}

/** Two triangles over a facet's corner quad, UVs in plane mm / scale (+ offset). */
function quadGeometry(corners, extent, uvFn) {
    const g = new THREE.BufferGeometry();
    const pos = [];
    corners.forEach(c => pos.push(c[0], c[1], c[2]));
    const planes = [[extent.aMin, extent.bMin], [extent.aMax, extent.bMin], [extent.aMax, extent.bMax], [extent.aMin, extent.bMax]];
    const uv = [];
    planes.forEach(([a, b]) => uv.push(...uvFn(a, b)));
    g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
    g.setAttribute('uv', new THREE.Float32BufferAttribute(uv, 2));
    g.setIndex([0, 1, 2, 0, 2, 3]);
    g.computeVertexNormals();
    return g;
}

/** Plywood facets with outlined edges. Returns { group, meshes: Map(facetId → mesh) }. */
export function buildFacets(view, renderer) {
    const group = new THREE.Group();
    const grid = gridTexture(renderer);
    const material = new THREE.MeshStandardMaterial({
        map: grid, roughness: 0.92, metalness: 0, side: THREE.DoubleSide,
        polygonOffset: true, polygonOffsetFactor: 1, polygonOffsetUnits: 1,
    });
    const edgeMat = new THREE.LineBasicMaterial({ color: 0x5b4630 });
    const meshes = new Map();
    for (const f of view.facets) {
        const geo = quadGeometry(f.corners, f.extent, (a, b) => [a / GRID_MM, b / GRID_MM]);
        const mesh = new THREE.Mesh(geo, material);
        mesh.userData.facet = f;
        group.add(mesh);
        meshes.set(f.id, mesh);
        const loop = new THREE.BufferGeometry().setFromPoints(f.corners.map(v3));
        group.add(new THREE.LineLoop(loop, edgeMat));
    }
    return { group, meshes };
}

/**
 * Rectified photos on their facets. A texture covers `bounds` of its facet plane (the worker's
 * convention: column i → a = aMin + (i + ½)·mmPerPx left to right, row j → b = bMax − (j + ½)·mmPerPx
 * top to bottom), so with three's default flipY the plane rect maps to UV 0..1 without any flip:
 * u = (a − aMin) / width, v = (b − bMin) / height. The quad is clipped to the facet's extent — the
 * texture's extra margin would otherwise overlap the neighbouring facets — and drawn a hair above
 * the plywood and the marker squares.
 */
export function buildTextures(view, renderer) {
    const group = new THREE.Group();
    const loader = new THREE.TextureLoader();
    const byId = new Map(view.facets.map(f => [f.id, f]));
    for (const t of view.textures || []) {
        const f = byId.get(t.facetId);
        const b = t.bounds;
        if (!f || !b || !(b.aMax > b.aMin) || !(b.bMax > b.bMin)) continue;
        const e = f.extent;
        const r = {
            aMin: Math.max(b.aMin, e.aMin), aMax: Math.min(b.aMax, e.aMax),
            bMin: Math.max(b.bMin, e.bMin), bMax: Math.min(b.bMax, e.bMax),
        };
        if (!(r.aMax > r.aMin) || !(r.bMax > r.bMin)) continue;
        // Above the drawn marker squares (1.5 mm): the photo shows the real printed markers.
        const lift = v3(f.normal).multiplyScalar(3);
        const corners = [[r.aMin, r.bMin], [r.aMax, r.bMin], [r.aMax, r.bMax], [r.aMin, r.bMax]]
            .map(([a, bb]) => v3(f.origin).addScaledVector(v3(f.u), a).addScaledVector(v3(f.v), bb).add(lift).toArray());
        const geo = quadGeometry(corners, r, (a, bb) => [(a - b.aMin) / (b.aMax - b.aMin), (bb - b.bMin) / (b.bMax - b.bMin)]);
        const tex = loader.load(t.url, () => renderer.__wall3dRequest?.());
        tex.colorSpace = THREE.SRGBColorSpace;
        tex.anisotropy = Math.min(8, renderer.capabilities.getMaxAnisotropy());
        const mat = new THREE.MeshStandardMaterial({ map: tex, roughness: 0.9, side: THREE.DoubleSide });
        group.add(new THREE.Mesh(geo, mat));
    }
    return group;
}

/** The printed markers as small dark squares, lifted a millimetre off their facet. */
export function buildMarkers(view) {
    const pos = [];
    const idx = [];
    for (const m of view.markers) {
        const c = m.corners.map(v3);
        const n = new THREE.Vector3().subVectors(c[1], c[0]).cross(new THREE.Vector3().subVectors(c[3], c[0])).normalize();
        // TL,TR,BR,BL: (TR-TL) × (BL-TL) points into the wall for a front-facing marker; lift the other way.
        const lift = n.multiplyScalar(-1.5);
        const base = pos.length / 3;
        c.forEach(p => { p.add(lift); pos.push(p.x, p.y, p.z); });
        idx.push(base, base + 1, base + 2, base, base + 2, base + 3);
    }
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
    g.setIndex(idx);
    return new THREE.Mesh(g, new THREE.MeshBasicMaterial({ color: 0x17171c, side: THREE.DoubleSide }));
}

/** A screen-sized text label (sizeAttenuation off, so it stays legible at any zoom). */
export function label(text, sub) {
    const c = document.createElement('canvas');
    const g = c.getContext('2d');
    const font = '600 30px system-ui, -apple-system, "Segoe UI", sans-serif';
    const subFont = '500 24px system-ui, -apple-system, "Segoe UI", sans-serif';
    g.font = font;
    const w1 = g.measureText(text).width;
    g.font = subFont;
    const w2 = sub ? g.measureText(sub).width : 0;
    c.width = Math.ceil(Math.max(w1, w2) + 28);
    c.height = sub ? 76 : 46;
    g.fillStyle = 'rgba(22,22,42,0.78)';
    g.beginPath();
    g.roundRect(0, 0, c.width, c.height, 12);
    g.fill();
    g.fillStyle = '#fff';
    g.font = font;
    g.textBaseline = 'top';
    g.fillText(text, 14, 8);
    if (sub) {
        g.font = subFont;
        g.fillStyle = '#ffd08a';
        g.fillText(sub, 14, 42);
    }
    const tex = new THREE.CanvasTexture(c);
    tex.colorSpace = THREE.SRGBColorSpace;
    const sprite = new THREE.Sprite(new THREE.SpriteMaterial({ map: tex, depthTest: false, sizeAttenuation: false }));
    const h = sub ? 0.075 : 0.045;
    sprite.userData.baseScale = [h * c.width / c.height, h];
    sprite.scale.set(h * c.width / c.height, h, 1);
    sprite.renderOrder = 20;
    return sprite;
}

/**
 * Tilts within this many degrees of plumb read as "vertical": a declared-vertical kickboard that
 * measures ±1–2° is solver noise, not a slab.
 */
export const VERTICAL_TOLERANCE_DEG = 2;

/** "45.4° overhang" / "vertical" / "12.0° slab" for a facet's measured tilt from vertical. */
export function angleText(deg, normal) {
    if (deg == null) return null;
    if (Math.abs(deg) < VERTICAL_TOLERANCE_DEG) return 'vertical';
    // The normal's z sign tells overhang (faces down) from slab (faces up).
    const kind = normal && normal[2] > 0.01 ? 'slab' : 'overhang';
    return `${Math.abs(deg).toFixed(1)}° ${kind}`;
}

/**
 * Screen-sized sprites scale with the viewport HEIGHT, so on a portrait phone they would be as
 * wide as the screen. Shrink them with the aspect ratio (never below half size).
 */
export function fitLabels(group, aspect) {
    const k = Math.max(0.5, Math.min(1, aspect / 1.3));
    group.children.forEach(s => {
        if (!s.userData.baseScale) return;          // the leader lines (wall3d-labels.js)
        const [w, h] = s.userData.baseScale;
        s.scale.set(w * k, h * k, 1);
    });
}

/** Facet name + angle labels, placed near each facet's top edge. */
export function buildLabels(view) {
    const group = new THREE.Group();
    for (const f of view.facets) {
        const e = f.extent;
        const a = (e.aMin + e.aMax) / 2;
        const b = e.bMax - Math.min(250, (e.bMax - e.bMin) * 0.15);
        const p = v3(f.origin).addScaledVector(v3(f.u), a).addScaledVector(v3(f.v), b).addScaledVector(v3(f.normal), 60);
        const s = label(f.name, angleText(f.angleDeg, f.normal));
        s.position.copy(p);
        group.add(s);
    }
    return group;
}
