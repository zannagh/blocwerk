/*
 * bwAssetSession — what a page load decides about the wall-asset cache before anything else runs (design 2026-10-21
 * §6.6), and the ledger the rest of the page writes through.
 *
 * begin(): reads the page's `bw-partition` meta and acting user (`data-bw-user`), applies the wipe decision
 * (asset-wipe.js decide), records the new label and user tag in the ledger's meta, and fires the identity change when
 * it applies. Under a real label it also deletes every bw-a1-* cache and ledger row of any other partition, every
 * start, so nothing a stale tab or worker wrote under an older identity survives.
 *
 * guard(): a ledger whose writes (putAsset, updateWall) go through only while the ledger's `activePartition` is the
 * row's partition. A tab still showing an old identity (another tab signed out or in since) can then never rewrite
 * rows for the partition it no longer owns.
 */
window.bwAssetSession = (function () {
    'use strict';

    const PROTO = 1;
    const REAL_LABEL = /^(anon|[uk]-[a-z2-7]{20})$/;

    function pageLabel(doc) {
        const meta = doc.querySelector('meta[name="bw-partition"]');
        return meta ? meta.getAttribute('content') : null;
    }

    /** Returns the label this page caches under: a real label, or 'none' / 'offline' / 'unknown' (caches nothing). */
    async function begin(win, ledger) {
        const Wipe = win.bwAssetWipe;
        const env = { caches: win.caches, ledger, origin: win.location.origin };
        const meta = await ledger.getMeta();
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
        if (decision.active !== meta.activePartition || userTag !== meta.userTag) {
            await ledger.setMeta({ activePartition: decision.active, userTag, proto: PROTO });
        }
        if (decision.identityChanged) {
            Wipe.announceIdentityChange(win);
        }
        return decision.page;
    }

    function guard(ledger) {
        const owned = async row => (await ledger.getMeta()).activePartition === row.partition;
        return Object.assign(Object.create(ledger), {
            updateWall: async (partition, wallId, change) => ((await owned({ partition })) ? ledger.updateWall(partition, wallId, change) : undefined),
            putAsset: async row => ((await owned(row)) ? ledger.putAsset(row) : undefined)
        });
    }

    return { begin, guard, pageLabel };
})();
