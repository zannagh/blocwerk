/*
 * bwAssetNotice — the storage notices of the wall-asset cache, driven by StorageNotice.razor (design 2026-10-21 §6.8,
 * decisions of 2026-10-21).
 *
 *   high-storage: the viewer belongs to HIGH_STORAGE_WALLS or more walls with 3D data (/api/me/cache-summary's
 *                 `wallsWith3d`) and still keeps 3D. It offers the 3D opt-out.
 *   install-ios:  an iPhone/iPad Safari tab that holds cached walls. Tab storage there is best-effort and can be
 *                 evicted; the installed app is where it persists (the storage probe). Not while the general install
 *                 banner (bwInstall) is showing, which already says the same.
 * Signed-in viewers only (a `u-` partition): never on a kiosk or for an anonymous viewer. A dismissal is remembered
 * per user, keyed by the partition label (an HMAC of the user id, so no id is stored), in localStorage.
 */
window.bwAssetNotice = (function () {
    'use strict';

    const HIGH_STORAGE_WALLS = 3;
    const DISMISS_KEY = 'blocwerk-cache-notice';

    function storageKey(kind, partition) {
        return `${DISMISS_KEY}:${kind}:${partition}`;
    }

    function isDismissed(kind, partition) {
        try {
            return window.localStorage.getItem(storageKey(kind, partition)) === '1';
        } catch (e) {
            return false;
        }
    }

    /** The notice to show, or null. facts: { partition, summary, keep3d, ios, standalone, cachedWalls, installShowing, dismissed(kind) }. */
    function decide(facts) {
        if (typeof facts.partition !== 'string' || !facts.partition.startsWith('u-')) {
            return null;
        }
        const with3d = facts.summary ? facts.summary.wallsWith3d : 0;
        if (with3d >= HIGH_STORAGE_WALLS && facts.keep3d && !facts.dismissed('high-storage')) {
            return { kind: 'high-storage', wallsWith3d: with3d };
        }
        if (facts.ios && !facts.standalone && facts.cachedWalls > 0 && !facts.installShowing && !facts.dismissed('install-ios')) {
            return { kind: 'install-ios', wallsWith3d: with3d };
        }
        return null;
    }

    function isIos() {
        const ua = navigator.userAgent || '';
        return /iPhone|iPad|iPod/.test(ua) || (/Macintosh/.test(ua) && navigator.maxTouchPoints > 1);
    }

    function isStandalone() {
        try {
            if (window.matchMedia && window.matchMedia('(display-mode: standalone)').matches) {
                return true;
            }
        } catch (e) {
            /* fall through */
        }
        return navigator.standalone === true;
    }

    async function current() {
        try {
            const cache = window.bwAssetCache;
            if (!cache || !cache.ready) {
                return null;
            }
            const summary = await cache.summary();
            const s = cache.state();
            const walls = s.env ? await s.env.ledger.wallsOf(s.active) : [];
            const install = window.bwInstall && typeof window.bwInstall.shouldShow === 'function' && window.bwInstall.shouldShow();
            return decide({
                partition: s.active, summary, keep3d: cache.keep3d(), ios: isIos(), standalone: isStandalone(),
                cachedWalls: walls.length, installShowing: !!install, dismissed: kind => isDismissed(kind, s.active)
            });
        } catch (e) {
            return null;
        }
    }

    function dismiss(kind) {
        try {
            const partition = window.bwAssetCache.state().active;
            window.localStorage.setItem(storageKey(kind, partition), '1');
        } catch (e) {
            /* storage blocked: the notice still hides for this page */
        }
    }

    function skip3d() {
        dismiss('high-storage');
        return window.bwAssetCache ? window.bwAssetCache.setKeep3d(false) : Promise.resolve();
    }

    return { HIGH_STORAGE_WALLS, decide, current, dismiss, skip3d };
})();
