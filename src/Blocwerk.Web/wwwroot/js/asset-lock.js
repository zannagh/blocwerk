/*
 * bwAssetLock — the 401 lock of the wall-asset cache (Phase 3 plan §1, PR 3.5; user decision: hold, and ask to sign in).
 *
 * When a signed-in device's session EXPIRES, its `u-` partition is not wiped: it is LOCKED, `meta.locked = { label,
 * since }`, and the active label becomes `anon`. Locked data is kept but never served (the worker refuses the label:
 * sw-asset-lock.js reads the marker this file writes to `bw-sw-state`) and never rendered (every reader goes through the
 * active label; the page shows only "Sign in to keep your offline data" and "Clear offline data", the banner below).
 *
 *   lock     a page `anon` whose meta says `data-signout="expired"` while the active label is `u-…` (asset-session.js),
 *            or a 401 `expired` on the manifest, the docs index or the marks of a running `u-` page (refused()).
 *   kept     across `anon` pages that say `expired` (the login page, a share link, rule 6 on a tokenless page): their
 *            wipes and the every-start sweep keep { active, locked } (asset-wipe.js keep sets).
 *   unlock   the same `u-` label returns (it is an HMAC of the user id): the lock clears, the label is active again.
 *   wipe     another `u-` or `k-` label, `none`, an `anon` page with any other reason (`forced`, `gone`, `signed-out`,
 *            none at all),
 *            an `unknown` page whose acting user changed, logout and the signed-out page, "Clear offline data", a 401
 *            `forced`/`gone` or any 410 of a running page, and `since + LOCK_MS`, checked on every start.
 * Kiosk and anonymous partitions never lock: there is no identity to return to. A 401 or 410 there wipes them.
 * Nothing here throws into the page.
 */
