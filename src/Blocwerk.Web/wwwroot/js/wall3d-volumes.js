// Volumes of the 3D wall view (wall3d.js), found without markers (Wall3DVolume: a height field over the
// facet, mm). Each becomes a surface draped over its facet from its grid of heights, falling to the wall at
// its rim. Two looks: `plain` (Schematic: light plywood, lit) and `photo` (Photos: textured from the facet's
// rectified photo). The photo painted a volume point where the TEXTURE CAMERA's ray through it meets the
// facet plane, so projecting each vertex from that camera onto the plane gives its texture coordinate and
// the photo lands back on the volume. Photo-real shows the captured volumes themselves, so neither is drawn.
// A volume with "flat sides" comes as planar faces instead (`faces`): flat triangles, and in Schematic its sheet edges.
import * as THREE from '../lib/three/three.module.min.js';
import { v3 } from './wall3d-scene.js';

const PLAIN_WOOD = 0xe4d2b0;
/** The sheet edges of flat-sided volumes (Schematic). */
const EDGE_MAT = new THREE.LineBasicMaterial({ color: 0x6b5232 });
/** Lift over the facet, mm: the rim cells meet the wall under the photo (3 mm) and markers. */
const LIFT = 3.5;

/** Height of cell (i, j), 0 outside the grid. */
function heightAt(vol, i, j) {
    if (i < 0 || j < 0 || i >= vol.cols || j >= vol.rows) return 0;
    return vol.heights[j * vol.cols + i];
}

/**
 * Display heights on the grid's nodes (one ring of cells beyond it, where the volume meets the wall): a 3×3
 * mean of the stored heights, so the 20 mm grid reads as a surface rather than steps. The data stays as stored.
 */
function smoothed(vol) {
    const n = (vol.cols + 2) * (vol.rows + 2);
    const out = new Float32Array(n);
    for (let j = -1; j <= vol.rows; j++) {
        for (let i = -1; i <= vol.cols; i++) {
            let sum = 0;
            for (let dj = -1; dj <= 1; dj++) for (let di = -1; di <= 1; di++) sum += heightAt(vol, i + di, j + dj);
            out[(j + 1) * (vol.cols + 2) + (i + 1)] = heightAt(vol, i, j) > 0 ? sum / 9 : 0;
        }
    }
    return out;
}

/** World position of a facet point (a, b) lifted h + LIFT along the normal. */
function worldOf(f, a, b, h) {
    return v3(f.origin).addScaledVector(v3(f.u), a).addScaledVector(v3(f.v), b).addScaledVector(v3(f.normal), h + LIFT);
}

/** Texture coordinate of a volume point: its projection from the texture camera onto the plane, in the texture's bounds. */
function uvOf(a, b, h, cam, bounds) {
    const t = cam[2] / Math.max(1, cam[2] - h);
    return [(cam[0] + (a - cam[0]) * t - bounds.aMin) / (bounds.aMax - bounds.aMin), (cam[1] + (b - cam[1]) * t - bounds.bMin) / (bounds.bMax - bounds.bMin)];
}

/**
 * The surface as an indexed grid over the cell centres (plus the rim ring at the wall), so normals are smooth:
 * a quad wherever any of its four corners stands proud.
 */
function geometryOf(vol, f, texture) {
    const w = vol.cols + 2;
    const hs = smoothed(vol);
    const count = w * (vol.rows + 2);
    const pos = new Float32Array(count * 3);
    const uv = texture ? new Float32Array(count * 2) : null;
    for (let k = 0; k < count; k++) {
        const a = vol.aLo + ((k % w) - 0.5) * vol.cellMm;
        const b = vol.bLo + (Math.floor(k / w) - 0.5) * vol.cellMm;
        const p = worldOf(f, a, b, hs[k]);
        pos.set([p.x, p.y, p.z], k * 3);
        if (uv) uv.set(uvOf(a, b, hs[k], vol.textureCamera, texture.bounds), k * 2);
    }
    const index = [];
    for (let j = 0; j < vol.rows + 1; j++) {
        for (let i = 0; i < w - 1; i++) {
            const k = j * w + i;
            if (hs[k] <= 0 && hs[k + 1] <= 0 && hs[k + w] <= 0 && hs[k + w + 1] <= 0) continue;
            index.push(k, k + 1, k + w + 1, k, k + w + 1, k + w);
        }
    }
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.BufferAttribute(pos, 3));
    if (uv) g.setAttribute('uv', new THREE.BufferAttribute(uv, 2));
    g.setIndex(index);
    g.computeVertexNormals();
    return g;
}

