// What this browser remembers about the photo-real ladder (wall3d-splat-ladder.js): per splat file
// (the scene's level URLs without their `?lod=` size), the biggest level that ran fine and the smallest
// that lost the context. Both expire after MEMORY_MS: a device is not locked to the lowest detail by
// one bad moment (a heavy tab, a flaky start) for good. A level that renders at or above a remembered
// failure clears it. Storage may be off or throw anywhere: every access is guarded and falls back to
// "nothing remembered".
const STORE_KEY = 'bw.photoreal.lod.v2';
const LEGACY_KEY = 'bw.photoreal.lod.v1';      // one global failure that never expired: dropped on first read
const MEMORY_MS = 7 * 24 * 3600 * 1000;
const MAX_FILES = 8;

/** Splat count of a level, the unknown full scene counted as larger than anything. */
export const sizeOf = level => level.splats > 0 ? level.splats : Number.MAX_SAFE_INTEGER;

/** The scene a ladder belongs to: its first level's URL without query or hash. */
export const fileKeyOf = levels => String(levels?.[0]?.url ?? '').split(/[?#]/)[0];

const lastAt = r => Math.max(r.failAt ?? 0, r.okAt ?? 0);
const fresh = (at, now) => typeof at === 'number' && now - at >= 0 && now - at < MEMORY_MS;

function readAll() {
    try {
        localStorage.removeItem(LEGACY_KEY);
        const v = JSON.parse(localStorage.getItem(STORE_KEY) || 'null');
        return v && typeof v === 'object' && v.files && typeof v.files === 'object' ? v : { files: {} };
    } catch {
        return { files: {} };
    }
}

function writeAll(v) {
    try {
        const keys = Object.keys(v.files);
        if (keys.length > MAX_FILES) {
            keys.sort((a, b) => lastAt(v.files[a]) - lastAt(v.files[b]));
            for (const k of keys.slice(0, keys.length - MAX_FILES)) delete v.files[k];
        }
        localStorage.setItem(STORE_KEY, JSON.stringify(v));
    } catch {
        // Private mode / storage off: the device relearns its limit next visit.
    }
}

/** The unexpired record of one file: { failSplats, failAt, okSplats, okAt } (any may be missing). */
function recordOf(all, key, now) {
    const r = all.files[key] || {};
    const out = {};
    if (r.failSplats != null && fresh(r.failAt, now)) { out.failSplats = r.failSplats; out.failAt = r.failAt; }
    if (r.okSplats != null && fresh(r.okAt, now)) { out.okSplats = r.okSplats; out.okAt = r.okAt; }
    return out;
}

/** What is remembered about this file: { failSplats, okSplats } (either may be missing; expired ones are). */
export function remembered(key, now = Date.now()) {
    const { failSplats, okSplats } = recordOf(readAll(), key, now);
    return { failSplats, okSplats };
}

function update(key, change, now) {
    const all = readAll();
    const r = recordOf(all, key, now);
    change(r);
    if (r.failSplats == null && r.okSplats == null) {
        delete all.files[key];
    } else {
        all.files[key] = r;
    }
    writeAll(all);
}

const clearFailure = r => { delete r.failSplats; delete r.failAt; };

/** Remembers that a level lost the context: this file stays below it until the memory expires. */
export function rememberFailure(key, level, now = Date.now()) {
    const size = sizeOf(level);
    update(key, r => {
        r.failSplats = Math.min(r.failSplats ?? Number.MAX_SAFE_INTEGER, size);
        r.failAt = now;
        if (r.okSplats >= size) { delete r.okSplats; delete r.okAt; }
    }, now);
}

/** A level at least as big as the remembered failure rendered: the failure was not the level's fault. */
export function rememberRendered(key, level, now = Date.now()) {
    update(key, r => {
        if (r.failSplats != null && sizeOf(level) >= r.failSplats) clearFailure(r);
    }, now);
}

/** Remembers that a level ran fine, so a later visit steps up to it without measuring again. */
export function rememberSuccess(key, level, now = Date.now()) {
    const size = sizeOf(level);
    update(key, r => {
        if (r.failSplats != null && size >= r.failSplats) clearFailure(r);
        if ((r.okSplats ?? 0) < size) { r.okSplats = size; r.okAt = now; }
    }, now);
}
