/*
 * bwClientHandback — the offline client's way back to the Server pages (Phase 3 plan §1, PR 3.14).
 *
 * THE PROBE. The client asks GET /alive on start, on `online`, when the tab comes back into view, and every PROBE_MS
 * while visible. A 200 means online; anything else, or no answer within ALIVE_MS, offline. `offline` from the browser
 * is believed at once. A change is told through window.bwStatus when a page has it ("Switched to online/offline mode");
 * the client's own pill (#bw-client-pill, ClientLayout) always shows the lasting state:
 *   offline  "Offline · read-only · saved {time}", the newest time a wall of this device was saved (the ledger's
 *            `fetchedAt`, client-store-rows.js `savedAt`), or without the time when none is known;
 *   online   "Back online · open live", a button that opens the Server page of this path.
 *
 * THE NEXT TAP. ClientLayout asks take(target) on every client navigation (a link, a programmatic one, back/forward).
 * Online, it answers true and makes a full navigation to the Server twin of the target instead: the client never hands
 * back on its own, only on a tap. Offline, or with the state unknown, the client navigates as usual (and probes). A
 * Server page that then fails is handed to the client again by the worker on a network error or a 502/503/504, never by
 * its 4 s timer (a navigation from a /v/ page waits 20 s instead), so a slow server is waited for and nothing loops.
 * sessionStorage `bw-handback` tells the Server page it was opened from here (client-continue.js shows "online").
 *
 * THE SCHEMA. /alive's `clientSchemaMin` above this shell's `bw-client-schema` means this client may misread the
 * server's documents. The client asks the worker to precache the deploy's version (`client-precache`) and, once a
 * DIFFERENT complete version is there, reloads into it when idle (visible, nobody typing). sessionStorage
 * `bw-client-schema-reload` remembers the floor it reloaded for: never twice for the same floor, so a deploy whose own
 * client is still too old (or a fill that keeps the old version) costs one reload at most, never a loop.
 * Nothing here throws into the page.
 */
