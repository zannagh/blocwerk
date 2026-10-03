// Surroundings and teardown of the 3D wall view (wall3d.js): the floor mat, a person for scale, the
// lights, the stylesheet and theme colours, and freeing every GPU resource of the scene.
import * as THREE from '../lib/three/three.module.min.js';

/** Floor, a 1.75 m person where a climber stands (scale at a glance), and lights. Returns [floor, person]. */
export function buildSurroundings(scene, frame, bg) {
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

/** Frees every geometry, material and texture of `scene` (each once). */
export function disposeScene(scene) {
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

/** Frees the GPU copies of the facet photos (three.js re-uploads them if Photos mode shows again). */
export function releaseTextures(group) {
    group?.traverse(o => {
        const m = o.material;
        if (!m) return;
        for (const t of [m.map, m.alphaMap]) t?.dispose();
    });
}

/** Injects wall3d.css once; returns the link while it is still loading (null when already there). */
export function ensureStylesheet() {
    const href = new URL('../css/wall3d.css', import.meta.url).href;
    if ([...document.querySelectorAll('link[rel="stylesheet"]')].some(l => l.href === href)) return null;
    const link = document.createElement('link');
    link.rel = 'stylesheet';
    link.href = href;
    document.head.append(link);
    return link;
}

/** A CSS custom property of `el`, or `fallback` when unset. */
export function themeColor(el, name, fallback) {
    const v = getComputedStyle(el).getPropertyValue(name).trim();
    return v || fallback;
}
