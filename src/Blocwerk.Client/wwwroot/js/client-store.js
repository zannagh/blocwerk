/*
 * bwClientStore — the offline client's read side of the device cache (Phase 3 plan §1, PR 3.10), called by Blazor's
 * ClientStore through JsClientStoreSource. It reads only what the Server pages cached, through the shared modules
 * (Blocwerk.Shared.Ui: ledger, wipe, lock, session), and never fetches.
 *
 * THE PARTITION is the ledger's `activePartition`, never the shell's `offline` meta (which decides nothing). Only the
 * active label's rows and caches are read, and each read re-checks it.
 *   locked       any lock (bwAssetLock.current(): the ledger, or the mirror on a degraded ledger): nothing is read,
 *                whatever the active label. The page shows only "Sign in to keep your offline data" and Clear.
 *   updating     the ledger's v2 upgrade waits for another tab (upgradePending): "Updating offline data…".
 *   unavailable  storage refused (a degraded ledger) or the shared modules missing.
 *   kiosk        a `k-` label: no offline kiosk before PR 3.15.
 *   empty        no real label, or no wall row under it.
 *   ready        the label's wall rows (`data`: kind → { sha, cursor }) and, for `u-` only, its mine rows.
 * `anon` is a share link's class: only the wall the /v/ path names with a token is read; a tokenless /v/ wipes `anon`.
 * THE START (once per page): a lock past 7 days goes with its partition, rule 6 above, and on a durable ledger the
 * every-start sweep of every other partition (the locked one kept), which takes a degraded Clear's mine rows too.
 * WEB-ONLY GLOBALS (bwAssetStore, bwAssetCache, bwAssetReport, bwDeployDrafts) are not loaded here. asset-lock.js
 * posts to the worker itself without bwAssetStore; asset-clear.js's clear() is a silent no-op without bwAssetCache, so
 * Clear is this file's own clear(), announced on asset-clear.js's channel. Nothing here throws into the page.
 */
