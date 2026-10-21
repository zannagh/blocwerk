/*
 * bwAssetWipe — the wipe rules of the wall-asset cache (design 2026-10-21 §6.6, rules 1–6).
 *
 * Partitions are labels, not a boundary: a cache hit never reaches the server, so privacy on the device rests on
 * wiping. A wipe deletes every `bw-a1-*` cache and every ledger row outside the partition it keeps. It never touches
 * `blocwerk-offline` (unsent ticks), `bw-sw-state` (the worker's last label) or the shell cache.
 *
 *   1. A server-rendered page whose `bw-partition` meta is missing, `none`, or differs from the ledger's
 *      `activePartition` wipes, and its label becomes active. `offline` (offline.html only) decides nothing.
 *      `unknown` (the server could not resolve it) caches nothing on that page and wipes only if the acting user
 *      changed. A changed acting user (`data-bw-user`, which also covers a kiosk member's release) or a change of
 *      the label's class (u- / k- / anon / none) fires `bw:identity-changed` and clears the deploy drafts
 *      (`bwDeployDrafts.clearAll`, when loaded). A new label of the same class and user (a partition-key rotation)
 *      wipes but announces nothing.
 *   2. Logout: a plain click on the /account/logout link wipes everything first, then goes on, and fires the identity
 *      change. The server answers account sign-out and kiosk unregister with a signed-out page (PR 2.5) that carries
 *      `Clear-Site-Data: "cache"` and calls signedOut() itself, so a sign-out the interceptor missed wipes too. The
 *      kiosk release does NOT wipe: the tablet's `k-` partition is the same for every identity on it and holds only
 *      the bound wall's kiosk-class assets, so wiping on every member's release would only refill the tablet.
 *   3. A server-forced sign-out is caught by rule 1 on the next page.
 *   4. A manifest 403/404 drops that wall: its assets that no other wall lists go (dropWall).
 *   5. Revoked access does the same: dropWall is the hook, and a wall that left the viewer's /api/me/cache-summary
 *      list is dropped on the next start.
 *   6. `anon` hygiene: a page in the `anon` partition without a share token in its URL wipes `anon` too; it
 *      announces only when the device was not anonymous before.
 */
