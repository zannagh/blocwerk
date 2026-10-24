/*
 * bwClientStore — the offline client's read side of the device cache (Phase 3 plan §1, PR 3.10), called by Blazor's
 * ClientStore through JsClientStoreSource. It reads only what the Server pages cached, through the shared modules
 * (Blocwerk.Shared.Ui: ledger, wipe, lock, session, clear) and client-store-rows.js, and never fetches.
 *
 * THE PARTITION is the ledger's `activePartition`, never the shell's `offline` meta (which decides nothing). Only the
 * active label's rows and caches are read, and each read re-checks it.
 *   locked       any lock (bwAssetLock.current(): the ledger, or the mirror on a degraded ledger): nothing is read,
 *                whatever the active label. The page shows only "Sign in to keep your offline data" and Clear.
 *   updating     the ledger's v2 upgrade waits for another tab (upgradePending): "Updating offline data…".
 *   unavailable  storage refused (a degraded ledger) or the shared modules missing.
 *   kiosk        a `k-` label: no offline kiosk before PR 3.15.
 *   empty        no real label, or no wall row under it.
 *   ready        the label's wall rows (`data`: kind → { sha, cursor }, `member`, `key`) and, for `u-` only, its marks.
 * `anon` is a share link's class: only the wall whose stored token hash the /v/ path's token matches is read
 * (client-store-rows.js); a tokenless /v/ wipes `anon`.
 * THE START (once per page): a lock past 7 days goes with its partition, rule 6 above, and on a durable ledger a
 * degraded Clear's leftovers (the mirror's `cleared` label, and a lock the mirror dropped) and the every-start sweep of
 * every other partition (the locked one kept).
 * WEB-ONLY GLOBALS (bwAssetStore, bwAssetCache, bwAssetReport, bwDeployDrafts) are not loaded here. asset-lock.js
 * posts to the worker itself without bwAssetStore; asset-clear.js's clear() is a silent no-op without bwAssetCache, so
 * Clear is this file's own clear(), announced over asset-clear.js's one channel (announce), on which this page hears
 * only the other tabs (subscribe). Nothing here throws into the page.
 */