window.bwClientStore = (function (win) {
    'use strict';

    const REAL_LABEL = /^(anon|[uk]-[a-z2-7]{20})$/;
    const USER_LABEL = /^u-[a-z2-7]{20}$/;
    const KINDS = ['wall', 'holds', 'boulders', 'gallery', 'comments'];
    // A replaced document of these kinds is never read in its successor's place (asset-data.js deletes it at once).
    const PRIVATE_KINDS = new Set(['comments']);
    const SHA = /^[0-9a-f]{64}$/;
    const SHARE_WALL = /\/walls\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})(?:\/|$)/i;
    const CHANNEL = 'bw-asset-clear';
    const MIRROR_KEY = 'blocwerk-asset-identity';

    const Ledger = () => win.bwAssetLedger;
    const Wipe = () => win.bwAssetWipe;
    const Lock = () => win.bwAssetLock;
    const Session = () => win.bwAssetSession;
    const modules = () => !!(Ledger() && Wipe() && Lock() && Session());
    const envOf = ledger => ({ caches: win.caches, ledger, origin: win.location.origin });
    const idOf = value => String(value || '').toLowerCase();
    let started = null;
    let bus = null;

    /** The share link the page's path carries: { token, wallId }, or null. */
    function shareOf(location) {
        const token = Wipe().tokenOf(location);
        const wall = SHARE_WALL.exec(location.pathname || '');
        return token && wall ? { token, wallId: idOf(wall[1]) } : null;
    }

    // The active label as the last start recorded it: the ledger's meta, or the mirror on a degraded ledger.
    async function activeOf(ledger) {
        if (!ledger.degraded) {
            return (await ledger.getMeta()).activePartition ?? null;
        }
        const saved = Session().mirror(win);
        return saved ? saved.activePartition ?? null : null;
    }

    async function begin() {
        const ledger = await Ledger().open();
        const env = envOf(ledger);
        const stored = Lock().valid((await ledger.getMeta()).locked) || (ledger.degraded ? await Lock().current() : null);
        const plan = Lock().plan(await activeOf(ledger), stored, { label: 'offline', reason: null, userChanged: false }, Date.now());
        if (plan.drop) {
            await Wipe().dropPartition(env, plan.drop);
            await Lock().forget(ledger);
        }
        const active = await activeOf(ledger);
        if (active === 'anon' && !shareOf(win.location)) {
            await Wipe().dropPartition(env, 'anon');
        }
        if (!ledger.degraded && REAL_LABEL.test(String(active))) {
            await Wipe().wipe(env, [active].concat(plan.lock ? [plan.lock.label] : []));
        }
    }

    /** Runs the start rules once per page; every later call waits for the same run. */
    function start() {
        if (!started) {
            started = (modules() ? begin() : Promise.resolve()).catch(() => undefined);
        }
        return started;
    }

    // The ledger's rows for the active label, filtered to what this page may read (anon: the share link's wall).
    async function rows(ledger, active) {
        const share = shareOf(win.location);
        const walls = (await ledger.wallsOf(active))
            .filter(row => active !== 'anon' || (share && idOf(row.wallId) === share.wallId))
            .map(row => ({ wallId: idOf(row.wallId), data: refsOf(row.data) }));
        const shown = new Set(walls.map(w => w.wallId));
        const mine = USER_LABEL.test(active)
            ? (await ledger.mineOf(active)).filter(m => m.partition === active && shown.has(idOf(m.wallId)))
                .map(m => ({ wallId: idOf(m.wallId), cursor: m.cursor ?? null, ticks: (m.ticks || []).map(tickOf), favourites: m.favourites || [] }))
            : [];
        return { share, walls, mine };
    }

    // A stored tick as the client's MineDocumentTick reads it: a missing time is the epoch, never a null.
    const tickOf = t => Object.assign({}, t, { lastAt: typeof t.lastAt === 'string' ? t.lastAt : new Date(0).toISOString() });

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

    /** What the client may show now: { state, partition, shareWall, walls, mine }; see the header for the states. */
    async function snapshot() {
        const nothing = (state, partition) => ({ state, partition: partition ?? null, shareWall: null, walls: [], mine: [] });
        try {
            if (!modules()) {
                return nothing('unavailable');
            }
            await start();
            const ledger = await Ledger().open();
            if (await Lock().current()) {
                return nothing('locked');
            }
            if (Ledger().upgradePending()) {
                return nothing('updating');
            }
            if (ledger.degraded) {
                return nothing('unavailable');
            }
            const active = await activeOf(ledger);
            if (String(active).startsWith('k-')) {
                return nothing('kiosk', active);
            }
            if (!REAL_LABEL.test(String(active))) {
                return nothing('empty');
            }
            const { share, walls, mine } = await rows(ledger, active);
            return { state: walls.length > 0 ? 'ready' : 'empty', partition: active, shareWall: share ? share.wallId : null, walls, mine };
        } catch (e) {
            return nothing('unavailable');
        }
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

    // Still the label to read: active, not locked, no kiosk (no offline kiosk before PR 3.15), and the ledger durable.
    async function readable(ledger, partition) {
        return REAL_LABEL.test(String(partition)) && !String(partition).startsWith('k-') && !ledger.degraded && !Ledger().upgradePending()
            && (await activeOf(ledger)) === partition && !(await Lock().current());
    }

    /**
     * The wall's cached documents under `partition`: { partition, wallId, docs: { kind → { sha, text } } }, read from one
     * ledger row, the replaced predecessor (`dataPrev`) standing in while its successor is not cached (never for
     * comments). Null when the label is no longer the one to read (another identity, a lock), before or after the reads,
     * or the wall is not this page's to read.
     */
    async function docs(partition, wallId) {
        try {
            await start();
            const ledger = await Ledger().open();
            if (!(await readable(ledger, partition))) {
                return null;
            }
            const share = shareOf(win.location);
            if (partition === 'anon' && (!share || share.wallId !== idOf(wallId))) {
                return null;
            }
            const row = (await ledger.wallsOf(partition)).find(r => idOf(r.wallId) === idOf(wallId));
            if (!row) {
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
            return (await readable(ledger, partition)) ? { partition, wallId: idOf(wallId), docs: out } : null;
        } catch (e) {
            return null;
        }
    }

    function post(message) {
        try {
            if (typeof win.BroadcastChannel === 'function') {
                bus = bus || new win.BroadcastChannel(CHANNEL);
                bus.postMessage(message);
            } else if (win.localStorage) {
                win.localStorage.setItem(CHANNEL, JSON.stringify(Object.assign({ at: Date.now(), nonce: Math.random() }, message)));
                win.localStorage.removeItem(CHANNEL);
            }
        } catch (e) {
            /* no channel: the other tabs see the cleared ledger on their next page */
        }
    }

    /**
     * "Clear offline data" inside the client: the active partition's caches and rows (walls, assets, mine), and a locked
     * partition with its lock. The active label stays (a Server page refills it), so the client shows nothing until then.
     * On a degraded ledger the caches still go by the mirror's label; its rows wait for the next durable start's sweep.
     * The Server tabs of the partition hear `clearing` / `cleared` (asset-clear.js), so they stop and refill.
     */
    async function clear() {
        try {
            if (!modules()) {
                return false;
            }
            await start();
            const ledger = await Ledger().open();
            const active = await activeOf(ledger);
            if (REAL_LABEL.test(String(active))) {
                post({ type: 'clearing', partition: active });
                await Wipe().dropPartition(envOf(ledger), active);
                post({ type: 'cleared', partition: active });
            }
            await Lock().clear();
            return true;
        } catch (e) {
            return false;
        }
    }

    /**
     * A refusal the sync loop (PR 3.12) met for `partition`, by the plan's identity matrix (§3): 403/404 drop the wall
     * (a mine refusal, `mine: true`, only its marks); a 401 `expired` locks a `u-` label; any other 401 and any 410
     * wipe (asset-lock.js refused). Returns 'dropped' | 'locked' | 'wiped' | 'kept' | 'switched'.
     */
    async function refused(partition, wallId, status, reason, opts) {
        try {
            await start();
            const ledger = await Ledger().open();
            if ((await activeOf(ledger)) !== partition) {
                return 'switched';
            }
            const env = envOf(ledger);
            if (status === 403 || status === 404) {
                await (opts && opts.mine ? ledger.deleteMine(partition, idOf(wallId)) : Wipe().dropWall(env, partition, idOf(wallId)));
                return 'dropped';
            }
            if (status !== 401 && status !== 410) {
                return 'kept';
            }
            const headers = { get: name => (String(name).toLowerCase() === Lock().HEADER ? reason ?? null : null) };
            await Lock().refused({ active: partition, env }, { status, headers });
            return status === 401 && reason === Lock().EXPIRED && USER_LABEL.test(partition) ? 'locked' : 'wiped';
        } catch (e) {
            return 'kept';
        }
    }

    /**
     * Calls `onChange()` when what the client may show could have changed: another tab cleared, locked or wiped a
     * partition (asset-clear.js's channel), the identity mirror changed (a sign-in or out in another tab), or the tab
     * came back into view.
     */
    function listen(onChange) {
        const fire = () => { try { onChange(); } catch (e) { /* the caller's own failure */ } };
        try {
            if (typeof win.BroadcastChannel === 'function') {
                new win.BroadcastChannel(CHANNEL).onmessage = fire;
            }
        } catch (e) {
            /* no channel: the storage event and visibility still apply */
        }
        win.addEventListener('storage', event => { if (event.key === CHANNEL || event.key === MIRROR_KEY) { fire(); } });
        win.document.addEventListener('visibilitychange', () => { if (win.document.visibilityState === 'visible') { fire(); } });
    }

    // The JSON forms Blazor reads (JsClientStoreSource): one string, so no interop serializer touches the documents.
    const asJson = value => (value === null || value === undefined ? null : JSON.stringify(value));

    return {
        KINDS, start, snapshot, docs, clear, refused, listen,
        snapshotJson: () => snapshot().then(asJson),
        docsJson: (partition, wallId) => docs(partition, wallId).then(asJson),
        listenDotNet: ref => listen(() => ref.invokeMethodAsync('OnStoreChanged').catch(() => undefined))
    };
})(window);
