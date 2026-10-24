/*
 * bwClientStoreRows — what client-store.js reads from one partition of the ledger (Phase 3 plan §1, PR 3.10): its wall
 * rows as the client's snapshot names them, the viewer's marks, and one wall's cached documents. Pure reads over the
 * shared modules (bwAssetLedger, bwAssetWipe); the partition checks stay with client-store.js.
 *
 * A SHARE LINK (`anon`): a row is the page's only when the /v/ path names its wall AND its token hashes to the row's
 * `share` (bwAssetLedger.shareHash; asset-manifest.js stores the hash, never the token). A row without one, or a token
 * that does not match, is not read: offline, a guessed /v/…/shared/{anything} opens nothing.
 * A ROW'S KEY is the identity of what the ledger names for it: its documents' shas and the predecessors still standing
 * in (`dataPrev`). The client keeps a loaded wall while the key stands, whatever of it was cached or parsed.
 */
window.bwClientStoreRows = (function (win) {
    'use strict';

    const USER_LABEL = /^u-[a-z2-7]{20}$/;
    const KINDS = ['wall', 'holds', 'boulders', 'gallery', 'comments'];
    // A replaced document of these kinds is never read in its successor's place (asset-data.js deletes it at once).
    const PRIVATE_KINDS = new Set(['comments']);
    const SHA = /^[0-9a-f]{64}$/;
    const SHARE_WALL = /\/walls\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})(?:\/|$)/i;

    const idOf = value => String(value || '').toLowerCase();

    /** The share link the page's path carries: { token, wallId }, or null. */
    function shareOf() {
        const token = win.bwAssetWipe.tokenOf(win.location);
        const wall = SHARE_WALL.exec(win.location.pathname || '');
        return token && wall ? { token, wallId: idOf(wall[1]) } : null;
    }

    /** True when `row` may be read under `partition` on this page: any row of a user label, the share link's for `anon`. */
    async function mayRead(partition, row) {
        if (partition !== 'anon') {
            return true;
        }
        const share = shareOf();
        if (!share || idOf(row.wallId) !== share.wallId || typeof row.share !== 'string') {
            return false;
        }
        return (await win.bwAssetLedger.shareHash(share.token)) === row.share;
    }

    function refsOf(data) {
        const refs = {};
        for (const kind of KINDS) {
            const doc = data && data[kind];
            if (doc && SHA.test(String(doc.sha))) {
                refs[kind] = { sha: doc.sha, cursor: typeof doc.cursor === 'string' ? doc.cursor : null };
            }
        }
        return refs;
    }

    /** The row's key: kind=sha of its documents, then of the predecessors standing in, in a fixed order. */
    function keyOf(row) {
        const refs = refsOf(row.data);
        const prev = (row && row.dataPrev) || {};
        return KINDS.map(kind => `${kind}=${refs[kind] ? refs[kind].sha : ''}/${SHA.test(String(prev[kind])) ? prev[kind] : ''}`).join(';');
    }

    // A stored tick as the client's MineDocumentTick reads it: a missing time is the epoch, never a null.
    const tickOf = t => Object.assign({}, t, { lastAt: typeof t.lastAt === 'string' ? t.lastAt : new Date(0).toISOString() });

    /** The partition's rows this page may read: { share, walls, mine } (marks for a `u-` label only). */
    async function rows(ledger, partition) {
        const share = shareOf();
        const walls = [];
        for (const row of await ledger.wallsOf(partition)) {
            if (await mayRead(partition, row)) {
                walls.push({ wallId: idOf(row.wallId), data: refsOf(row.data), member: row.member === true, key: keyOf(row) });
            }
        }
        const shown = new Set(walls.map(w => w.wallId));
        const mine = USER_LABEL.test(partition)
            ? (await ledger.mineOf(partition)).filter(m => m.partition === partition && shown.has(idOf(m.wallId)))
                .map(m => ({ wallId: idOf(m.wallId), cursor: m.cursor ?? null, ticks: (m.ticks || []).map(tickOf), favourites: m.favourites || [] }))
            : [];
        return { share, walls, mine };
    }

    // One cached document's text from the partition's `-3d` cache (the worker keys a document by its sha alone).
    async function cachedText(partition, sha) {
        try {
            const hit = await win.caches.match(`${win.location.origin}/__bw/${sha}`, { cacheName: `bw-a1-${partition}-3d` });
            return hit ? await hit.text() : null;
        } catch (e) {
            return null;
        }
    }

    /**
     * The wall's row and cached documents under `partition`: { key, docs: { kind → { sha, text } } }, the replaced
     * predecessor (`dataPrev`) standing in while its successor is not cached (never for comments). Null when there is
     * no such row or this page may not read it.
     */
    async function docsOf(ledger, partition, wallId) {
        const row = (await ledger.wallsOf(partition)).find(r => idOf(r.wallId) === idOf(wallId));
        if (!row || !(await mayRead(partition, row))) {
            return null;
        }
        const out = {};
        const refs = refsOf(row.data);
        for (const kind of KINDS) {
            const shas = [refs[kind] && refs[kind].sha, PRIVATE_KINDS.has(kind) ? null : (row.dataPrev || {})[kind]].filter(s => SHA.test(String(s)));
            for (const sha of shas) {
                const text = await cachedText(partition, sha);
                if (text !== null) {
                    out[kind] = { sha, text };
                    break;
                }
            }
        }
        return { key: keyOf(row), docs: out };
    }

    return { KINDS, idOf, shareOf, rows, docsOf, keyOf };
})(window);
