// Served at /__gate/mount-splat.js (page.route) and imported into the page under test: mounts the app's own wall3d.js
// over one facet and switches it to photo-real with the given splat, as the 3D view does. The splat is drawn as it is
// (identity matrix): its pixels do not matter here, only what decoding, sorting and drawing it cost.
import { mount } from '/js/wall3d.js';

const FACET = {
    id: '0', segment: 0, name: 'gate wall', origin: [0, 0, 0], u: [1, 0, 0], v: [0, 0, 1], normal: [0, -1, 0],
    extent: { aMin: -2000, aMax: 2000, bMin: -1500, bMax: 1500 }, angleDeg: 0,
    corners: [[-2000, 0, -1500], [2000, 0, -1500], [2000, 0, 1500], [-2000, 0, 1500]],
};

// The splat's metres to the view's millimetres, column-major (the view's splatMatrix).
const METRES_TO_MM = [1000, 0, 0, 0, 0, 0, 1000, 0, 0, 1000, 0, 0, 0, 0, 0, 1];

// `splats`: the level's count, as the server's ladder states it. A phone is asked to tap Photo-real again before a level
// over its cap (wall3d-splat-ladder.js firstLevelWarning); the gate taps again, as a person would.
export async function mountSplat(splatUrl, splats) {
    const stage = document.createElement('div');
    stage.style.cssText = 'position:fixed;inset:0;z-index:10;background:#000';
    document.body.append(stage);
    const view = {
        wallId: 'gate', wallName: 'Gate', boulderId: null, markerSizeMm: 125, facets: [FACET], markers: [], holds: [],
        unplacedHoldCount: 0, textures: [], splatUrl, splatMatrix: METRES_TO_MM,
        splatLevels: [{ url: splatUrl, splats, sizeBytes: 0 }],
    };
    const started = performance.now();
    const handle = await mount(stage, view, { initialPreset: 'front' });
    await handle.photoReal(true);
    const confirmed = handle.state().mode !== 'photoreal';
    if (confirmed) {
        await handle.photoReal(true);
    }
    handle.renderNow();
    window.__gateWall3d = handle;
    return { ms: Math.round(performance.now() - started), mode: handle.state().mode, confirmed, shown: handle.stats().splat?.splats ?? null };
}
