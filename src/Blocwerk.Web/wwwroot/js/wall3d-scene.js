// Scene builders for the 3D wall view (wall3d.js). World frame = wall-geometry.json: millimetres,
// z up, the climber on the side each facet normal points to. Everything here returns plain
// three.js objects; wall3d.js owns their lifetime and disposes them.
import * as THREE from '../lib/three/three.module.min.js';

const GRID_MM = 250;
/** The back of the wall: bare plywood, a shade darker than the gridded front. */
const BACK_WOOD = 0xc4ab82;

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

const rectPoints = r => [[r.aMin, r.bMin], [r.aMax, r.bMin], [r.aMax, r.bMax], [r.aMin, r.bMax]];

/** A facet's plane outline, [a, b] counter-clockwise: its polygon (a triangle cut at its seam) or its extent rectangle. */
export function facetOutline(f) {
    return f.outline && f.outline.length >= 3 ? f.outline : rectPoints(f.extent);
}

/** The part of convex polygon `poly` ([a, b] points) inside rectangle `r` (Sutherland–Hodgman). */
function clipToRect(poly, r) {
    const edges = [[0, r.aMin, 1], [0, r.aMax, -1], [1, r.bMin, 1], [1, r.bMax, -1]];
    let out = poly;
    for (const [k, v, s] of edges) {
        const src = out;
        out = [];
        src.forEach((p, i) => {
            const q = src[(i + 1) % src.length];
            const sp = s * (p[k] - v), sq = s * (q[k] - v);
            if (sp >= 0) out.push(p);
            if ((sp >= 0) !== (sq >= 0)) {
                const t = sp / (sp - sq);
                out.push([p[0] + t * (q[0] - p[0]), p[1] + t * (q[1] - p[1])]);
            }
        });
        if (out.length < 3) return [];
    }
    return out;
}

/** A triangle fan over a convex polygon: `corners` world points, `planes` their [a, b]; UVs from uvFn(a, b). */
function polygonGeometry(corners, planes, uvFn) {
    const g = new THREE.BufferGeometry();
    const pos = [];
    corners.forEach(c => pos.push(c[0], c[1], c[2]));
    const uv = [];
    planes.forEach(([a, b]) => uv.push(...uvFn(a, b)));
    const idx = [];
    for (let i = 1; i < corners.length - 1; i++) idx.push(0, i, i + 1);
    g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
    g.setAttribute('uv', new THREE.Float32BufferAttribute(uv, 2));
    g.setIndex(idx);
    g.computeVertexNormals();
    return g;
}

/**
 * Plywood facets with outlined edges: the gridded front (the side the normal points to) and a plain
 * wood back, as two single-sided meshes. Everything lying on a facet is front-only
 * (wall3d-sides.js), so from behind the wall reads as the back of a board, never a mirrored photo.
 * Returns { group, meshes: Map(facetId → front mesh), backs: [back meshes] }.
 */
export function buildFacets(view, renderer) {
    const group = new THREE.Group();
    const grid = gridTexture(renderer);
    const offset = { polygonOffset: true, polygonOffsetFactor: 1, polygonOffsetUnits: 1 };
    const material = new THREE.MeshStandardMaterial({ map: grid, roughness: 0.92, metalness: 0, side: THREE.FrontSide, ...offset });
    const backMaterial = new THREE.MeshStandardMaterial({ color: BACK_WOOD, roughness: 0.95, metalness: 0, side: THREE.BackSide, ...offset });
    const backs = [];
    const edgeMat = new THREE.LineBasicMaterial({ color: 0x5b4630 });
    const meshes = new Map();
    for (const f of view.facets) {
        const geo = polygonGeometry(f.corners, facetOutline(f), (a, b) => [a / GRID_MM, b / GRID_MM]);
        const mesh = new THREE.Mesh(geo, material);
        mesh.userData.facet = f;
        const back = new THREE.Mesh(geo, backMaterial);
        back.userData.facet = f;
        group.add(mesh, back);
        meshes.set(f.id, mesh);
        backs.push(back);
        const loop = new THREE.BufferGeometry().setFromPoints(f.corners.map(v3));
        group.add(new THREE.LineLoop(loop, edgeMat));
    }
    return { group, meshes, backs };
}

