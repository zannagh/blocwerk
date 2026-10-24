/*
 * bwClient3d — what the offline client's 3D view (Phase 3 PR 3.13) may show of a wall, from the device alone: the
 * view3d document a Server page's 3D view cached (asset-3d.js lists it in the wall row's `docs`, newest first) with
 * every texture and mask it draws, read under the active partition's rules (client-store.js: no lock, the active
 * label, a share link only its own wall). It never fetches and never starts a fill: caches.match and the ledger only,
 * so a phone downloads nothing 3D unless the viewer opens 3D on a Server page (plan §2: "3D, only when cache.match
 * confirms the doc and textures").
 *
 *   status(partition, wallId) → { state, sha, photoReal }
 *     none     the device knows no 3D model of the wall (no view3d document in its row);
 *     off      the viewer turned "Keep 3D on this device" off (bwPrefs' `blocwerk-cache-3d` cookie) and the wall is
 *              not saved for offline 3D (`save3d`): the client shows no 3D, whatever an older visit left behind;
 *     missing  a model is known, but no document is cached with all its textures and masks;
 *     ready    `sha` is the newest such document; `photoReal` when at least one of its splat levels is cached too.
 *   view(partition, wallId, sha, token, boulder) → the Wall3DView wall3d.js draws, or null when `sha` is not (or no
 *     longer) ready. Its splat ladder keeps only the cached levels, so photo-real never downloads one; the share token
 *     goes onto every texture and splat URL (as wall3d-doc.js does); `boulder` ({ id, name, roles: { holdId → role },
 *     colorFoot: [holdId] }) highlights that boulder, a folded twin hold taking the strongest role of its copies.
 */
