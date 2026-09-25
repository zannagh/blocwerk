// The Detail choice of the photo-real view (wall3d-splat.js, the toggle in wall3d-overlay.js),
// remembered per browser:
//   auto  — phones stop at the cool cap, desktops step up while frames are fast (wall3d-splat-ladder.js);
//   high  — phones may go up to the high cap (only offered where that lifts anything);
//   ultra — the full stored level on any device: no phone cap, no frame-time probe, no step-down for
//           slow frames or sustained load. Only a lost WebGL context still steps it down (for this view).

const DETAIL_KEY = 'bw.photoreal.detail';
const MODES = ['auto', 'high', 'ultra'];

/** The stored choice: 'auto' | 'high' | 'ultra'. */
export function storedDetail() {
    try {
        const v = localStorage.getItem(DETAIL_KEY);
        return MODES.includes(v) ? v : 'auto';
    } catch {
        return 'auto';
    }
}

export function storeDetail(mode) {
    try {
        localStorage.setItem(DETAIL_KEY, mode);
    } catch {
        // Private mode / storage off: the choice holds for this view only.
    }
}

/** The choices offered, in toggle order: High only where it lifts the cap (`highLifts`). */
export const detailChoices = highLifts => highLifts ? MODES : ['auto', 'ultra'];

/** The choice after `mode` in `choices` (the toggle cycles). */
export function nextDetail(mode, choices) {
    const i = choices.indexOf(mode);
    return choices[(i + 1) % choices.length];
}

/** "1.6M", "250k", or "full" for the unknown full-scene count. */
export function splatCount(n) {
    if (!(n > 0)) return 'full';
    if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(n >= 10_000_000 ? 0 : 1).replace(/\.0$/, '')}M`;
    return `${Math.round(n / 1000)}k`;
}

/** The toggle's label: the choice and the level being drawn, e.g. "Ultra · 1.6M". */
export function detailLabel(mode, splats) {
    const name = mode[0].toUpperCase() + mode.slice(1);
    return splats == null ? `Detail: ${name}` : `${name} · ${splatCount(splats)}`;
}
