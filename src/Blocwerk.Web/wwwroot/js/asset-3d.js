/*
 * bwAsset3d — the 3D side of the on-device wall-asset cache (design 2026-10-21 §6.8, PR 2.8, decisions of 2026-10-21):
 * the mobile-data notice before a 3D view starts, "Save 3D offline", and the ledger's record of view3d documents.
 *
 *   gate(wallId)     → { ask, bytes }: whether Wall3DStage asks before starting the 3D view. Only on a phone or a tablet
 *                      (bwAssetPlan.isMobile, iOS and Android; they never background-fill 3D), never on a kiosk, never
 *                      once the wall's 3D is on the device (its textures, masks and one splat level), and never after
 *                      "Don't ask again" (kept per user like the storage notices) or a "Continue" on this page.
 *                      `bytes` is what is still to download, from the wall's manifest; null when it is not known.
 *   accept(wallId, remember)  the viewer chose to continue.
 *   status(wallId)   → { available, saved, cached, install }: whether "Save 3D offline" is offered (a signed-in viewer,
 *                      a worker that caches, a page that is not streaming, and not an iOS Safari tab, whose storage is not
 *                      persisted and is wiped after 7 days unused) and whether the wall's 3D is saved. In that iOS tab
 *                      `install` holds bwInstall's instructions instead: the button becomes the install nudge.
 *   save(wallId)     → { saved }: marks the wall keepOffline and save3d in the ledger, asks for persistent storage, and
 *                      fills the wall's 3D now (asset-plan.js plans a saved wall's 3D on every device and connection,
 *                      past the 3D budget and the opt-out; eviction never takes it). A browser that refuses the storage
 *                      leaves the page streaming (asset-fill.js), silently: `saved` is simply false.
 *   unsave(wallId)   → { saved: false }: the undo. Clears save3d and keepOffline and runs eviction, so the budget and
 *                      the opt-out apply to the wall's 3D again.
 *   held(shas)       → Set of those shas the ledger holds in this page's partition (the photo-real ladder skips the
 *                      splat levels between the ones on the device); empty when nothing caches here.
 *   noteDoc(wallId, sha, bytes)  wall3d-doc.js loaded a view3d document; the wall's row lists it (the newest
 *                      MAX_DOCS), so eviction counts it as that wall's 3D instead of an orphan. Its size is the ledger's
 *                      count of the cached copy when there is one (the worker's Content-Length), else the page's.
 *                      It is also a 3D view opened online, so the offline client keeps the 3D code from then on
 *                      (bwClientPrecache.store3d, PR 3.13: phones precache none until then; with the 3D opt-out
 *                      only once a wall is saved for 3D, which save() also stores it for).
 * Every failure answers "no notice" / "not available": nothing here may keep a 3D view from starting.
 */
