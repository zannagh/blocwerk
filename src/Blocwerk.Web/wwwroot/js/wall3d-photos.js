// The rectified facet photos of the 3D wall view (wall3d-scene.js, wall3d-volumes.js), loaded lazily.
// They only show in the Photos mode, so the view starts with empty textures and downloads nothing
// until that mode is first picked (`load()`, from wall3d-modes.js). A mesh stays hidden until all of
// its textures have their image: an empty texture would draw black.
import * as THREE from '../lib/three/three.module.min.js';

/** `request` asks for a frame when a photo lands. */
export function createPhotoLoader(request) {
    const loader = new THREE.ImageLoader();
    const byUrl = new Map();        // url → { texture, ready: Promise<boolean> | null }
    const meshes = [];              // { mesh, urls }
    let started = null;

    function fetchImage(entry, url) {
        entry.ready ??= new Promise(resolve => loader.load(url, image => {
            entry.texture.image = image;
            entry.texture.needsUpdate = true;
            resolve(true);
        }, undefined, () => resolve(false)));
        return entry.ready;
    }

    async function reveal({ mesh, urls }) {
        const ok = await Promise.all(urls.map(u => fetchImage(byUrl.get(u), u)));
        if (!ok.every(Boolean)) return;
        mesh.visible = true;
        request();
    }

    return {
        /** The (shared, still empty) texture of `url`. */
        texture(url, colorSpace) {
            let entry = byUrl.get(url);
            if (!entry) {
                const texture = new THREE.Texture();
                texture.colorSpace = colorSpace;
                entry = { texture, ready: null };
                byUrl.set(url, entry);
            }
            return entry.texture;
        },
        /** Hides `mesh` until the textures of `urls` (made by `texture`) have their images. */
        add(mesh, urls) {
            mesh.visible = false;
            meshes.push({ mesh, urls });
            if (started) reveal(meshes[meshes.length - 1]);
        },
        /** Starts the downloads (once); resolves when every photo landed or failed. */
        load() {
            started ??= Promise.all(meshes.map(reveal));
            return started;
        },
    };
}