/**
 * Rectified photos on their facets. A texture covers `bounds` of its facet plane (the worker's
 * convention: column i → a = aMin + (i + ½)·mmPerPx left to right, row j → b = bMax − (j + ½)·mmPerPx
 * top to bottom), so with three's default flipY the plane rect maps to UV 0..1 without any flip:
 * u = (a − aMin) / width, v = (b − bMin) / height. The quad is clipped to the facet's outline — the
 * texture's extra margin would otherwise overlap the neighbouring facets — and drawn a hair above
 * the plywood and the marker squares. `photos` (wall3d-photos.js) downloads them on the first switch to Photos.
 */
export function buildTextures(view, renderer, photos) {
    const group = new THREE.Group();
    const byId = new Map(view.facets.map(f => [f.id, f]));
    for (const t of view.textures || []) {
        const f = byId.get(t.facetId);
        const b = t.bounds;
        if (!f || !b || !(b.aMax > b.aMin) || !(b.bMax > b.bMin)) continue;
        const planes = clipToRect(facetOutline(f), b);
        if (planes.length < 3) continue;
        // Above the drawn marker squares (1.5 mm): the photo shows the real printed markers.
        const lift = v3(f.normal).multiplyScalar(3);
        const corners = planes
            .map(([a, bb]) => v3(f.origin).addScaledVector(v3(f.u), a).addScaledVector(v3(f.v), bb).add(lift).toArray());
        const geo = polygonGeometry(corners, planes, (a, bb) => [(a - b.aMin) / (b.aMax - b.aMin), (bb - b.bMin) / (b.bMax - b.bMin)]);
        const tex = photos.texture(t.url, THREE.SRGBColorSpace);
        tex.anisotropy = Math.min(8, renderer.capabilities.getMaxAnisotropy());
        const mesh = texturedMesh(geo, tex, t.maskUrl ? photos.texture(t.maskUrl, THREE.NoColorSpace) : null);
        mesh.userData.facetId = f.id;                // faded while ghosted (wall3d-ghost.js)
        photos.add(mesh, t.maskUrl ? [t.url, t.maskUrl] : [t.url]);
        group.add(mesh);
    }
    return group;
}

/**
 * A facet photo, optionally with its coverage mask as alpha (0 where no photo saw the spot; the worker
 * paints those parts black): the plywood facet 3 mm below then shows through instead. alphaTest drops
 * the fully uncovered pixels outright so they write no depth; the feathered seam blends. Drawn first
 * among the transparent objects so the (dimmed, transparent) hold outlines 5 mm up always blend over
 * it. Without a mask (older textures) the photo stays opaque, as before. Unlit: the photo already
 * carries the room's real light, and scene shading on top made the overhangs look far darker than the
 * vertical facets next to them.
 */
function texturedMesh(geo, tex, mask) {
    const mat = new THREE.MeshBasicMaterial({ map: tex, side: THREE.FrontSide });
    if (mask) {
        mask.colorSpace = THREE.NoColorSpace;   // a plain coverage value, not a colour
        Object.assign(mat, { alphaMap: mask, transparent: true, alphaTest: 0.02 });
    }
    const mesh = new THREE.Mesh(geo, mat);
    if (mask) mesh.renderOrder = -1;
    return mesh;
}

/**
 * The printed markers as small dark squares, lifted a millimetre off their facet. `sides`
 * (wall3d-sides.js) drops the squares of a ghosted facet.
 */
export function buildMarkers(view, sides) {
    const pos = [];
    const idx = [];
    const slots = [];
    for (const m of view.markers) {
        const c = m.corners.map(v3);
        const n = new THREE.Vector3().subVectors(c[1], c[0]).cross(new THREE.Vector3().subVectors(c[3], c[0])).normalize();
        // TL,TR,BR,BL: (TR-TL) × (BL-TL) points into the wall for a front-facing marker; lift the other way.
        const lift = n.multiplyScalar(-1.5);
        const base = pos.length / 3;
        c.forEach(p => { p.add(lift); pos.push(p.x, p.y, p.z); slots.push(sides.index.get(m.facetId) ?? 0); });
        // Wound to face out of the wall (front side only): from behind the board they are culled.
        idx.push(base, base + 2, base + 1, base, base + 3, base + 2);
    }
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
    g.setAttribute('facetIndex', new THREE.Float32BufferAttribute(slots, 1));
    g.setIndex(idx);
    return new THREE.Mesh(g, sides.cullBehind(new THREE.MeshBasicMaterial({ color: 0x17171c, side: THREE.FrontSide }), 'solid'));
}

/**
 * Tilts within this many degrees of plumb read as "vertical": a declared-vertical kickboard that
 * measures ±1–2° is solver noise, not a slab. (Mirrors SurfaceAngle in Blocwerk.Core.)
 */
export const VERTICAL_TOLERANCE_DEG = 2;
