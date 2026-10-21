/*
 * bwAssetPlan — what the background fill of the wall-asset cache downloads, in which order (design 2026-10-21 §6.8,
 * decisions of 2026-10-21). Pure: asset-fill.js runs the plan.
 *
 * ORDER: tier first, then wall.
 *   1. `core` of every wall (the current panels at the fit rendition plus 640), always, whatever the budget;
 *   2. the gallery (640 and the original), newest images first, while the image budget has room;
 *   3. 3D (textures, masks, one splat level), while 3D is kept, the connection is unmetered and the 3D budget has room.
 * Within a tier the open wall goes first, then the walls by when they were last opened. Every other `lazy` asset
 * (superseded panels, reset photos, video thumbnails) is never background-filled: the worker caches it on use, and so
 * it does 3D on a metered connection.
 */
window.bwAssetPlan = (function () {
    'use strict';

    const WIDTHS = [640, 1280, 1920, 2560];
    // image-res.js: at fit the rendition is capped at 1920 (FIT_CAP), which is what a page shows first.
    const FIT_CAP = 1920;
    const THUMB = 640;
    // iOS fails a single Cache API entry at about 600 MB (the storage probe); 400 MB stored fine.
    const MAX_ENTRY_BYTES = 400 * 1024 * 1024;

    /** The rendition a wall photo is shown at on this device at fit. */
    function fitWidth(cssWidth, dpr) {
        const needed = (cssWidth || 0) * (dpr || 1);
        const rung = WIDTHS.find(w => w >= needed) || WIDTHS[WIDTHS.length - 1];
        return Math.min(rung, FIT_CAP);
    }

    /**
     * Cellular, Save-Data or a 2g/3g estimate. A browser without the Network Information API (Safari, Firefox) counts
     * as unmetered: it cannot say, and treating it as metered would never fill 3D there.
     */
    function isMetered(nav) {
        const c = nav && nav.connection;
        return !!c && (!!c.saveData || c.type === 'cellular' || /(^|-)2g$|^3g$/.test(c.effectiveType || ''));
    }

    function keyOf(sha, width) {
        return width ? `${sha}/${width}` : sha;
    }

    function urlOf(wallId, sha, width, token) {
        const path = `/api/walls/${wallId}/a/${sha}` + (width ? `/${width}` : '');
        return token ? `${path}?token=${encodeURIComponent(token)}` : path;
    }

    function wallOrder(walls, openWallId) {
        return walls.slice().sort((a, b) => {
            if (a.wallId === openWallId || b.wallId === openWallId) {
                return a.wallId === openWallId ? -1 : 1;
            }
            return (b.lastOpened || 0) - (a.lastOpened || 0) || String(a.wallId).localeCompare(String(b.wallId));
        });
    }

    function photoWidths(entry, widths) {
        if (!Array.isArray(entry.widths) || entry.widths.length === 0) {
            return [null];
        }
        return [...new Set(widths)].filter(w => w === null || entry.widths.includes(w));
    }

    // Textures and masks whole; of the splat levels only the smallest, unless the wall already has one cached.
    function view3dEntries(wall, cachedShas) {
        const entries = (wall.entries || []).filter(e => e.tier === 'view3d' && e.sha);
        const splats = entries.filter(e => e.kind === 'splat');
        const others = entries.filter(e => e.kind !== 'splat');
        if (splats.length === 0 || splats.some(e => cachedShas.has(e.sha))) {
            return others;
        }
        const smallest = splats.slice().sort((a, b) => (a.splats || Infinity) - (b.splats || Infinity))[0];
        return others.concat([smallest]);
    }

    const atOf = entry => Date.parse(entry.at || '') || 0;

    function galleryEntries(wall) {
        return (wall.entries || []).filter(e => e.kind === 'gallery' && e.sha).sort((a, b) => atOf(b) - atOf(a));
    }

    /**
     * The ordered download list. `state`: { walls, cachedKeys: Set, openWallId, keep3d, metered, budget: {images,
     * view3d}, usedImages, used3d, width, tokens: Map wallId → token }. Returns [{ key, url, sha, width, tier, wallId }].
     */
    function planFill(state) {
        const walls = wallOrder(state.walls, state.openWallId);
        const planned = new Set(state.cachedKeys);
        const cachedShas = new Set([...state.cachedKeys].map(k => String(k).split('/')[0]));
        const tasks = [];
        const push = (wall, entry, width, tier) => {
            const key = keyOf(entry.sha, width);
            if (planned.has(key)) {
                return false;
            }
            planned.add(key);
            const token = state.tokens ? state.tokens.get(wall.wallId) : null;
            tasks.push({ key, url: urlOf(wall.wallId, entry.sha, width, token), sha: entry.sha, width, tier, wallId: wall.wallId });
            return true;
        };
        // Pushes an entry's renditions while `room` holds; the original's size bounds what each one costs.
        const within = (wall, entry, widths, tier, room) => {
            const bytes = entry.bytes || 0;
            if (bytes > MAX_ENTRY_BYTES || !room.fits(bytes)) {
                return;
            }
            const pushed = photoWidths(entry, widths).filter(w => push(wall, entry, w, tier)).length;
            room.take(pushed > 0 ? bytes : 0);
        };
        const roomOf = (used, budget) => ({ fits: b => used + b <= budget, take: b => { used += b; } });

        for (const wall of walls) {
            for (const entry of (wall.entries || []).filter(e => e.tier === 'core' && e.sha)) {
                photoWidths(entry, [state.width, THUMB]).forEach(w => push(wall, entry, w, 'core'));
            }
        }
        const images = roomOf(state.usedImages || 0, state.budget.images);
        for (const wall of walls) {
            galleryEntries(wall).forEach(entry => within(wall, entry, [THUMB, null], 'gallery', images));
        }
        if (state.keep3d && !state.metered) {
            const view3d = roomOf(state.used3d || 0, state.budget.view3d);
            for (const wall of walls) {
                view3dEntries(wall, cachedShas).forEach(entry => within(wall, entry, [null], 'view3d', view3d));
            }
        }
        return tasks;
    }

    return { WIDTHS, THUMB, MAX_ENTRY_BYTES, fitWidth, isMetered, keyOf, urlOf, planFill };
})();