window.bwAsset3d = (function (win) {
    'use strict';

    const NOTICE_KIND = 'data-3d';
    const DISMISS_KEY = 'blocwerk-cache-notice';
    const READY_WAIT_MS = 2000;
    const MAX_DOCS = 3;
    const accepted = new Set();

    const Cache = () => win.bwAssetCache;
    const Plan = () => win.bwAssetPlan;
    const lower = wallId => String(wallId || '').toLowerCase();
    const wait = ms => new Promise(resolve => win.setTimeout(resolve, ms));

    // The coordinator's state once it started (bounded: a hung ledger must not hold the 3D view back).
    async function settled() {
        const cache = Cache();
        if (!cache || !cache.ready) {
            return null;
        }
        await Promise.race([Promise.resolve(cache.ready).catch(() => undefined), wait(READY_WAIT_MS)]);
        return cache.state();
    }

    // bwAssetPlan's one phone-or-tablet test, with matchMedia for the Android desktop-mode rule (a coarse pointer).
    const isMobile = () => Plan().isMobile(win.navigator, typeof win.matchMedia === 'function' ? q => win.matchMedia(q) : null);

    // An iPhone or iPad Safari tab, not the installed app: its storage is best-effort and wiped after 7 days unused.
    function isIosTab() {
        const nav = win.navigator || {};
        const ua = nav.userAgent || '';
        const ios = /iPhone|iPad|iPod/.test(ua) || (/Macintosh/.test(ua) && nav.maxTouchPoints > 1);
        let standalone = nav.standalone === true;
        try {
            standalone = standalone || (typeof win.matchMedia === 'function' && win.matchMedia('(display-mode: standalone)').matches);
        } catch (e) {
            /* treated as a tab */
        }
        return ios && !standalone;
    }

    function installHint() {
        const install = win.bwInstall;
        const how = install && typeof install.instructions === 'function' ? install.instructions() : 'In Safari: tap Share, then "Add to Home Screen".';
        return `Install the app to keep 3D offline. ${how}`;
    }

    const labelOf = s => (s && typeof s.active === 'string' ? s.active : 'none');
    const caching = s => !!(s && s.env && /^(anon|[uk]-[a-z2-7]{20})$/.test(labelOf(s)));

    function isKiosk(s) {
        const body = win.document && win.document.body;
        return !!(body && body.dataset && body.dataset.bwKiosk) || labelOf(s).startsWith('k-');
    }

    function dismissKey(s) {
        return `${DISMISS_KEY}:${NOTICE_KIND}:${labelOf(s)}`;
    }

    function dismissed(s) {
        try {
            return win.localStorage.getItem(dismissKey(s)) === '1';
        } catch (e) {
            return false;
        }
    }

    async function rowOf(s, wallId) {
        return caching(s) ? (await s.env.ledger.getWall(s.active, wallId)) || null : null;
    }

    /**
     * What the wall's 3D still needs on this device: textures and masks, plus one splat level unless one is cached (the
     * smallest, the one a visit starts on). `cached` when nothing is missing; `bytes` null without a manifest.
     */
    async function missing(s, row) {
        const entries = row ? (row.entries || []).filter(e => e.tier === 'view3d' && e.sha) : [];
        if (entries.length === 0) {
            return { cached: false, bytes: null };
        }
        const keys = new Set((await s.env.ledger.assetsOf(s.active)).map(a => a.key));
        const splats = entries.filter(e => e.kind === 'splat');
        const absent = entries.filter(e => e.kind !== 'splat' && !keys.has(e.sha));
        if (splats.length > 0 && !splats.some(e => keys.has(e.sha))) {
            absent.push(splats.slice().sort((a, b) => (a.splats || Infinity) - (b.splats || Infinity))[0]);
        }
        return { cached: absent.length === 0, bytes: absent.reduce((sum, e) => sum + (e.bytes || 0), 0) };
    }

    async function gate(wallId) {
        try {
            const id = lower(wallId);
            if (!Plan() || !isMobile() || accepted.has(id)) {
                return { ask: false, bytes: null };
            }
            const s = await settled();
            if (isKiosk(s) || dismissed(s)) {
                return { ask: false, bytes: null };
            }
            const need = caching(s) ? await missing(s, await rowOf(s, id)) : { cached: false, bytes: null };
            return need.cached ? { ask: false, bytes: null } : { ask: true, bytes: need.bytes };
        } catch (e) {
            return { ask: false, bytes: null };
        }
    }

    async function accept(wallId, remember) {
        accepted.add(lower(wallId));
        if (remember) {
            try {
                win.localStorage.setItem(dismissKey(await settled()), '1');
            } catch (e) {
                /* storage blocked: the notice still stays away for this page */
            }
        }
    }

    function offerable(s) {
        return caching(s) && labelOf(s).startsWith('u-') && !isKiosk(s) && !!s.capable && !!s.runner && !s.runner.streaming;
    }

    const none = { available: false, saved: false, cached: false, install: null };

    async function status(wallId) {
        try {
            const s = await settled();
            if (!offerable(s)) {
                return none;
            }
            if (isIosTab()) {
                return Object.assign({}, none, { install: installHint() });
            }
            const row = await rowOf(s, lower(wallId));
            const cached = (await missing(s, row)).cached;
            return { available: true, saved: !!(row && row.keepOffline && row.save3d && cached), cached, install: null };
        } catch (e) {
            return none;
        }
    }

    async function unsave(wallId) {
        try {
            const s = await settled();
            const id = lower(wallId);
            const row = caching(s) ? await rowOf(s, id) : null;
            if (row && (row.keepOffline || row.save3d)) {
                await s.env.ledger.updateWall(s.active, id, current => (current ? Object.assign(current, { keepOffline: false, save3d: false }) : undefined));
                await Cache().evict();
            }
        } catch (e) {
            /* the wall stays saved; the button offers the removal again */
        }
        return { saved: false };
    }

    // keepOffline (2.7's field: eviction never takes the wall) and save3d (its 3D is filled whatever the device).
    // Through the ledger's one merge (updateWall): a manifest round in flight writes only its own fields, so it can no
    // longer drop the mark.
    async function mark(s, wallId) {
        let changed = false;
        await s.env.ledger.updateWall(s.active, wallId, current => {
            const row = current || { partition: s.active, wallId, entries: [], etag: null, cursor: null, pending: 0, lastOpened: Date.now() };
            changed = !(row.keepOffline && row.save3d);
            return Object.assign(row, { keepOffline: true, save3d: true });
        });
        return changed;
    }

    function requestPersist() {
        try {
            const storage = win.navigator.storage;
            return storage && typeof storage.persist === 'function' ? storage.persist().catch(() => false) : Promise.resolve(false);
        } catch (e) {
            return Promise.resolve(false);
        }
    }

    async function save(wallId) {
        // Asked first, while the click's user activation still holds (Firefox prompts for it).
        const persisting = requestPersist();
        try {
            const s = await settled();
            const id = lower(wallId);
            if (!offerable(s) || isIosTab()) {
                return { saved: false };
            }
            await mark(s, id);
            await persisting;
            await Cache().manifestChanged(id);
            await Cache().filled();
            // The offline client's 3D code too, now that the wall's 3D shows whatever the opt-out says.
            if (win.bwClientPrecache) {
                win.bwClientPrecache.store3d();
            }
            return { saved: (await status(id)).saved };
        } catch (e) {
            return { saved: false };
        }
    }

    async function held(shas) {
        try {
            const s = await settled();
            if (!caching(s)) {
                return new Set();
            }
            const keys = new Set((await s.env.ledger.assetsOf(s.active)).map(a => a.key));
            return new Set((shas || []).filter(sha => typeof sha === 'string' && keys.has(sha)));
        } catch (e) {
            return new Set();
        }
    }

    async function noteDoc(wallId, sha, bytes) {
        if (win.bwClientPrecache) {
            win.bwClientPrecache.store3d();
        }
        try {
            const s = Cache() ? Cache().state() : null;
            const row = typeof sha === 'string' ? await rowOf(s, lower(wallId)) : null;
            if (!row || (row.docs || []).some(d => d.sha === sha)) {
                return;
            }
            const held = (await s.env.ledger.assetsOf(s.active)).find(a => a.key === sha);
            const size = (held && held.bytes) || bytes || 0;
            const doc = { sha, kind: 'view3d', tier: 'view3d', bytes: size };
            await s.env.ledger.updateWall(s.active, lower(wallId), current => (current
                ? Object.assign(current, { docs: [doc].concat((current.docs || []).filter(d => d.sha !== sha)).slice(0, MAX_DOCS) })
                : undefined));
        } catch (e) {
            /* the document is then an orphan, evicted after its grace */
        }
    }

    return { NOTICE_KIND, MAX_DOCS, gate, accept, status, save, unsave, held, noteDoc };
})(window);