window.bwClientStore = (function (win) {
    'use strict';

    const REAL_LABEL = /^(anon|[uk]-[a-z2-7]{20})$/;
    const USER_LABEL = /^u-[a-z2-7]{20}$/;
    const MIRROR_KEY = 'blocwerk-asset-identity';

    const Ledger = () => win.bwAssetLedger;
    const Wipe = () => win.bwAssetWipe;
    const Lock = () => win.bwAssetLock;
    const Session = () => win.bwAssetSession;
    const Clear = () => win.bwAssetClear;
    const Rows = () => win.bwClientStoreRows;
    const modules = () => !!(Ledger() && Wipe() && Lock() && Session() && Clear() && Rows());
    const envOf = ledger => ({ caches: win.caches, ledger, origin: win.location.origin });
    let started = null;

    // The active label as the last start recorded it: the ledger's meta, or the mirror on a degraded ledger.
    async function activeOf(ledger) {
        if (!ledger.degraded) {
            return (await ledger.getMeta()).activePartition ?? null;
        }
        const saved = Session().mirror(win);
        return saved ? saved.activePartition ?? null : null;
    }

    // What a degraded Clear could not reach (asset-session.js): its `cleared` label, and a lock the mirror dropped.
    async function leftovers(env, ledger) {
        const saved = Session().mirror(win);
        const cleared = Session().clearedOf(saved);
        if (cleared) {
            await Wipe().dropPartition(env, cleared);
            Session().mirror(win, { cleared: null });
        }
        const stored = Lock().valid((await ledger.getMeta()).locked);
        if (stored && saved && 'locked' in saved && saved.locked === null) {
            await Wipe().dropPartition(env, stored.label);
            await Lock().forget(ledger);
        }
    }

    async function begin() {
        const ledger = await Ledger().open();
        const env = envOf(ledger);
        if (!ledger.degraded) {
            await leftovers(env, ledger);
        }
        const plan = Lock().plan(await activeOf(ledger), await Lock().current(), { label: 'offline', reason: null, userChanged: false }, Date.now());
        if (plan.drop) {
            await Wipe().dropPartition(env, plan.drop);
            await Lock().forget(ledger);
        }
        const active = await activeOf(ledger);
        if (active === 'anon' && !Rows().shareOf()) {
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
            const { share, walls, mine } = await Rows().rows(ledger, active);
            return { state: walls.length > 0 ? 'ready' : 'empty', partition: active, shareWall: share ? share.wallId : null, walls, mine };
        } catch (e) {
            return nothing('unavailable');
        }
    }

    // Still the label to read: active, not locked, no kiosk (no offline kiosk before PR 3.15), and the ledger durable.
    async function readable(ledger, partition) {
        return REAL_LABEL.test(String(partition)) && !String(partition).startsWith('k-') && !ledger.degraded && !Ledger().upgradePending()
            && (await activeOf(ledger)) === partition && !(await Lock().current());
    }

    /**
     * The wall's cached documents under `partition`: { partition, wallId, key, docs: { kind → { sha, text } } }
     * (client-store-rows.js docsOf). Null when the label is no longer the one to read (another identity, a lock),
     * before or after the reads, or the wall is not this page's to read.
     */
    async function docs(partition, wallId) {
        try {
            await start();
            const ledger = await Ledger().open();
            if (!(await readable(ledger, partition))) {
                return null;
            }
            const read = await Rows().docsOf(ledger, partition, wallId);
            if (!read || !(await readable(ledger, partition))) {
                return null;
            }
            return { partition, wallId: Rows().idOf(wallId), key: read.key, docs: read.docs };
        } catch (e) {
            return null;
        }
    }

    /**
     * "Clear offline data" inside the client: the active partition's caches and rows (walls, assets, mine), and a locked
     * partition with its lock. The active label stays (a Server page refills it), so the client shows nothing until then.
     * On a degraded ledger (refused storage, a pending upgrade) the caches go by the mirror's label, but the durable rows
     * are out of reach: the mirror's `cleared` names the label, and the next durable start (a Server page's or this
     * file's) drops its rows before anything reads them. The Server tabs of the partition hear `clearing` / `cleared`
     * (asset-clear.js), so they stop and refill.
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
                Clear().announce({ type: 'clearing', partition: active });
                await Wipe().dropPartition(envOf(ledger), active);
                if (ledger.degraded) {
                    Session().mirror(win, { cleared: active });
                }
                Clear().announce({ type: 'cleared', partition: active });
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
                await (opts && opts.mine ? ledger.deleteMine(partition, Rows().idOf(wallId)) : Wipe().dropWall(env, partition, Rows().idOf(wallId)));
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
     * partition (asset-clear.js's channel, never this page's own posts), the identity mirror changed (a sign-in or out
     * in another tab), or the tab came back into view.
     */
    function listen(onChange) {
        const fire = () => { try { onChange(); } catch (e) { /* the caller's own failure */ } };
        if (Clear()) {
            Clear().subscribe(fire);
        }
        win.addEventListener('storage', event => { if (event.key === MIRROR_KEY) { fire(); } });
        win.document.addEventListener('visibilitychange', () => { if (win.document.visibilityState === 'visible') { fire(); } });
    }

    // The JSON forms Blazor reads (JsClientStoreSource): one string, so no interop serializer touches the documents.
    const asJson = value => (value === null || value === undefined ? null : JSON.stringify(value));

    return {
        start, snapshot, docs, clear, refused, listen,
        snapshotJson: () => snapshot().then(asJson),
        docsJson: (partition, wallId) => docs(partition, wallId).then(asJson),
        listenDotNet: ref => listen(() => ref.invokeMethodAsync('OnStoreChanged').catch(() => undefined))
    };
})(window);
