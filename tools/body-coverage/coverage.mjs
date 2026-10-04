// Camera-sweep check for the 3D wall body (wwwroot/js/wall3d-body.js): viewer-only added geometry (body blocks,
// caps, lips, recess closing) must never cover a real textured facet from a viewpoint the camera can reach.
// For a grid of reachable camera positions, every facet is sampled; a sample the camera sees (no other real
// facet in between) must not have a body triangle in front of it. A facet's block is not drawn while the camera
// is behind that facet's plane, except the main wall's (wall3d-occlude.js pieceHidden), the sweep does the same.
//   node tools/body-coverage/coverage.mjs [view.json]        (default: attic-view.json, from the C# builder)
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const js = path.resolve(process.env.WWWROOT_JS || path.join(here, '../../src/Blocwerk.Web/wwwroot/js'));
const THREE = await import(`${js}/../lib/three/three.module.min.js`);
const { buildBody } = await import(`${js}/wall3d-body.js`);

const view = JSON.parse(fs.readFileSync(process.argv[2] || path.join(here, 'attic-view.json'), 'utf8'));
const facets = view.facets;
const body = buildBody({ facets, recesses: view.recesses || [] });
const v3 = a => new THREE.Vector3(a[0], a[1], a[2]);

// Reach rules of wall3d-reach.js: CAMERA_GAP in front of the main plane, above the floor, under the ceiling.
const CAMERA_GAP_MM = 80, FLOOR_GAP_MM = 200, CEILING_GAP_MM = 150;
const EPS_MM = 1.5, STEP_MM = 450, SAMPLES = 6;

/** World triangles ([a, b, c] Vector3s) of every mesh of the body group. */
function bodyTriangles() {
    const out = [];
    body.group.updateMatrixWorld(true);
    for (const m of body.group.children) {
        const pos = m.geometry.attributes.position;
        const index = m.geometry.index;
        const at = i => new THREE.Vector3().fromBufferAttribute(pos, i).applyMatrix4(m.matrixWorld);
        const n = index ? index.count : pos.count;
        for (let i = 0; i < n; i += 3) {
            const [a, b, c] = [0, 1, 2].map(k => at(index ? index.getX(i + k) : i + k));
            out.push({ tri: [a, b, c], piece: m.userData.facetId ?? (m.userData.roof ? 'roof' : 'cap/lip') });
        }
    }
    return out;
}

/** Ray/triangle distance (two-sided Möller–Trumbore), Infinity when missed. */
function hit(o, d, [a, b, c]) {
    const e1 = b.clone().sub(a), e2 = c.clone().sub(a);
    const p = d.clone().cross(e2), det = e1.dot(p);
    if (Math.abs(det) < 1e-9) return Infinity;
    const s = o.clone().sub(a), u = s.dot(p) / det;
    if (u < 0 || u > 1) return Infinity;
    const q = s.clone().cross(e1), v = d.dot(q) / det;
    if (v < 0 || u + v > 1) return Infinity;
    const t = e2.dot(q) / det;
    return t > 0 ? t : Infinity;
}

const byId = new Map(facets.map(f => [f.id, f]));
const mainId = (facets.find(f => f.id === '0') || facets[0]).id;
/** wall3d-occlude.js pieceHidden: a facet's block hides while the camera is behind that facet (not the main wall's). */
const hiddenBlock = (piece, cam) => {
    const f = byId.get(piece);
    return !!f && f.id !== mainId && v3(f.normal).dot(cam.clone().sub(v3(f.origin))) < -1;
};

const facetTris = facets.map(f => {
    const c = f.corners.map(v3);
    return c.slice(1, -1).map((_, i) => [c[0], c[i + 1], c[i + 2]]);
});
const bodyTris = bodyTriangles();
const nearest = (list, o, d) => list.reduce((best, t) => Math.min(best, hit(o, d, t.tri || t)), Infinity);

