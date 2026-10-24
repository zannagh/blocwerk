/*
 * bwAssetSession — what a page load decides about the wall-asset cache before anything else runs (design 2026-10-21
 * §6.6), and the ledger the rest of the page writes through.
 *
 * begin(): reads the page's `bw-partition` meta and acting user (`data-bw-user`), applies the wipe decision
 * (asset-wipe.js decide), records the new label and user tag in the ledger's meta, and fires the identity change when
 * it applies. Under a real label it also deletes every bw-a1-* cache and ledger row of any other partition, every
 * start, so nothing a stale tab or worker wrote under an older identity survives.
 *
 * IDENTITY MIRROR. The label and user tag are also kept in localStorage (MIRROR_KEY). A degraded ledger (a pending
 * upgrade, refused storage) starts empty, so it reads the previous identity from there: a sign-out or another user
 * is still announced (bw:identity-changed, the deploy drafts cleared). When localStorage cannot be read either, the
 * previous user counts as unknown and changed, so it fails towards announcing (the drafts go), never towards keeping.
 *
 * THE 401 LOCK (PR 3.5, asset-lock.js): before the wipes, the lock is resolved: an `anon` page saying `expired` after
 * a `u-` label locks it, the same label unlocks it, and anything else drops it. A kept lock's label joins every wipe's
 * keep set here, the every-start sweep's included, so a normal start never wipes it early and another identity always
 * does. The mirror carries the lock too, so a degraded page keeps it as well.
 *
 * THE KIOSK MARK (PR 3.15). The mirror's `kiosk` says whether the last Server page was a kiosk tablet's (its body's
 * data-bw-kiosk, set for every identity on it, or a `k-` label). The offline client reads it to tell a tablet that may
 * keep nothing (`none`: every answer `no-store`) from a device with nothing saved: the tablet shows "Not available
 * offline". It is a display hint only: it grants no read, and a `none` label holds nothing to read.
 *
 * A DEGRADED CLEAR (the offline client's Clear on a memory ledger) deletes the caches but cannot reach the durable rows:
 * it leaves `cleared: label` in the mirror, kept by every degraded start, and the next durable start drops that
 * partition's rows before anything reads them (the client's start honours it too, through clearedOf()).
 *
 * guard(): a ledger whose writes (putAsset, updateWall, putMine) go through only while the ledger's `activePartition` is the
 * row's partition. A tab still showing an old identity (another tab signed out or in since) can then never rewrite
 * rows for the partition it no longer owns. putMine also needs a `u-` row: a kiosk or share page never stores marks.
 */
