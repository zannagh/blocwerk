// Level-of-detail ladder of the photo-real view (wall3d-splat.js). The server stores the splat scene
// pruned to a few sizes (SplatLodLadder: ~40k, 120k, 250k splats, then the full scene). A device
// starts on the smallest — it shows within a second, even on a phone — and steps up one level at a
// time while frames stay fast, up to a device-dependent cap. A lost WebGL context steps it back down
// and remembers, in this browser, the size that was too much, so the next visit stops below it.

/** Phones and low-memory devices never step past this many splats. */
const LIGHT_CAP_SPLATS = 800_000;
/** Frames skipped after a level shows (uploads, the first sorts) before its frame time counts. */
const WARMUP_FRAMES = 20;
/** Frames measured before deciding to step up. */
const SAMPLE_FRAMES = 45;
/** Median frame time (ms) at or under which a device steps up: light devices / desktops. */
const STEP_UP_MS = { light: 26, desktop: 45 };
/** Median frame time (ms) above which a level is too slow and the view steps back down. */
const STEP_DOWN_MS = 90;
const STORE_KEY = 'bw.photoreal.lod.v1';

/**
 * The view's levels, smallest first: `view.splatLevels` ({ url, splats, sizeBytes }), or for an older
 * payload the mobile copy and the full scene. The full scene's count may be 0 (unknown): it is last.
 */
export function ladderOf(view) {
    const given = (view.splatLevels || []).filter(l => l && l.url);
    if (given.length > 0) return given;
    const levels = [];
    if (view.splatMobileUrl) levels.push({ url: view.splatMobileUrl, splats: 180000, sizeBytes: 0 });
    if (view.splatUrl) levels.push({ url: view.splatUrl, splats: 0, sizeBytes: 0 });
    return levels;
}

function readStore() {
    try {
        const v = JSON.parse(localStorage.getItem(STORE_KEY) || 'null');
        return v && typeof v === 'object' ? v : {};
    } catch {
        return {};
    }
}

function writeStore(v) {
    try {
        localStorage.setItem(STORE_KEY, JSON.stringify(v));
    } catch {
        // Private mode / storage off: the device relearns its limit next visit.
    }
}

/** Splat count of a level, the unknown full scene counted as larger than anything. */
export const sizeOf = level => level.splats > 0 ? level.splats : Number.MAX_SAFE_INTEGER;

/** What this browser remembers: { failSplats, okSplats } (either may be missing). */
export const remembered = () => readStore();

/** Remembers that a level lost the context: the device stays below it from now on. */
export function rememberFailure(level) {
    const s = readStore();
    const size = sizeOf(level);
    s.failSplats = Math.min(s.failSplats ?? Number.MAX_SAFE_INTEGER, size);
    if (s.okSplats >= size) delete s.okSplats;
    s.at = Date.now();
    writeStore(s);
}

/** Remembers that a level ran fine, so a later visit steps up to it without measuring again. */
export function rememberSuccess(level) {
    const s = readStore();
    const size = sizeOf(level);
    if (s.failSplats != null && size >= s.failSplats) return;
    if ((s.okSplats ?? 0) >= size) return;
    s.okSplats = size;
    s.at = Date.now();
    writeStore(s);
}

/**
 * Highest level index this device may step up to: light devices stop below LIGHT_CAP_SPLATS, and
 * everyone below a size that lost the context here before (the smallest level is always allowed).
 * `?splatLevel=N` pins the level (testing); `?splatLod=full|mobile` pins the top / the first.
 */
export function levelCap(levels, light) {
    const q = new URLSearchParams(location.search);
    const pinned = pinnedLevel(levels.length, q);
    if (pinned != null) return pinned;
    const failed = readStore().failSplats ?? Number.MAX_SAFE_INTEGER;
    let cap = 0;
    for (let i = 1; i < levels.length; i++) {
        const size = sizeOf(levels[i]);
        if (size >= failed || (light && size > LIGHT_CAP_SPLATS)) break;
        cap = i;
    }
    return cap;
}

/** The level `?splatLevel=` / `?splatLod=` pins, or null. */
export function pinnedLevel(count, q = new URLSearchParams(location.search)) {
    const n = q.get('splatLevel');
    if (n != null && /^\d+$/.test(n)) return Math.min(+n, count - 1);
    const lod = q.get('splatLod');
    if (lod === 'full') return count - 1;
    if (lod === 'mobile') return 0;
    return null;
}

/**
 * Measures frame intervals of the level on show and decides: 'up' (fast enough to step up), 'down'
 * (too slow), or null (keep measuring / stay). `light` picks the stricter threshold.
 */
export function createFrameMonitor(light) {
    let last = 0;
    let seen = 0;
    let samples = [];
    const upMs = +(new URLSearchParams(location.search).get('splatStepMs') || (light ? STEP_UP_MS.light : STEP_UP_MS.desktop));
    return {
        reset() { last = 0; seen = 0; samples = []; },
        /** The last decision's median frame time, for the diagnostics. */
        median: 0,
        frame(now) {
            if (last) {
                seen++;
                if (seen > WARMUP_FRAMES) samples.push(now - last);
            }
            last = now;
            if (samples.length < SAMPLE_FRAMES) return null;
            const sorted = [...samples].sort((a, b) => a - b);
            this.median = sorted[sorted.length >> 1];
            samples = [];
            if (this.median <= upMs) return 'up';
            if (this.median > STEP_DOWN_MS) return 'down';
            return null;
        },
    };
}

/** The small "Loading detail…" pill over the stage. */
export function createDetailBadge(root) {
    const el = document.createElement('div');
    el.className = 'w3d-detail';
    el.hidden = true;
    el.setAttribute('role', 'status');
    root?.append(el);
    return {
        show(text) { el.textContent = text; el.hidden = false; },
        hide() { el.hidden = true; },
        remove() { el.remove(); },
    };
}