/** Sample points inside a convex facet: a barycentric grid over its corner fan, pulled slightly inward. */
function samples(f) {
    const c = f.corners.map(v3), n = v3(f.normal), mid = c.reduce((s, p) => s.add(p), new THREE.Vector3()).divideScalar(c.length);
    const out = [];
    for (let k = 1; k < c.length - 1; k++) {
        for (let i = 0; i <= SAMPLES; i++) {
            for (let j = 0; j <= SAMPLES - i; j++) {
                const p = c[0].clone().multiplyScalar(1 - (i + j) / SAMPLES).addScaledVector(c[k], i / SAMPLES).addScaledVector(c[k + 1], j / SAMPLES);
                out.push(p.lerp(mid, 0.04).addScaledVector(n, EPS_MM));
            }
        }
    }
    return out;
}

const corners = facets.flatMap(f => f.corners.map(v3));
const box = new THREE.Box3().setFromPoints(corners);
const main = facets.find(f => f.id === '0') || facets[0];
const mainN = v3(main.normal), mainO = v3(main.origin);
const reachable = p => mainN.dot(p.clone().sub(mainO)) >= CAMERA_GAP_MM && p.z >= box.min.z + FLOOR_GAP_MM && p.z <= box.max.z - CEILING_GAP_MM;

const cams = [];
for (let x = box.min.x - 4000; x <= box.max.x + 6000; x += STEP_MM) {
    for (let y = box.min.y - 5000; y <= box.max.y + 2500; y += STEP_MM) {
        for (let z = box.min.z + 300; z <= box.max.z; z += 350) {
            const p = new THREE.Vector3(x, y, z);
            if (reachable(p)) cams.push(p);
        }
    }
}

const covered = new Map(facets.map(f => [f.id, { pairs: 0, hidden: 0, cams: new Set(), by: new Map() }]));
const samplePoints = facets.map(samples);
for (const cam of cams) {
    facets.forEach((f, fi) => {
        const n = v3(f.normal), stat = covered.get(f.id);
        if (n.dot(cam.clone().sub(v3(f.origin))) <= 0) return;                  // the camera is behind this facet
        for (const p of samplePoints[fi]) {
            const d = p.clone().sub(cam), dist = d.length();
            d.divideScalar(dist);
            const real = facetTris.reduce((m, tris, gi) => (gi === fi ? m : Math.min(m, nearest(tris, cam, d))), Infinity);
            if (real < dist - EPS_MM) continue;                                 // another facet hides it anyway
            stat.pairs++;
            let first = null, tb = dist - EPS_MM;
            for (const b of bodyTris) {
                if (hiddenBlock(b.piece, cam)) continue;
                const t = hit(cam, d, b.tri);
                if (t < tb) { tb = t; first = b.piece; }
            }
            if (first) {
                stat.hidden++;
                stat.cams.add(`${Math.round(cam.x)},${Math.round(cam.y)},${Math.round(cam.z)}`);
                stat.by.set(first, (stat.by.get(first) || 0) + 1);
            }
        }
    });
}

let bad = 0;
console.log(`${cams.length} reachable cameras, ${facets.length} facets, ${bodyTris.length} body triangles`);
for (const f of facets) {
    const s = covered.get(f.id);
    const by = [...s.by].map(([k, v]) => `${k}:${v}`).join(' ');
    const ok = s.hidden === 0;
    if (!ok) bad++;
    console.log(`${ok ? 'PASS' : 'FAIL'}  facet ${f.id}: ${s.hidden}/${s.pairs} visible samples covered from ${s.cams.size} cameras${by ? `  [by ${by}]` : ''}`);
    if (!ok) console.log(`      e.g. cameras ${[...s.cams].slice(0, 4).join(' | ')}`);
}
console.log(bad === 0 ? '\nno real facet is covered by body geometry' : `\n${bad} facet(s) covered by body geometry`);
process.exit(bad === 0 ? 0 : 1);