/**
 * A flat-sided volume (Wall3DVolume.faces: planar faces, corners [a, b, h] mm) as flat triangles: each face fanned
 * from its first corner, wound so it faces away from the wall whatever the facet frame's handedness.
 */
function flatGeometryOf(vol, f, texture) {
    const pos = [];
    const uv = [];
    const out = v3(f.normal);
    for (const face of vol.faces) {
        const pts = face.map(c => worldOf(f, c[0], c[1], c[2]));
        const n = new THREE.Vector3().subVectors(pts[1], pts[0]).cross(new THREE.Vector3().subVectors(pts[2], pts[0]));
        const flip = n.dot(out) < 0;
        for (let k = 1; k + 1 < face.length; k++) {
            const tri = flip ? [0, k + 1, k] : [0, k, k + 1];
            for (const i of tri) {
                pos.push(pts[i].x, pts[i].y, pts[i].z);
                if (texture) uv.push(...uvOf(face[i][0], face[i][1], face[i][2], vol.textureCamera, texture.bounds));
            }
        }
    }
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
    if (texture) g.setAttribute('uv', new THREE.Float32BufferAttribute(uv, 2));
    g.computeVertexNormals();
    return g;
}

/** The sheet edges of a flat-sided volume (Schematic): lines where neighbouring faces meet at an angle, and its rim. */
function edgesOf(geometry) {
    return new THREE.LineSegments(new THREE.EdgesGeometry(geometry, 8), EDGE_MAT);
}

/**
 * { plain, photo }: two groups with every volume of `view` (either may be empty); `photo` is added to the
 * facet photo group `textures`, so it shows and hides with the photos. `renderer` asks for a frame when a
 * photo lands. Volumes are one-sided (front only): from behind the wall they are hidden.
 */
export function buildVolumes(view, renderer, textures) {
    const plain = new THREE.Group();
    const photo = new THREE.Group();
    const facets = new Map(view.facets.map(f => [f.id, f]));
    const photoOf = new Map((view.textures || []).map(t => [t.facetId, t]));
    const loader = new THREE.TextureLoader();
    const photos = new Map();
    const plainMat = new THREE.MeshStandardMaterial({ color: PLAIN_WOOD, roughness: 0.9, metalness: 0, side: THREE.FrontSide });
    for (const vol of view.volumes || []) {
        const f = facets.get(vol.facetId);
        if (!f || !vol.heights || vol.heights.length !== vol.cols * vol.rows) continue;
        const flat = Array.isArray(vol.faces) && vol.faces.length >= 3;
        const plainMesh = new THREE.Mesh(flat ? flatGeometryOf(vol, f, null) : geometryOf(vol, f, null), plainMat);
        if (flat) plainMesh.add(edgesOf(plainMesh.geometry));
        plainMesh.userData = { facetId: f.id, volumeId: vol.id };
        plain.add(plainMesh);
        const tex = photoOf.get(vol.facetId);
        if (!tex || !vol.textureCamera) continue;
        if (!photos.has(tex.url)) {
            const map = loader.load(tex.url, () => renderer.__wall3dRequest?.());
            map.colorSpace = THREE.SRGBColorSpace;
            map.anisotropy = Math.min(8, renderer.capabilities.getMaxAnisotropy());
            photos.set(tex.url, new THREE.MeshBasicMaterial({ map, side: THREE.FrontSide }));
        }
        const photoMesh = new THREE.Mesh(flat ? flatGeometryOf(vol, f, tex) : geometryOf(vol, f, tex), photos.get(tex.url));
        photoMesh.userData = { facetId: f.id, volumeId: vol.id };
        photo.add(photoMesh);
    }
    textures.add(photo);
    return { plain, photo };
}
