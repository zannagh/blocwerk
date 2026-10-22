/*
 * bwAssetMine — the signed-in viewer's own ticks and favourites of each cached wall (Phase 3 plan §1, PR 3.4), kept in
 * the ledger's `mine` store for the offline client's boulder list and detail ("my tick and favourite marks").
 *
 * GET /api/me/walls/{w}/mine (no-store, never in any HTTP cache) after every manifest round that left the wall current,
 * beside its docs index (asset-cache.js), and on bwAssetMine.changed(wallId): the circuit saw a `tick.mine` or
 * `favorite.changed` row of the wall (AssetCacheFeedRelay.cs), one refetch DEBOUNCE_MS after the first of a burst.
 *   200          → the wall's row { partition, wallId, cursor, ticks, favourites, fetchedAt } replaces the stored one.
 *   403/404/410  → the stored row goes, the wall's other data stays (a kiosk session, a class that sees no marks, a wall
 *                  that left, an account that is gone; the manifest and the docs index decide about the wall itself).
 *   401, 5xx, no network → the stored row stands (whether a 401 locks or wipes is PR 3.5's).
 * ONLY under a `u-` partition, for a wall that has a row in it. `k-` and `anon` never fetch one and never write one
 * (the session's guard refuses such a write as well, asset-session.js). Every wipe of a partition drops its mine rows
 * with it: rules 1, 2 and 6 and the every-start sweep (ledger deleteAllExcept), rules 4/5 (dropWall), Clear offline data.
 * Nothing here throws into the page.
 */
window.bwAssetMine = (function (win) {
    'use strict';

    const DEBOUNCE_MS = 2000;
    const USER_LABEL = /^u-[a-z2-7]{20}$/;
    const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
    const DROP = new Set([403, 404, 410]);
    const timers = new Map();

    const Cache = () => win.bwAssetCache;
    const idOf = value => String(value || '').toLowerCase();
    const mineUrl = wallId => `/api/me/walls/${wallId}/mine`;

    function tickOf(tick) {
        if (!tick || !UUID.test(idOf(tick.boulderId))) {
            return null;
        }
        return {
            boulderId: idOf(tick.boulderId),
            attempts: Math.max(0, Math.floor(Number(tick.attempts) || 0)),
            sent: tick.sent === true,
            flashed: tick.flashed === true,
            lastAt: typeof tick.lastAt === 'string' ? tick.lastAt : null
        };
    }

    /** The body as the stored fields, or null when it is not the marks of `wallId`. */
    function compact(body, wallId) {
        if (!body || idOf(body.wallId) !== idOf(wallId) || !Array.isArray(body.ticks) || !Array.isArray(body.favourites)) {
            return null;
        }
        return {
            cursor: typeof body.cursor === 'string' ? body.cursor : null,
            ticks: body.ticks.map(tickOf).filter(Boolean),
            favourites: body.favourites.map(idOf).filter(id => UUID.test(id))
        };
    }

    /**
     * Refreshes one wall's marks. env: asset-cache.js's { fetch, ledger, partition(), now }.
     * Returns 'updated' | 'dropped' | 'offline' | 'switched' | 'skipped'.
     */
    async function refresh(env, partition, wallId) {
        if (!USER_LABEL.test(partition || '') || !(await env.ledger.getWall(partition, wallId))) {
            return 'skipped';
        }
        let response;
        try {
            response = await env.fetch(mineUrl(wallId), { credentials: 'same-origin', cache: 'no-store' });
        } catch (e) {
            return 'offline';
        }
        if (env.partition() !== partition) {
            return 'switched';
        }
        if (DROP.has(response.status)) {
            await env.ledger.deleteMine(partition, wallId);
            return 'dropped';
        }
        let fields = null;
        try {
            fields = response.ok ? compact(await response.json(), wallId) : null;
        } catch (e) {
            fields = null;
        }
        if (!fields) {
            return 'offline';
        }
        await env.ledger.putMine(Object.assign({ partition, wallId }, fields, { fetchedAt: env.now() }));
        return 'updated';
    }

    /** A mark of the viewer's changed on the wall (the circuit's feed): refetch DEBOUNCE_MS after the first call of a burst. */
    function changed(wallId) {
        const id = idOf(wallId);
        if (!id || timers.has(id)) {
            return;
        }
        timers.set(id, win.setTimeout(() => {
            timers.delete(id);
            const cache = Cache();
            if (cache && typeof cache.mineChanged === 'function') {
                Promise.resolve(cache.mineChanged(id)).catch(() => undefined);
            }
        }, DEBOUNCE_MS));
    }

    return { DEBOUNCE_MS, mineUrl, compact, refresh, changed };
})(window);
