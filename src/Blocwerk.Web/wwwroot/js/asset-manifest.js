/*
 * bwAssetManifest — keeps the ledger's copy of each wall's asset manifest current (design 2026-10-21 §6.4, PR 2.4).
 *
 * GET /api/walls/{w}/manifest[?token=] with If-None-Match on the stored weak ETag. The manifest carries no partition
 * and nothing per-user; the ledger keeps only what the fill and eviction read (compact()).
 *   200      → the entries, ETag, cursor (members only) and `pending` are stored.
 *   304      → unchanged; the fetch time moves.
 *   403/404  → the wall is dropped (wipe rule 4): no access any more, a rotated share token, a kiosk that may not
 *              cache it.
 *   anything else, or no network → the stored copy stands (offline: render from cache).
 * `pending > 0` means some assets have no hash yet (listed with `sha: null`, used network-only); the caller asks again
 * after PENDING_RETRY_MS, when the server's backfill will have stamped them.
 *
 * Retention: a stored copy older than RETENTION_MS is not revalidated but fetched whole, and its feed cursor is
 * dropped. The feed keeps 30 days, so an older cursor is past its horizon and means a full resync.
 */
window.bwAssetManifest = (function () {
    'use strict';

    const PENDING_RETRY_MS = 60 * 1000;
    const RETENTION_MS = 30 * 24 * 60 * 60 * 1000;

    function compact(assets) {
        return (assets || []).filter(a => a && a.sha).map(a => {
            const entry = { sha: a.sha, kind: a.kind, tier: a.tier, bytes: a.bytes || 0 };
            if (Array.isArray(a.widths)) {
                entry.widths = a.widths;
            }
            if (a.splats) {
                entry.splats = a.splats;
            }
            return entry;
        });
    }

    function manifestUrl(wallId, token) {
        const path = `/api/walls/${wallId}/manifest`;
        return token ? `${path}?token=${encodeURIComponent(token)}` : path;
    }

    /**
     * Refreshes one wall's row. env: { fetch, ledger, now(), partition() }. opts: { token, member, opened, dropWall }.
     * Returns { outcome: 'updated'|'unchanged'|'dropped'|'offline'|'switched', pending }.
     */
    async function refresh(env, partition, wallId, opts) {
        const now = env.now();
        const stored = await env.ledger.getWall(partition, wallId);
        const row = stored || { partition, wallId, entries: [], etag: null, cursor: null, pending: 0, lastOpened: 0 };
        if (row.fetchedAt && now - row.fetchedAt > RETENTION_MS) {
            row.etag = null;
            row.cursor = null;
        }
        row.member = !!opts.member;
        if (opts.token) {
            row.shared = true;
        }
        if (opts.opened) {
            row.lastOpened = now;
        }
        const headers = {};
        if (row.etag && row.entries.length > 0) {
            headers['If-None-Match'] = row.etag;
        }
        let response;
        try {
            response = await env.fetch(manifestUrl(wallId, opts.token), { credentials: 'same-origin', cache: 'no-store', headers });
        } catch (e) {
            if (stored || opts.opened) {
                await env.ledger.putWall(row);
            }
            return { outcome: 'offline', pending: row.pending || 0 };
        }
        if (env.partition() !== partition) {
            return { outcome: 'switched', pending: 0 };
        }
        if (response.status === 403 || response.status === 404) {
            await opts.dropWall(wallId);
            return { outcome: 'dropped', pending: 0 };
        }
        if (response.status === 304) {
            row.fetchedAt = now;
            await env.ledger.putWall(row);
            return { outcome: 'unchanged', pending: row.pending || 0 };
        }
        if (!response.ok) {
            if (stored || opts.opened) {
                await env.ledger.putWall(row);
            }
            return { outcome: 'offline', pending: row.pending || 0 };
        }
        let body;
        try {
            body = await response.json();
        } catch (e) {
            return { outcome: 'offline', pending: row.pending || 0 };
        }
        row.entries = compact(body.assets);
        row.etag = response.headers.get('etag');
        row.cursor = body.cursor || null;
        row.pending = body.pending || 0;
        row.fetchedAt = now;
        await env.ledger.putWall(row);
        return { outcome: 'updated', pending: row.pending };
    }

    return { PENDING_RETRY_MS, RETENTION_MS, compact, manifestUrl, refresh };
})();