window.bwClient3d = (function (win) {
    'use strict';

    const SHA = /^[0-9a-f]{64}$/;
    const ASSET = /^\/api\/walls\/[^/?#]+\/a\/([0-9a-f]{64})(?:\/(\d{1,5}))?(?:[?#]|$)/;
    const OPT_OUT = /(?:^|;)\s*blocwerk-cache-3d=off\s*(?:;|$)/;
    const ROLE_RANK = { Start: 4, Top: 4, Hand: 3, Foot: 2, ColorFoot: 1 };

    const Ledger = () => win.bwAssetLedger;
    const Lock = () => win.bwAssetLock;
    const Rows = () => win.bwClientStoreRows;
    const idOf = value => String(value || '').toLowerCase();
    let parsed = { sha: null, doc: null };

    function optedOut() {
        try {
            return OPT_OUT.test(win.document.cookie || '');
        } catch (e) {
            return false;
        }
    }

    // The active label, unlocked, on a durable ledger: what client-store.js reads under.
    async function readable(ledger, partition) {
        return !ledger.degraded && !Ledger().upgradePending() && (await ledger.getMeta()).activePartition === partition
            && !(await Lock().current());
    }

    async function rowOf(partition, wallId) {
        const ledger = await Ledger().open();
        if (!(await readable(ledger, partition))) {
            return null;
        }
        const row = await ledger.getWall(partition, idOf(wallId));
        return row && (await Rows().mayRead(ledger, partition, row)) ? row : null;
    }

    // A cached asset of the partition by the worker's key (`{sha}` or `{sha}/{width}`), in either of its caches.
    async function cached(partition, key, names) {
        for (const cacheName of names || [`bw-a1-${partition}-img`, `bw-a1-${partition}-3d`]) {
            try {
                const hit = await win.caches.match(`${win.location.origin}/__bw/${key}`, { cacheName });
                if (hit) {
                    return hit;
                }
            } catch (e) {
                /* a cache the browser refuses holds nothing */
            }
        }
        return null;
    }

    const keyOf = url => {
        const match = ASSET.exec(String(url || ''));
        return match ? (match[2] ? `${match[1]}/${match[2]}` : match[1]) : null;
    };

    async function allCached(partition, urls) {
        for (const url of urls) {
            const key = keyOf(url);
            if (!key || !(await cached(partition, key))) {
                return false;
            }
        }
        return true;
    }

    // The document's JSON, parsed once per sha (the last one is kept: a status and the mount read the same).
    async function docOf(partition, wallId, sha) {
        if (parsed.sha === sha && parsed.doc) {
            return idOf(parsed.doc.wallId) === idOf(wallId) ? parsed.doc : null;
        }
        const hit = await cached(partition, sha, [`bw-a1-${partition}-3d`]);
        if (!hit) {
            return null;
        }
        try {
            const doc = JSON.parse(await hit.text());
            parsed = { sha, doc };
            return idOf(doc && doc.wallId) === idOf(wallId) ? doc : null;
        } catch (e) {
            return null;
        }
    }

    const imageUrls = doc => (doc.textures || []).flatMap(t => [t.url, t.maskUrl]).filter(Boolean);

    // The splat ladder as wall3d-splat-ladder.js's ladderOf reads it: the levels, else the older one or two files.
    function levelsOf(doc) {
        const given = (doc.splatLevels || []).filter(l => l && l.url);
        if (given.length > 0) {
            return given;
        }
        return [[doc.splatMobileUrl, 180000], [doc.splatUrl, 0]].filter(([url]) => url).map(([url, splats]) => ({ url, splats, sizeBytes: 0 }));
    }

    async function cachedLevels(partition, doc) {
        const levels = [];
        for (const level of levelsOf(doc)) {
            if (await allCached(partition, [level.url])) {
                levels.push(level);
            }
        }
        return levels;
    }

    /** The newest view3d document of the wall's row that is cached in full: { state, sha, doc } (see the header). */
    async function find(partition, wallId, only) {
        const row = await rowOf(partition, wallId);
        const docs = ((row && row.docs) || []).filter(d => d && d.kind === 'view3d' && SHA.test(String(d.sha)));
        if (docs.length === 0) {
            return { state: 'none' };
        }
        if (optedOut() && !row.save3d) {
            return { state: 'off' };
        }
        for (const ref of docs.filter(d => !only || d.sha === only)) {
            const doc = await docOf(partition, wallId, ref.sha);
            if (doc && (await allCached(partition, imageUrls(doc)))) {
                return { state: 'ready', sha: ref.sha, doc };
            }
        }
        return { state: 'missing' };
    }

    async function status(partition, wallId) {
        try {
            const found = await find(partition, wallId, null);
            const photoReal = found.state === 'ready' && Array.isArray(found.doc.splatMatrix) && found.doc.splatMatrix.length === 16
                && (await cachedLevels(partition, found.doc)).length > 0;
            return { state: found.state, sha: found.sha || null, photoReal };
        } catch (e) {
            return { state: 'none', sha: null, photoReal: false };
        }
    }

    const tokenized = (url, token) => (url && token ? `${url}${url.includes('?') ? '&' : '?'}token=${encodeURIComponent(token)}` : url);

    // The boulder's holds lit by role: a folded hold (duplicateIds) takes the strongest role any of its copies has.
    function withBoulder(view, boulder) {
        if (!boulder) {
            return view;
        }
        const roles = boulder.roles || {};
        const colorFoot = new Set((boulder.colorFoot || []).map(idOf));
        const roleOf = hold => [hold.id].concat(hold.duplicateIds || []).map(idOf)
            .map(id => roles[id] || (colorFoot.has(id) ? 'ColorFoot' : null))
            .reduce((best, role) => ((ROLE_RANK[role] || 0) > (ROLE_RANK[best] || 0) ? role : best), null);
        const holds = (view.holds || []).map(h => {
            const role = roleOf(h);
            return role ? Object.assign({}, h, { role }) : h;
        });
        return Object.assign({}, view, { boulderId: idOf(boulder.id), boulderName: boulder.name || null, holds });
    }

    async function view(partition, wallId, sha, token, boulder) {
        try {
            const found = await find(partition, wallId, sha);
            if (found.state !== 'ready') {
                return null;
            }
            const levels = (await cachedLevels(partition, found.doc)).map(l => Object.assign({}, l, { url: tokenized(l.url, token) }));
            const doc = found.doc;
            return withBoulder(Object.assign({}, doc, {
                textures: (doc.textures || []).map(t => Object.assign({}, t, { url: tokenized(t.url, token), maskUrl: tokenized(t.maskUrl, token) })),
                splatLevels: levels,
                splatUrl: null,
                splatMobileUrl: null,
            }), boulder);
        } catch (e) {
            return null;
        }
    }

    return {
        status, view, withBoulder,
        statusJson: (partition, wallId) => status(partition, wallId).then(s => JSON.stringify(s)),
    };
})(window);