window.bwClientHandback = (function (win, doc) {
    'use strict';

    const PROTO = 1;
    const BASE = '/v/';
    const ALIVE_MS = 3000;
    const PROBE_MS = 15000;
    const PRECACHE_MS = 120000;
    const HANDBACK_KEY = 'bw-handback';
    const SCHEMA_KEY = 'bw-client-schema-reload';
    const STAY_KEY = 'bw-stay-offline';
    const STAY_MS = 120000;
    const PRECACHE_MARKER_KEY = 'bw-client-precache';
    const PILL_ID = 'bw-client-pill';

    const state = { started: false, online: null, savedAt: null, drawn: null, slot: null, timer: null, probing: null, schema: null, pending: null };

    const meta = name => {
        const el = doc.querySelector(`meta[name="${name}"]`);
        return el ? el.getAttribute('content') : null;
    };
    const status = kind => {
        try {
            if (win.bwStatus && typeof win.bwStatus.show === 'function') {
                win.bwStatus.show(kind);
            }
        } catch (e) {
            // A bubble is a courtesy; the pill says the same.
        }
    };
    const session = {
        get: key => { try { return win.sessionStorage.getItem(key); } catch (e) { return undefined; } },
        set: (key, value) => { try { win.sessionStorage.setItem(key, value); return true; } catch (e) { return false; } }
    };

    /** The Server twin of a client URL (same origin, under /v/), query and fragment kept; null for anything else. */
    function serverUrl(href) {
        let url;
        try {
            url = new URL(href, win.location.href);
        } catch (e) {
            return null;
        }
        const path = url.pathname;
        if (url.origin !== win.location.origin || !(path.toLowerCase() + '/').startsWith(BASE)) {
            return null;
        }
        // Never protocol-relative: `/v//evil.example` must stay on this origin.
        return '/' + path.slice(BASE.length).replace(/^[/\\]+/, '') + url.search + url.hash;
    }

    /** "14:32" today, "22 Oct" on an earlier day; null without a time. */
    function savedLabel(at, now) {
        if (typeof at !== 'number' || !Number.isFinite(at) || at <= 0) {
            return null;
        }
        const when = new Date(at);
        const today = new Date(now === undefined ? Date.now() : now);
        return when.toDateString() === today.toDateString()
            ? when.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
            : when.toLocaleDateString([], { day: 'numeric', month: 'short' });
    }

    function pillLabel() {
        if (state.online) {
            return 'Back online · open live';
        }
        const saved = savedLabel(state.savedAt);
        return saved ? `Offline · read-only · saved ${saved}` : 'Offline · read-only';
    }

    function render() {
        const slot = doc.getElementById(PILL_ID);
        if (!slot) {
            return;
        }
        const online = state.online === true;
        // Redrawn only when the pill changes: a rebuild every probe would drop the focus on the button.
        const drawn = online + '|' + pillLabel();
        if (state.drawn === drawn && state.slot === slot) {
            return;
        }
        state.drawn = drawn;
        state.slot = slot;
        const pill = doc.createElement(online ? 'button' : 'span');
        pill.className = 'bw-client-pill ' + (online ? 'bw-client-pill-info' : 'bw-client-pill-warn');
        pill.setAttribute('data-bw-online', online ? 'true' : 'false');
        if (online) {
            pill.type = 'button';
            pill.addEventListener('click', () => {
                session.set(STAY_KEY, '0');
                take(win.location.href);
            });
        }
        const dot = doc.createElement('span');
        dot.className = 'bw-client-pill-dot';
        const label = doc.createElement('span');
        label.className = 'bw-client-pill-label';
        label.textContent = pillLabel();
        pill.append(dot, label);
        slot.replaceChildren(pill);
    }

    function setOnline(online) {
        const was = state.online;
        state.online = online;
        if (was !== online && (was !== null || !online)) {
            status(online ? 'online' : 'offline');
        }
        render();
    }

    async function readSaved() {
        try {
            const snap = win.bwClientStore ? await win.bwClientStore.snapshot() : null;
            const times = (snap && Array.isArray(snap.walls) ? snap.walls : []).map(w => w.savedAt).filter(t => typeof t === 'number');
            state.savedAt = times.length > 0 ? Math.max(...times) : null;
        } catch (e) {
            state.savedAt = null;
        }
        render();
    }

    async function alive() {
        const abort = typeof win.AbortController === 'function' ? new win.AbortController() : null;
        const timer = win.setTimeout(() => abort && abort.abort(), ALIVE_MS);
        try {
            const response = await win.fetch('/alive', { cache: 'no-store', credentials: 'omit', signal: abort ? abort.signal : undefined });
            if (!response.ok) {
                return null;
            }
            try {
                return await response.json();
            } catch (e) {
                return {};
            }
        } catch (e) {
            return null;
        } finally {
            win.clearTimeout(timer);
        }
    }

    /** One probe at a time; resolves the online state it found. */
    function probe() {
        if (!state.probing) {
            state.probing = (async () => {
                const body = win.navigator.onLine === false ? null : await alive();
                // A deploy's /alive (maintenance) answers 200 for the seconds before the restart: not a server to hand back to.
                setOnline(body !== null && body.maintenance !== true);
                if (body !== null) {
                    await checkSchema(body.clientSchemaMin);
                }
                return state.online;
            })().catch(() => state.online).finally(() => { state.probing = null; });
        }
        return state.probing;
    }

    function schedule() {
        win.clearTimeout(state.timer);
        state.timer = win.setTimeout(() => {
            if (doc.visibilityState !== 'hidden') {
                probe();
            }
            schedule();
        }, PROBE_MS);
    }

    // "Continue offline" (client-continue.js) was chosen a moment ago: the server answers /alive but its circuit may not
    // (a proxy that blocks WebSockets), so a tap stays in the client until the pill itself is tapped or STAY_MS pass.
    function stayingOffline() {
        const at = Number(session.get(STAY_KEY));
        return at > 0 && Date.now() >= at && Date.now() - at < STAY_MS;
    }

    /** True when the client hands this navigation back: it navigates to the Server page itself. */
    function take(target) {
        const url = serverUrl(target);
        if (!url || state.online !== true || win.navigator.onLine === false || stayingOffline()) {
            if (state.online !== true) {
                probe();
            }
            return false;
        }
        session.set(HANDBACK_KEY, String(Date.now()));
        win.location.assign(url);
        return true;
    }

    // A focused text field with something typed in it, or a hidden tab: no reload now.
    function busy() {
        const el = doc.activeElement;
        const tag = el ? String(el.tagName || '').toLowerCase() : '';
        const typing = (tag === 'input' || tag === 'textarea') ? !!el.value : !!(el && el.isContentEditable && (el.textContent || '').trim());
        return typing || doc.visibilityState === 'hidden';
    }

    function askWorker(message, ms) {
        const sw = win.navigator.serviceWorker;
        const controller = sw && sw.controller;
        if (!controller || typeof win.MessageChannel !== 'function') {
            return Promise.resolve(null);
        }
        return new Promise(resolve => {
            const channel = new win.MessageChannel();
            const timer = win.setTimeout(() => resolve(null), ms);
            channel.port1.onmessage = event => {
                win.clearTimeout(timer);
                resolve(event.data || null);
            };
            try {
                controller.postMessage(Object.assign({ proto: PROTO }, message), [channel.port2]);
            } catch (e) {
                win.clearTimeout(timer);
                resolve(null);
            }
        });
    }

    // The 3D code goes along as the Server page's last fill had it (its marker); else 'opened', which the worker decides by
    // whether a 3D view was ever opened on this device. The new version replaces the old, so never fewer files than before.
    function group3d() {
        try {
            const marker = JSON.parse(win.localStorage.getItem(PRECACHE_MARKER_KEY) || 'null');
            return marker && marker.with3d === true ? 'precache' : 'opened';
        } catch (e) {
            return 'opened';
        }
    }

    /** Resolves 'current', 'held' (already reloaded for this floor), 'stale' (no newer version), 'waiting' or 'reloading'. */
    function checkSchema(min) {
        const own = Number(meta('bw-client-schema'));
        if (!Number.isInteger(min) || !Number.isInteger(own) || min <= own) {
            return Promise.resolve('current');
        }
        if (!state.schema) {
            state.schema = (async () => {
                const done = session.get(SCHEMA_KEY);
                if (done === undefined || done === String(min)) {
                    return 'held';
                }
                const reply = await askWorker({ type: 'client-precache', group3d: group3d() }, PRECACHE_MS);
                const version = meta('bw-client-version');
                if (!reply || reply.complete !== true || !reply.version || reply.version === version) {
                    // A later probe may try again (a busy worker, a timed-out fill) or see a higher floor.
                    state.schema = null;
                    return 'stale';
                }
                state.pending = String(min);
                return reloadWhenIdle();
            })();
        }
        return state.schema;
    }

    function reloadWhenIdle() {
        if (state.pending === null) {
            return 'current';
        }
        if (busy()) {
            return 'waiting';
        }
        if (!session.set(SCHEMA_KEY, state.pending)) {
            return 'held';
        }
        state.pending = null;
        win.location.reload();
        return 'reloading';
    }

    function start() {
        if (state.started) {
            return state.probing || Promise.resolve(state.online);
        }
        state.started = true;
        render();
        readSaved();
        win.addEventListener('online', () => probe());
        win.addEventListener('offline', () => setOnline(false));
        doc.addEventListener('visibilitychange', () => {
            if (doc.visibilityState === 'visible') {
                probe();
                readSaved();
                reloadWhenIdle();
            }
        });
        doc.addEventListener('focusout', () => win.setTimeout(reloadWhenIdle, 0));
        schedule();
        return probe();
    }

    return { start, probe, take, serverUrl, savedLabel, checkSchema, get online() { return state.online; } };
})(window, document);
