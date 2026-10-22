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

    // { activePartition, userTag } of the last start, or null when localStorage cannot be read.
    function readMirror(win) {
        try {
            const value = win.localStorage.getItem(MIRROR_KEY);
            const parsed = value ? JSON.parse(value) : {};
            return parsed && typeof parsed === 'object' ? parsed : {};
        } catch (e) {
            return null;
        }
    }

    function writeMirror(win, activePartition, userTag) {
        try {
            win.localStorage.setItem(MIRROR_KEY, JSON.stringify({ activePartition, userTag }));
        } catch (e) {
            /* no mirror: a degraded start then announces, which is the safe side */
        }
    }

    // What the last start recorded: the ledger's meta, or on a degraded ledger the mirror.
    function previousOf(win, ledger, stored) {
        if (!ledger.degraded) {
            return stored;
        }
        const mirror = readMirror(win);
        if (!mirror) {
            return { activePartition: stored.activePartition, userTag: UNKNOWN_TAG };
        }
        return { activePartition: mirror.activePartition ?? null, userTag: mirror.userTag };
    }

    function pageLabel(doc) {
        const meta = doc.querySelector('meta[name="bw-partition"]');
        return meta ? meta.getAttribute('content') : null;
    }

    /** Returns the label this page caches under: a real label, or 'none' / 'offline' / 'unknown' (caches nothing). */
    async function begin(win, ledger) {
        const Wipe = win.bwAssetWipe;
        const env = { caches: win.caches, ledger, origin: win.location.origin };
        const stored = await ledger.getMeta();
        const meta = previousOf(win, ledger, stored);
        const label = pageLabel(win.document);
        // offline.html knows no user: it decides nothing here either.
        const offline = label === 'offline';
        const userTag = offline ? meta.userTag : Wipe.userTagOf(win.document);
        const userChanged = !offline && typeof meta.userTag === 'string' && meta.userTag !== userTag;
        const decision = Wipe.decide(meta.activePartition, label, !!Wipe.tokenOf(win.location), userChanged);
        if (decision.wipe) {
            await Wipe.wipe(env, decision.keep);
        } else if (REAL_LABEL.test(decision.page)) {
            // Every other partition's caches and rows, every start: a worker that stored under an old label meanwhile
            // (a stale tab, the window before a switch was seen) leaves nothing behind.
            await Wipe.wipe(env, decision.page);
        }
        if (decision.active !== stored.activePartition || userTag !== stored.userTag) {
            await ledger.setMeta({ activePartition: decision.active, userTag, proto: PROTO });
        }
        writeMirror(win, decision.active, userTag);
        if (decision.identityChanged) {
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

    return { begin, guard, pageLabel };
})();