window.bwAssetWipe = (function () {
    'use strict';

    const PREFIX = 'bw-a1-';
    const CACHE_NAME = /^bw-a1-(.+)-(img|3d)$/;
    const LOGOUT_PATH = '/account/logout';
    const LOGOUT_WAIT_MS = 1500;
    const REAL_LABEL = /^(anon|[uk]-[a-z2-7]{20})$/;

    /** The share token in a page URL: `/…/shared/{token}[/…]` or `?token=`. */
    function tokenOf(location) {
        const match = /\/shared\/([^/?#]+)/.exec(location.pathname || '');
        if (match) {
            return decodeURIComponent(match[1]);
        }
        try {
            return new URLSearchParams(location.search || '').get('token');
        } catch (e) {
            return null;
        }
    }

    /**
     * What a page load means. `previous`: the ledger's activePartition (null on a first run). `label`: the page's
     * meta content, null when the meta is missing. `userChanged`: the acting user differs from the last page's.
     * Returns { wipe, keep, active, page, identityChanged }: `keep` is the one partition a wipe spares (null spares
     * none), `active` what the ledger records, `page` the label this page caches under (only a real label caches).
     *   offline  offline.html: decides nothing.
     *   unknown  the server could not resolve the partition: trust nothing, cache nothing on this page, keep the
     *            ledger's label; but a changed acting user still wipes everything.
     */
    function decide(previous, label, hasToken, userChanged) {
        if (label === 'offline') {
            return { wipe: false, keep: previous, active: previous, page: 'offline', identityChanged: false };
        }
        if (label === 'unknown') {
            return userChanged
                ? { wipe: true, keep: null, active: 'none', page: 'unknown', identityChanged: true }
                : { wipe: false, keep: previous, active: previous, page: 'unknown', identityChanged: false };
        }
        const real = typeof label === 'string' && REAL_LABEL.test(label);
        const active = real ? label : 'none';
        const changed = active !== previous;
        // A label is not an identity: a server restarting on a run-only partition key (Development, or after an I/O
        // fault) gives the same user a new label. That wipes the cache, but announces nothing, so the drafts a deploy
        // kept for this very reload survive. Only a changed acting user or a change of class (u- / k- / anon / none)
        // is an identity change; anon→anon (rule 6 on every tokenless load) is not.
        const known = previous !== null && previous !== undefined;
        const identityChanged = !!userChanged || (known && classOf(active) !== classOf(previous));
        if (!real) {
            return { wipe: true, keep: null, active, page: active, identityChanged };
        }
        if (active === 'anon' && !hasToken) {
            return { wipe: true, keep: null, active, page: active, identityChanged };
        }
        return { wipe: changed, keep: active, active, page: active, identityChanged };
    }

    function classOf(label) {
        const value = String(label);
        return value.startsWith('u-') ? 'u' : value.startsWith('k-') ? 'k' : value;
    }

    function cacheUrl(origin, key) {
        return `${origin}/__bw/${key}`;
    }

    /** Deletes every bw-a1-* cache outside `keep`, then the ledger rows outside it. */
    async function wipe(env, keep) {
        try {
            const names = await env.caches.keys();
            for (const name of names) {
                const match = CACHE_NAME.exec(name);
                if (name.startsWith(PREFIX) && (!match || match[1] !== keep)) {
                    await env.caches.delete(name);
                }
            }
        } catch (e) {
            /* storage unavailable: nothing to wipe there */
        }
        await env.ledger.deleteAllExcept(keep);
    }

    /** Deletes `keys` (`{sha}[/{width}]`) from a partition's caches and its ledger. */
    async function deleteKeys(env, partition, keys) {
        if (keys.length === 0) {
            return;
        }
        for (const suffix of ['img', '3d']) {
            try {
                if (!(await env.caches.has(`${PREFIX}${partition}-${suffix}`))) {
                    continue;
                }
                const cache = await env.caches.open(`${PREFIX}${partition}-${suffix}`);
                for (const key of keys) {
                    await cache.delete(cacheUrl(env.origin, key));
                }
            } catch (e) {
                /* a refused open: the reconcile on the next start catches up */
            }
        }
        for (const key of keys) {
            await env.ledger.deleteAsset(partition, key);
        }
    }

    /** Rules 4 and 5: the wall's row goes, and its assets that no other wall of the partition lists. */
    async function dropWall(env, partition, wallId) {
        const walls = await env.ledger.wallsOf(partition);
        const dropped = walls.find(w => w.wallId === wallId);
        await env.ledger.deleteWall(partition, wallId);
        if (!dropped) {
            return;
        }
        const others = new Set();
        walls.filter(w => w.wallId !== wallId).forEach(w => (w.entries || []).forEach(e => others.add(e.sha)));
        const mine = new Set((dropped.entries || []).map(e => e.sha).filter(sha => sha && !others.has(sha)));
        const assets = await env.ledger.assetsOf(partition);
        await deleteKeys(env, partition, assets.filter(a => mine.has(a.sha)).map(a => a.key));
    }

    /**
     * A short tag of the page's acting user (`data-bw-user` on <body>, empty when anonymous): a 32-bit FNV-1a hash, enough
     * to see a change without keeping the id in the ledger.
     */
    function userTagOf(doc) {
        const id = (doc.body && doc.body.getAttribute('data-bw-user')) || '';
        let h = 0x811c9dc5;
        for (let i = 0; i < id.length; i++) {
            h = Math.imul(h ^ id.charCodeAt(i), 0x01000193) >>> 0;
        }
        return id ? h.toString(36) : '';
    }

    // `bw:identity-changed` is what deploy-drafts.js listens for; clearAll() is also called directly (idempotent) so the
    // kept copies go even if that listener is missing. Both are feature-detected.
    function announceIdentityChange(win) {
        try {
            win.dispatchEvent(new win.CustomEvent('bw:identity-changed'));
        } catch (e) {
            /* no listeners can break the page */
        }
        try {
            if (win.bwDeployDrafts && typeof win.bwDeployDrafts.clearAll === 'function') {
                win.bwDeployDrafts.clearAll();
            }
        } catch (e) {
            /* the drafts module's own failure is not ours */
        }
    }

    function pathOf(win, href) {
        try {
            return new win.URL(href, win.location.href).pathname;
        } catch (e) {
            return '';
        }
    }

    async function wipeAll(win, ledger) {
        await ledger.setMeta({ activePartition: null });
        await wipe({ caches: win.caches, ledger, origin: win.location.origin }, null);
    }

    /**
     * The signed-out page (SignedOutMarkup.cs, PR 2.5) calls this, so an account sign-out wipes even when no
     * interceptor below ran (a forced load of /account/logout, a link outside the app's pages, a kiosk unregister). It
     * fires nothing: that page dispatches `bw:identity-changed` itself. A second wipe after the interceptor's finds
     * nothing to delete.
     */
    function signedOut(win) {
        return win.bwAssetLedger.open().then(ledger => wipeAll(win, ledger)).catch(() => undefined);
    }

    const isPlainClick = event => (event.button === undefined || event.button === 0)
        && !event.metaKey && !event.ctrlKey && !event.shiftKey && !event.altKey;

    /**
     * Rule 2: a plain click on the logout link wipes first, then follows it. The LOGOUT_WAIT_MS timer is armed before
     * anything is awaited, so a ledger that never opens cannot hold the navigation. A modified or non-primary click
     * (a new tab, a download) is left to the browser. On a kiosk (the page's meta is `k-…`) the link only ends a member's
     * session, a plain redirect server-side, so it keeps the tablet's cache; the next page's acting-user check clears the
     * drafts.
     */
    function installLogout(win, getLedger) {
        win.document.addEventListener('click', event => {
            const link = event.target && event.target.closest ? event.target.closest('a[href]') : null;
            if (!link || event.defaultPrevented || !isPlainClick(event) || pathOf(win, link.getAttribute('href')) !== LOGOUT_PATH) {
                return;
            }
            const meta = win.document.querySelector('meta[name="bw-partition"]');
            if (meta && String(meta.getAttribute('content')).startsWith('k-')) {
                return;
            }
            event.preventDefault();
            let gone = false;
            const go = () => {
                if (!gone) {
                    gone = true;
                    win.location.assign(link.href);
                }
            };
            win.setTimeout(go, LOGOUT_WAIT_MS);
            announceIdentityChange(win);
            Promise.resolve()
                .then(getLedger)
                .then(ledger => wipeAll(win, ledger))
                .catch(() => undefined)
                .then(go);
        }, true);
    }

    return { tokenOf, decide, wipe, deleteKeys, dropWall, userTagOf, announceIdentityChange, installLogout, cacheUrl, signedOut: () => signedOut(window) };
})();
