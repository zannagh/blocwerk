/*
 * bwClientSync — the offline client's network side (Phase 3 plan §1, PR 3.12), called by Blazor's ClientSync through
 * JsSyncTransport. The loop itself (when to reload, probe, back off, reopen) is C#; this file only talks to the server
 * and writes what it learns into the device cache, through the same modules the Server pages use:
 *   indexJson   GET /api/walls/{w}/docs through Web's asset-data.js refresh (stored ETag, the ledger row's `data`,
 *               `dataPrev`, a replaced comments document deleted at once); answers { status, outcome, reason, docs }
 *               where docs is the row's { kind → { sha, cursor } } after an `updated` or `unchanged` refresh.
 *   doc         GET /api/walls/{w}/d/{kind}/{sha}: the JSON text (the service worker caches it under the partition;
 *               its `asset-cached` gives the ledger its asset row here, as asset-store.js does on a Server page).
 *   mineJson    GET /api/me/walls/{w}/mine through Web's asset-mine.js refresh (the ledger's `mine` row, `u-` only);
 *               answers { status, reason, body }.
 *   changesJson / meChangesJson   one catch-up page: { status, reason, retryAfter, body }; the own-rows page also
 *               carries `partition`, the label the cookie names right after it (another user may have signed in).
 *   savedCursor / saveCursor      the wall's resume cursor, on its ledger row (`feedCursor`, guarded writes).
 *   savedMeCursor / saveMeCursor  the own-rows poll's cursor, one localStorage entry for the last `u-` label.
 *   open / close                  the wall's EventSource (`open` on its 200; a cap's 429 never opens). A `bye` or an
 *               error closes it here, so the browser never reconnects by itself (PR 1.6 W3): the loop probes and reopens.
 *   refused     a refusal, by the identity matrix: client-store.js's refused (asset-lock.js tells the Server tabs).
 *   watch       visibilitychange and online, for the loop (listeners added once per page).
 *   status      a connection bubble (window.bwStatus?.show), when the hotfix batch's bubbles are on the page.
 * The refresh modules never act on a refusal here (their dropWall and signedOut are no-ops): the C# loop hands each
 * one to `refused`, so one place applies the matrix. Nothing here throws into the page.
 */
