/*
 * bwAssetWipe — the wipe rules of the wall-asset cache (design 2026-10-21 §6.6, rules 1–6).
 *
 * Partitions are labels, not a boundary: a cache hit never reaches the server, so privacy on the device rests on
 * wiping. A wipe deletes every `bw-a1-*` cache and every ledger row outside the partition it keeps. It never touches
 * `blocwerk-offline` (unsent ticks), `bw-sw-state` (the worker's last label) or the shell cache.
 *
 *   1. A server-rendered page whose `bw-partition` meta is missing, `none`, or differs from the ledger's
 *      `activePartition` wipes, and its label becomes active. `offline` (offline.html, a server that could not
 *      decide) decides nothing. A switch from a known label also fires `bw:identity-changed` and clears the deploy
 *      drafts (`bwDeployDrafts.clearAll`, when that module is loaded).
 *   2. Logout: the header's /account/logout link and the kiosk unregister form wipe everything first, then go on;
 *      they fire the identity change as well (rules 1 and 6 do when they wipe after a known label).
 *      The server adds `Clear-Site-Data: "cache"` to those responses (PR 2.5) for the HTTP cache. The kiosk release
 *      form does NOT wipe: the tablet's `k-` partition is the same for every identity on it and holds only the bound
 *      wall's kiosk-class assets, so wiping on every member's release would only refill the tablet.
 *   3. A server-forced sign-out is caught by rule 1 on the next page.
 *   4. A manifest 403/404 drops that wall: its assets that no other wall lists go (dropWall).
 *   5. Revoked access does the same: dropWall is the hook, and a wall that left the viewer's /api/me/cache-summary
 *      list is dropped on the next start.
 *   6. `anon` hygiene: a page in the `anon` partition without a share token in its URL wipes `anon` too.
 */
window.bwAssetWipe = (function () {
    'use strict';

    const PREFIX = 'bw-a1-';
    const CACHE_NAME = /^bw-a1-(.+)-(img|3d)$/;
    const LOGOUT_PATH = '/account/logout';
    const UNREGISTER_PATH = '/kiosk/unregister';
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
     * meta content, null when the meta is missing. Returns { wipe, keep, active, identityChanged }, where `keep` is the
     * one partition a wipe spares (null spares none).
     */
    function decide(previous, label, hasToken) {
        if (label === 'offline') {
            return { wipe: false, keep: previous, active: previous, identityChanged: false };
        }
        const real = typeof label === 'string' && REAL_LABEL.test(label);
        const active = real ? label : 'none';
        const changed = active !== previous;
        const identityChanged = changed && previous !== null && previous !== undefined;
        if (!real) {
            return { wipe: true, keep: null, active, identityChanged };
        }
        // Rule 6 ends whatever the shared device last held, so it clears the device-kept drafts too.
        if (active === 'anon' && !hasToken) {
            return { wipe: true, keep: null, active, identityChanged: previous !== null && previous !== undefined };
        }
        return { wipe: changed, keep: active, active, identityChanged };
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

    // Wipes, but never holds the navigation longer than LOGOUT_WAIT_MS.
    function wipeThen(win, env, go) {
        let gone = false;
        const leave = () => {
            if (!gone) {
                gone = true;
                go();
            }
        };
        win.setTimeout(leave, LOGOUT_WAIT_MS);
        announceIdentityChange(win);
        env.ledger.setMeta({ activePartition: null })
            .then(() => wipe(env, null))
            .catch(() => undefined)
            .then(leave);
    }

    function pathOf(win, href) {
        try {
            return new win.URL(href, win.location.href).pathname;
        } catch (e) {
            return '';
        }
    }

    /**
     * The signed-out page (SignedOutMarkup.cs, PR 2.5) calls this, so an account sign-out wipes even when no
     * interceptor below ran (a forced load of /account/logout, a link outside the app's pages). It fires nothing: that
     * page dispatches `bw:identity-changed` itself. A second wipe after the interceptor's finds nothing to delete.
     */
    function signedOut(win) {
        return win.bwAssetLedger.open()
            .then(ledger => ledger.setMeta({ activePartition: null })
                .then(() => wipe({ caches: win.caches, ledger, origin: win.location.origin }, null)))
            .catch(() => undefined);
    }

    /**
     * Rule 2: the logout link and the kiosk unregister form wipe first. `env` is resolved lazily (getEnv). On a kiosk
     * (`k-` partition) the logout link only ends a member's session, a plain redirect server-side, so it keeps the
     * tablet's cache; the next page's acting-user check clears the drafts.
     */
    function installLogout(win, getEnv) {
        win.document.addEventListener('click', event => {
            const link = event.target && event.target.closest ? event.target.closest('a[href]') : null;
            if (!link || pathOf(win, link.getAttribute('href')) !== LOGOUT_PATH || event.defaultPrevented) {
                return;
            }
            event.preventDefault();
            const go = () => win.location.assign(link.href);
            getEnv()
                .then(env => env.ledger.getMeta().then(meta => (String(meta.activePartition).startsWith('k-') ? go() : wipeThen(win, env, go))))
                .catch(go);
        }, true);
        win.document.addEventListener('submit', event => {
            const form = event.target;
            if (!form || form.dataset?.bwWiped === '1' || pathOf(win, form.getAttribute('action') || '') !== UNREGISTER_PATH) {
                return;
            }
            event.preventDefault();
            const go = () => {
                form.dataset.bwWiped = '1';
                form.submit();
            };
            getEnv().then(env => wipeThen(win, env, go), go);
        }, true);
    }

    return { tokenOf, decide, wipe, deleteKeys, dropWall, userTagOf, announceIdentityChange, installLogout, cacheUrl, signedOut: () => signedOut(window) };
})();