window.bwAssetLock = (function (win) {
    'use strict';

    const LOCK_MS = 7 * 24 * 60 * 60 * 1000;
    const SKEW_MS = 60 * 60 * 1000;
    const EXPIRED = 'expired';
    const HEADER = 'x-blocwerk-signout';
    const USER_LABEL = /^u-[a-z2-7]{20}$/;
    const STATE_CACHE = 'bw-sw-state';
    const MARKER_KEY = '/__bw/locked';
    const BANNER_ID = 'bw-offline-lock';

    const Wipe = () => win.bwAssetWipe;

    /** A stored lock as { label, since }, or null when it is none or malformed. */
    function valid(lock) {
        return lock && USER_LABEL.test(String(lock.label)) && Number.isFinite(lock.since) ? { label: lock.label, since: lock.since } : null;
    }

    /** The `data-signout` of the page's `bw-partition` meta, or null. */
    function signOutOf(doc) {
        const meta = doc.querySelector('meta[name="bw-partition"]');
        return meta ? meta.getAttribute('data-signout') : null;
    }

    /**
     * The lock after a page load. `previous`: the active label before it; `lock`: the stored lock; page: { label,
     * reason, userChanged }. Returns { lock, drop, unlocked }: `lock` the lock to keep (its label joins the keep set),
     * `drop` a locked label to wipe now (an expired or a dropped lock), `unlocked` when the page is the locked label.
     */
    function plan(previous, lock, page, now) {
        let current = valid(lock);
        let drop = null;
        // Past LOCK_MS, or stamped in the future beyond SKEW_MS (a clock that jumped): the lock goes.
        if (current && (now - current.since >= LOCK_MS || current.since > now + SKEW_MS)) {
            drop = current.label;
            current = null;
        }
        const dropped = () => ({ lock: null, drop: current ? current.label : drop, unlocked: false });
        if (page.label === 'offline') {
            return { lock: current, drop, unlocked: false };
        }
        if (page.label === 'unknown') {
            return page.userChanged ? dropped() : { lock: current, drop, unlocked: false };
        }
        if (page.label === 'anon' && page.reason === EXPIRED) {
            if (!USER_LABEL.test(String(previous))) {
                return { lock: current, drop, unlocked: false };
            }
            const since = current && current.label === previous ? current.since : now;
            return { lock: { label: previous, since }, drop: current && current.label !== previous ? current.label : drop, unlocked: false };
        }
        if (current && page.label === current.label) {
            return { lock: null, drop, unlocked: true };
        }
        return dropped();
    }

    // The worker's copy of the lock: a label it never serves or stores under (sw-asset-lock.js).
    async function mark(lock) {
        try {
            const cache = await win.caches.open(STATE_CACHE);
            if (lock) {
                await cache.put(MARKER_KEY, new win.Response(lock.label));
            } else {
                await cache.delete(MARKER_KEY);
            }
        } catch (e) {
            /* no storage: the worker serves the active label only, which is never the locked one online */
        }
    }

    // Have the worker read the cookie's label and the marker again (sw-refresh-partition), without waiting for it.
    function tellWorker() {
        try {
            const store = win.bwAssetStore;
            if (store && typeof store.refreshWorkerPartition === 'function') {
                store.refreshWorkerPartition(win).catch(() => undefined);
                return;
            }
            const controller = win.navigator.serviceWorker && win.navigator.serviceWorker.controller;
            if (controller) {
                controller.postMessage({ proto: 1, type: 'sw-refresh-partition' });
            }
        } catch (e) {
            /* no worker */
        }
    }

    /** Shows or hides the sign-in banner (BlocwerkApp.razor's #bw-offline-lock), and wires its Clear button once. */
    function render(lock) {
        const banner = win.document.getElementById && win.document.getElementById(BANNER_ID);
        if (!banner) {
            return;
        }
        banner.hidden = !lock;
        const button = banner.querySelector('[data-bw-lock-clear]');
        if (button && !button.dataset.bwWired) {
            button.dataset.bwWired = '1';
            button.addEventListener('click', () => { clear().then(() => render(null)); });
        }
    }

    /** Writes a changed lock to the worker's marker and tells the worker; the banner follows. */
    async function sync(lock, changed) {
        if (changed) {
            await mark(lock);
            tellWorker();
            if (lock) {
                tellTabs(lock.label);
            }
        }
        render(lock);
    }

    /** The stored lock of this device, or null (for the banner after an enhanced navigation, and 3.10's client). */
    async function current() {
        try {
            const ledger = await win.bwAssetLedger.open();
            const stored = valid((await ledger.getMeta()).locked);
            if (stored || !ledger.degraded) {
                return stored;
            }
            return valid((win.bwAssetSession && win.bwAssetSession.mirror(win) || {}).locked);
        } catch (e) {
            return null;
        }
    }

    /** "Clear offline data" for a locked device: the locked partition goes, with the lock. */
    async function clear() {
        try {
            const ledger = await win.bwAssetLedger.open();
            const lock = await current();
            if (lock) {
                await Wipe().dropPartition({ caches: win.caches, ledger, origin: win.location.origin }, lock.label);
            }
            await forget(ledger);
            return !!lock;
        } catch (e) {
            return false;
        }
    }

    /** Forgets the lock everywhere it is kept: the ledger's meta, the identity mirror, the worker's marker. */
    async function forget(ledger) {
        const had = !!valid((await ledger.getMeta()).locked);
        if (had) {
            await ledger.setMeta({ locked: null });
        }
        const session = win.bwAssetSession;
        const saved = session && typeof session.mirror === 'function' ? session.mirror(win) : null;
        if (saved && valid(saved.locked)) {
            session.mirror(win, { locked: null });
        }
        // The marker always goes: a caller may have cleared the meta's lock already (logout).
        await sync(null, true);
        return had;
    }

    // A running page stops caching at once: nothing more is written or filled under the label it held.
    function stop(state) {
        state.stopped = true;
        state.active = 'none';
    }

    // The other tabs of `label` stop too (asset-clear.js's channel), so none keeps showing or writing it.
    function tellTabs(label) {
        try {
            if (win.bwAssetClear && typeof win.bwAssetClear.announce === 'function') {
                win.bwAssetClear.announce({ type: 'locked', partition: label });
            }
        } catch (e) {
            /* no channel: the other tabs find out on their next page or request */
        }
    }

    /** Another tab locked or wiped this tab's partition: stop, and show the banner when it is a lock. */
    function stopTab(state) {
        stop(state);
        current().then(render).catch(() => undefined);
    }

    /**
     * A 401 or 410 of a running page (the manifest, the docs index, the marks): a `u-` label locks on a 401 `expired`
     * and is wiped otherwise; `anon` and `k-` are wiped. Only while the ledger still names this page's label: a tab
     * another tab already moved on only stops.
     */
    async function refused(state, response) {
        const label = state.active;
        const env = state.env;
        if (!env || typeof label !== 'string' || label === 'none') {
            return;
        }
        stop(state);
        const meta = await env.ledger.getMeta();
        if (meta.activePartition !== label) {
            return;
        }
        const reason = response.headers && typeof response.headers.get === 'function' ? response.headers.get(HEADER) : null;
        if (USER_LABEL.test(label) && response.status === 401 && reason === EXPIRED) {
            const lock = { label, since: Date.now() };
            await env.ledger.setMeta({ activePartition: 'anon', locked: lock });
            if (win.bwAssetSession && typeof win.bwAssetSession.mirror === 'function') {
                win.bwAssetSession.mirror(win, { activePartition: 'anon', locked: lock });
            }
            await sync(lock, true);
            return;
        }
        tellTabs(label);
        if (!USER_LABEL.test(label)) {
            await Wipe().dropPartition(env, label);
            tellWorker();
            return;
        }
        await env.ledger.setMeta({ activePartition: null });
        await Wipe().wipe(env, []);
        await forget(env.ledger);
        Wipe().announceIdentityChange(win);
    }

    // An enhanced navigation merges the server's markup over the banner (hidden again): show it again when locked.
    if (win.Blazor && typeof win.Blazor.addEventListener === 'function') {
        win.Blazor.addEventListener('enhancedload', () => { current().then(render).catch(() => undefined); });
    }

    return { LOCK_MS, SKEW_MS, EXPIRED, HEADER, MARKER_KEY, valid, signOutOf, plan, sync, render, current, clear, forget, refused, stopTab };
})(window);