window.bwClientSync = (function (win) {
    'use strict';

    const PROTO = 1;
    const SIGN_OUT = 'X-Blocwerk-SignOut';
    // The labels the client syncs (a kiosk polls nothing).
    const SYNCED_LABEL = /^(anon|u-[a-z2-7]{20})$/;
    const streams = new Map();
    let nextStream = 1;
    const PARTITION_URL = '/api/me/cache-partition';
    const ME_CURSOR_KEY = 'bw-me-cursor';
    let recording = false;
    let watching = false;

    const Ledger = () => win.bwAssetLedger;
    const Session = () => win.bwAssetSession;
    const idOf = value => String(value || '').toLowerCase();
    const json = value => (value === null || value === undefined ? null : JSON.stringify(value));
    const withQuery = (path, params) => {
        const query = new URLSearchParams();
        Object.entries(params).forEach(([key, value]) => { if (value) { query.set(key, value); } });
        const text = query.toString();
        return text ? `${path}?${text}` : path;
    };

    // The guarded ledger (asset-session.js: writes only under the active label) and a fetch that keeps the answer.
    async function envOf(partition) {
        const raw = await Ledger().open();
        const seen = { response: null, body: null };
        const env = {
            caches: win.caches, origin: win.location.origin, ledger: Session().guard(raw), now: () => Date.now(),
            partition: () => partition, signedOut: () => undefined,
            fetch: async (url, init) => {
                const response = await win.fetch(url, init);
                seen.response = response;
                seen.body = response.ok ? response.clone().text().catch(() => null) : null;
                return response;
            }
        };
        return { raw, env, seen };
    }

    async function activeOf(raw) {
        return raw.degraded ? null : (await raw.getMeta()).activePartition ?? null;
    }

    async function reasonOf(response) {
        const header = response.headers && response.headers.get(SIGN_OUT);
        if (header || response.status !== 401) {
            return header || null;
        }
        try {
            const body = await response.clone().json();
            return body && typeof body.reason === 'string' ? body.reason : null;
        } catch (e) {
            return null;
        }
    }

    function docsOf(row) {
        const docs = {};
        Object.entries((row && row.data) || {}).forEach(([kind, doc]) => {
            if (doc && typeof doc.sha === 'string') {
                docs[kind] = { sha: doc.sha, cursor: typeof doc.cursor === 'string' ? doc.cursor : null };
            }
        });
        return docs;
    }

    /** The wall's docs index into the ledger: { status, outcome, reason, docs }. */
    async function index(partition, wallId, token) {
        try {
            const { raw, env, seen } = await envOf(partition);
            if ((await activeOf(raw)) !== partition) {
                return { status: 0, outcome: 'switched', reason: null, docs: null };
            }
            const outcome = await win.bwAssetData.refresh(env, partition, idOf(wallId), { token: token || null, dropWall: () => undefined });
            const response = seen.response;
            const docs = outcome === 'updated' || outcome === 'unchanged' ? docsOf(await raw.getWall(partition, idOf(wallId))) : null;
            return { status: response ? response.status : 0, outcome, reason: response ? await reasonOf(response) : null, docs };
        } catch (e) {
            return { status: 0, outcome: 'offline', reason: null, docs: null };
        }
    }

    /** One data document's JSON text, or null. */
    async function doc(wallId, kind, sha, token) {
        try {
            const response = await win.fetch(withQuery(`/api/walls/${idOf(wallId)}/d/${kind}/${sha}`, { token }), { credentials: 'same-origin' });
            return response.ok ? await response.text() : null;
        } catch (e) {
            return null;
        }
    }

    /** The viewer's marks of the wall into the ledger: { status, reason, body }. */
    async function mine(partition, wallId) {
        try {
            const { raw, env, seen } = await envOf(partition);
            if ((await activeOf(raw)) !== partition) {
                return { status: 0, reason: null, body: null };
            }
            const outcome = await win.bwAssetMine.refresh(env, partition, idOf(wallId));
            const response = seen.response;
            const body = outcome === 'updated' && seen.body ? await seen.body : null;
            return { status: response ? response.status : 0, reason: response ? await reasonOf(response) : null, body };
        } catch (e) {
            return { status: 0, reason: null, body: null };
        }
    }

    async function page(url) {
        try {
            const response = await win.fetch(url, { credentials: 'same-origin', cache: 'no-store' });
            const retry = Number(response.headers.get('Retry-After'));
            return {
                status: response.status, reason: await reasonOf(response),
                retryAfter: Number.isFinite(retry) && retry > 0 ? retry : null,
                body: response.ok ? await response.text() : null
            };
        } catch (e) {
            return { status: 0, reason: null, retryAfter: null, body: null };
        }
    }

    const changes = (wallId, after, token) => page(withQuery(`/api/walls/${idOf(wallId)}/changes`, { after, token }));

    // The label the cookie names now: /api/me/changes answers for the cookie's user, whoever signed in meanwhile.
    async function cookiePartition() {
        try {
            const response = await win.fetch(PARTITION_URL, { credentials: 'same-origin', cache: 'no-store' });
            const body = response.ok ? await response.json() : null;
            return body && typeof body.partition === 'string' ? body.partition : null;
        } catch (e) {
            return null;
        }
    }

    async function meChanges(after) {
        const answer = await page(withQuery('/api/me/changes', { after }));
        return Object.assign(answer, { partition: answer.status === 200 ? await cookiePartition() : null });
    }

    /** The wall's stored resume cursor (the ledger row's feedCursor), or null. */
    async function savedCursor(partition, wallId) {
        try {
            const row = await (await Ledger().open()).getWall(partition, idOf(wallId));
            return row && typeof row.feedCursor === 'string' ? row.feedCursor : null;
        } catch (e) {
            return null;
        }
    }

    /** Stores the wall's resume cursor on its row, only under the active label and only while the row stands. */
    async function saveCursor(partition, wallId, cursor) {
        try {
            const ledger = Session().guard(await Ledger().open());
            await ledger.updateWall(partition, idOf(wallId), row => (row ? Object.assign(row, { feedCursor: String(cursor) }) : undefined));
        } catch (e) {
            /* the next start replays from the documents instead */
        }
    }

    // The own-rows poll's cursor: one entry, for the last signed-in label (a cursor names no person).
    function savedMeCursor(partition) {
        try {
            const stored = JSON.parse(win.localStorage.getItem(ME_CURSOR_KEY) || 'null');
            return stored && stored.partition === partition && typeof stored.cursor === 'string' ? stored.cursor : null;
        } catch (e) {
            return null;
        }
    }

    function saveMeCursor(partition, cursor) {
        try {
            win.localStorage.setItem(ME_CURSOR_KEY, JSON.stringify({ partition, cursor: String(cursor) }));
        } catch (e) {
            /* storage blocked: the next poll starts from the marks' cursors */
        }
    }

    /** Opens the wall's stream after `after`; its messages go to ref.OnMessage(type, data). Returns its id. */
    function open(wallId, after, token, ref) {
        const id = nextStream++;
        const send = (type, data) => ref.invokeMethodAsync('OnMessage', type, data ?? null).catch(() => undefined);
        let source;
        try {
            source = new win.EventSource(withQuery(`/api/walls/${idOf(wallId)}/feed/stream`, { after, token }));
        } catch (e) {
            win.setTimeout(() => send('error', null), 0);
            return id;
        }
        streams.set(id, source);
        const ended = (type, data) => {
            if (streams.get(id) === source) {
                close(id);
                send(type, data);
            }
        };
        source.onopen = () => { if (streams.get(id) === source) { send('open', null); } };
        source.onmessage = event => { if (streams.get(id) === source) { send('message', event.data); } };
        source.addEventListener('resync', event => { if (streams.get(id) === source) { send('resync', event.data); } });
        source.addEventListener('bye', event => ended('bye', event.data));
        source.onerror = () => ended('error', null);
        return id;
    }

    function close(id) {
        const source = streams.get(id);
        streams.delete(id);
        if (source) {
            try { source.close(); } catch (e) { /* already closed */ }
        }
    }

    /** A refusal the loop met, by §3's matrix (client-store.js). */
    function refused(partition, wallId, status, reason, mine) {
        return win.bwClientStore.refused(partition, wallId, status, reason, { mine: !!mine });
    }

    // The worker stored a document this page fetched: the ledger's asset row, as asset-store.js records it.
    async function onWorkerMessage(event) {
        const data = event && event.data;
        if (!data || data.proto !== PROTO || data.type !== 'asset-cached' || typeof data.sha !== 'string') {
            return;
        }
        try {
            const raw = await Ledger().open();
            const active = await activeOf(raw);
            if (!SYNCED_LABEL.test(String(active))) {
                return;
            }
            const now = Date.now();
            const key = data.width ? `${data.sha}/${data.width}` : data.sha;
            await Session().guard(raw).putAsset({ partition: active, key, sha: data.sha, width: data.width || null, cache: null, bytes: data.bytes || 0, cachedAt: now, verifiedAt: now });
        } catch (e) {
            /* the next Server page reconciles the ledger with the caches */
        }
    }

    /** Hands visibilitychange and online to ref (OnVisibility, OnOnline); answers whether the tab is visible now. */
    function watch(ref) {
        const call = (name, ...args) => ref.invokeMethodAsync(name, ...args).catch(() => undefined);
        if (!watching) {
            watching = true;
            win.document.addEventListener('visibilitychange', () => call('OnVisibility', win.document.visibilityState !== 'hidden'));
            win.addEventListener('online', () => call('OnOnline'));
        }
        const sw = win.navigator.serviceWorker;
        if (!recording && sw && typeof sw.addEventListener === 'function') {
            recording = true;
            sw.addEventListener('message', onWorkerMessage);
        }
        return win.document.visibilityState !== 'hidden';
    }

    function status(kind) {
        try {
            if (win.bwStatus && typeof win.bwStatus.show === 'function') {
                win.bwStatus.show(kind);
            }
        } catch (e) {
            /* the bubbles' own failure is not the loop's */
        }
    }

    return {
        index, doc, mine, changes, meChanges, open, close, refused, watch, status, onWorkerMessage,
        savedCursor, saveCursor, savedMeCursor, saveMeCursor,
        indexJson: (p, w, t) => index(p, w, t).then(json),
        mineJson: (p, w) => mine(p, w).then(json),
        changesJson: (w, a, t) => changes(w, a, t).then(json),
        meChangesJson: a => meChanges(a).then(json)
    };
})(window);