window.bwAssetSession = (function () {
    'use strict';

    const PROTO = 1;
    const REAL_LABEL = /^(anon|[uk]-[a-z2-7]{20})$/;
    const USER_LABEL = /^u-[a-z2-7]{20}$/;
    const MIRROR_KEY = 'blocwerk-asset-identity';
    // Equal to no user tag, so an unreadable mirror counts as a changed user.
    const UNKNOWN_TAG = '\u0000unknown';

    // { activePartition, userTag, locked } of the last start, or null when localStorage cannot be read.
    function readMirror(win) {
        try {
            const value = win.localStorage.getItem(MIRROR_KEY);
            const parsed = value ? JSON.parse(value) : {};
            return parsed && typeof parsed === 'object' ? parsed : {};
        } catch (e) {
            return null;
        }
    }

    function writeMirror(win, value) {
        try {
            win.localStorage.setItem(MIRROR_KEY, JSON.stringify(value));
        } catch (e) {
            /* no mirror: a degraded start then announces, which is the safe side */
        }
    }

    /** The identity mirror; with `patch`, merged into it (asset-lock.js keeps its `locked` current). */
    function mirror(win, patch) {
        const current = readMirror(win);
        if (patch && current) {
            writeMirror(win, Object.assign(current, patch));
        }
        return current;
    }

    // What the last start recorded: the ledger's meta, or on a degraded ledger the mirror. A durable ledger that holds no
    // lock takes the mirror's for the same label (a degraded page locked it), so its 7 days run from the first lock.
    function previousOf(win, ledger, stored) {
        const saved = readMirror(win);
        if (!ledger.degraded) {
            // A mirror that says `locked: null` beside a stored lock: a degraded page dropped it (Clear, logout, another
            // identity), which its memory ledger could not record. The mirror wins, and the stored lock is wiped.
            if (stored.locked && saved && 'locked' in saved && saved.locked === null) {
                return Object.assign({}, stored, { locked: null, dropLocked: stored.locked.label });
            }
            const locked = stored.locked || (saved && saved.locked && saved.locked.label === stored.activePartition ? saved.locked : null);
            return Object.assign({}, stored, { locked, dropCleared: clearedOf(saved) });
        }
        if (!saved) {
            return { activePartition: stored.activePartition, userTag: UNKNOWN_TAG, locked: null, cleared: null };
        }
        return { activePartition: saved.activePartition ?? null, userTag: saved.userTag, locked: saved.locked || null, cleared: clearedOf(saved) };
    }

    /** The label a degraded Clear left for the next durable start to drop (the mirror's `cleared`), or null. */
    function clearedOf(saved) {
        return saved && REAL_LABEL.test(String(saved.cleared)) ? saved.cleared : null;
    }

    // The page is a kiosk tablet's: offline.html (which knows nobody) keeps the last page's mark.
    function kioskOf(win, label, offline) {
        if (offline) {
            const saved = readMirror(win);
            return !!(saved && saved.kiosk === true);
        }
        const body = win.document.body;
        return !!(body && body.dataset && body.dataset.bwKiosk) || String(label).startsWith('k-');
    }

    function pageLabel(doc) {
        const meta = doc.querySelector('meta[name="bw-partition"]');
        return meta ? meta.getAttribute('content') : null;
    }

    // The wipes of a start: a dropped lock first, then the decision's, or the every-start sweep of a real label. A kept
    // lock's label joins the keep set of both (asset-lock.js).
    async function applyWipes(Wipe, env, decision, lockPlan, drops) {
        for (const label of new Set([lockPlan.drop].concat(drops).filter(Boolean))) {
            await Wipe.dropPartition(env, label);
        }
        const keep = decision.keep.concat(lockPlan.lock ? [lockPlan.lock.label] : []);
        if (decision.wipe) {
            await Wipe.wipe(env, keep);
        } else if (REAL_LABEL.test(decision.page)) {
            // Every other partition's caches and rows, every start: a worker that stored under an old label meanwhile
            // (a stale tab, the window before a switch was seen) leaves nothing behind.
            await Wipe.wipe(env, [decision.page].concat(keep));
        }
    }

    /** Returns the label this page caches under: a real label, or 'none' / 'offline' / 'unknown' (caches nothing). */
    async function begin(win, ledger) {
        const Wipe = win.bwAssetWipe;
        const Lock = win.bwAssetLock;
        const env = { caches: win.caches, ledger, origin: win.location.origin };
        const stored = await ledger.getMeta();
        const meta = previousOf(win, ledger, stored);
        const label = pageLabel(win.document);
        // offline.html knows no user: it decides nothing here either.
        const offline = label === 'offline';
        const userTag = offline ? meta.userTag : Wipe.userTagOf(win.document);
        const userChanged = !offline && typeof meta.userTag === 'string' && meta.userTag !== userTag;
        const decision = Wipe.decide(meta.activePartition, label, !!Wipe.tokenOf(win.location), userChanged);
        const lockPlan = Lock.plan(meta.activePartition, meta.locked, { label, reason: Lock.signOutOf(win.document), userChanged }, Date.now());
        await applyWipes(Wipe, env, decision, lockPlan, [meta.dropLocked, meta.dropCleared]);
        const locked = lockPlan.lock;
        if (decision.active !== stored.activePartition || userTag !== stored.userTag || JSON.stringify(locked) !== JSON.stringify(Lock.valid(stored.locked))) {
            await ledger.setMeta({ activePartition: decision.active, userTag, proto: PROTO, locked });
        }
        // A durable start has dropped a degraded Clear's label; a degraded one passes it on.
        writeMirror(win, { activePartition: decision.active, userTag, locked, cleared: ledger.degraded ? meta.cleared : null, kiosk: kioskOf(win, label, offline) });
        await Lock.sync(locked, JSON.stringify(locked) !== JSON.stringify(Lock.valid(meta.locked)) || !!lockPlan.drop || !!meta.dropLocked);
        // Unlocking is the same user coming back (the label is an HMAC of the id): only a changed acting user announces.
        if (lockPlan.unlocked ? userChanged : decision.identityChanged) {
            Wipe.announceIdentityChange(win);
        }
        return decision.page;
    }

    function guard(ledger) {
        const owned = async row => (await ledger.getMeta()).activePartition === row.partition;
        return Object.assign(Object.create(ledger), {
            updateWall: async (partition, wallId, change) => ((await owned({ partition })) ? ledger.updateWall(partition, wallId, change) : undefined),
            putAsset: async row => ((await owned(row)) ? ledger.putAsset(row) : undefined),
            // The viewer's own marks: only a `u-` partition ever holds them (asset-mine.js), and only while it is active.
            putMine: async row => (USER_LABEL.test(String(row && row.partition)) && (await owned(row)) ? ledger.putMine(row) : undefined)
        });
    }

    return { begin, guard, pageLabel, mirror, clearedOf };
})();
