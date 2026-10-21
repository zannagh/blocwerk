/*
 * deploy-drafts.js — editor state that survives a deploy's reload, and the notice that says what happened to it.
 *
 * Before autodeploy recreates the container it calls POST /api/v1/maintenance/flush-edits, and every open editor
 * persists its unsaved state. Where that can be done safely on the server it is (a draft boulder); where it cannot (a
 * half-done revise of a live boulder, a rename, a new-wall form) the editor's state is kept HERE, in localStorage, and
 * the editor takes it back when it opens after the reload. Either way a notice is queued and shown on the next page
 * load. Nothing is discarded silently.
 *
 * A sleeping laptop cannot answer a flush, so editors that keep state on the device also render it into a hidden
 * element (data-bw-keep-key / data-bw-keep-payload) while it is unsaved, and this file copies those into storage
 * when the page is hidden, frozen or unloaded. Taking such a copy back says so ("restored"). Every notice about a
 * kept copy has a Discard button: it forgets the copy, and reloads when the editor has already taken it back.
 *
 * Exposes window.bwDeployDrafts (called from DeployDraftInterop.cs):
 *   keep(key, payloadJson, notice?, href?)  notice(text, href?)  take(key) -> payloadJson|null  drop(key)
 *   show(text, href?)  (a notice right now, for state the server already saved)
 *   clearAll()  forgetVisible()   (identity switch; the offline queue took the form)
 *
 * Kept keys carry the identity they belong to ("<identity>|<editor>", see KeptCopyKey.cs); a page only writes and
 * takes back keys of its own identity, and a page load under a different identity than the last one clears them all.
 *
 * The store (createStore) is pure over a Storage-like object, so node tests it (test/js/deploy-drafts.test.mjs).
 */
(function (root, factory) {
    'use strict';
    const api = factory();
    if (typeof module === 'object' && module.exports) {
        module.exports = api;
    } else {
        root.bwDeployDrafts = api.install(root);
    }
})(typeof self !== 'undefined' ? self : this, function () {
    'use strict';

    const PREFIX = 'bw-kept:';
    const NOTICES = 'bw-deploy-notices';
    const IDENTITY = 'bw-kept-identity';
    const MAX_AGE_MS = 7 * 24 * 3600 * 1000;
    const RESTORED = 'Your unsaved changes from before were restored.';

    function read(storage, key) {
        try {
            const raw = storage.getItem(key);
            return raw ? JSON.parse(raw) : null;
        } catch (e) {
            return null;
        }
    }

    function write(storage, key, value) {
        try {
            storage.setItem(key, JSON.stringify(value));
            return true;
        } catch (e) {
            return false;
        }
    }

    function remove(storage, key) {
        try {
            storage.removeItem(key);
        } catch (e) { /* storage blocked: nothing was kept anyway */ }
    }

    function keysOf(storage) {
        const out = [];
        try {
            for (let i = 0; i < storage.length; i++) {
                out.push(storage.key(i));
            }
        } catch (e) { /* storage blocked */ }

        return out;
    }

    // storage: a Storage; now(): ms; show(text, href, key): displays a notice now (key: the copy it can discard);
    // identity(): who this page is for ("<user>", "k<kiosk key>-<user>" or "...anon"). Every kept key starts with
    // the identity it was kept for ("<identity>|<editor>"), and a copy is only ever written or taken back under the
    // identity of the page, so it can never restore into another person's form.
    function createStore(storage, now, show, identity) {
        const restored = new Set();
        const who = identity || function () { return 'anon'; };
        const owned = function (key) { return String(key).split('|')[0] === who(); };

        function notice(text, href, key) {
            if (!text) {
                return;
            }

            // One notice per editor (or per text) however often the flush runs.
            const list = (read(storage, NOTICES) || []).filter(function (n) {
                return key ? n.key !== key : !(n.text === String(text) && n.href === (href || null));
            });
            list.push({ text: String(text), href: href || null, key: key || null, at: now() });
            write(storage, NOTICES, list);
        }

        function keep(key, payload, text, href) {
            if (!owned(key)) {
                return false;
            }

            write(storage, PREFIX + key, { payload: String(payload), reason: 'deploy', at: now() });
            notice(text, href, key);
            return true;
        }

        function keepHidden(key, payload) {
            if (!owned(key)) {
                return;
            }

            const existing = read(storage, PREFIX + key);
            const reason = existing && existing.reason === 'deploy' ? 'deploy' : 'hidden';
            write(storage, PREFIX + key, { payload: String(payload), reason: reason, at: now() });
        }

        // { payload, reason: 'deploy' | 'hidden' } or null. 'deploy' means a pre-deploy flush kept it.
        function take(key) {
            if (!owned(key)) {
                return null;
            }

            const entry = read(storage, PREFIX + key);
            remove(storage, PREFIX + key);
            if (!entry || now() - entry.at > MAX_AGE_MS) {
                return null;
            }

            restored.add(key);
            if (entry.reason === 'hidden') {
                show(RESTORED, null, key);
            }

            return { payload: entry.payload, reason: entry.reason };
        }

        function drop(key) {
            remove(storage, PREFIX + key);
        }

        // The queued notices for this identity, oldest first, removed from storage.
        function takeNotices() {
            const list = read(storage, NOTICES) || [];
            remove(storage, NOTICES);
            return list.filter(function (n) { return now() - n.at <= MAX_AGE_MS && (!n.key || owned(n.key)); });
        }

        // The user dismissed a kept copy: forget it, and say whether the open editor already took it back (then only a
        // reload gets the editor back to what the server has).
        function discard(key) {
            remove(storage, PREFIX + key);
            return restored.has(key);
        }

        // Sign-out, kiosk act-as ending, an account switch: nothing kept may outlive the person it was kept for.
        function clearAll() {
            keysOf(storage).forEach(function (k) {
                if (k && k.indexOf(PREFIX) === 0) {
                    remove(storage, k);
                }
            });
            remove(storage, NOTICES);
        }

        // Called on every page load: a different identity than last time means the switch happened elsewhere.
        function checkIdentity() {
            let last = null;
            try {
                last = storage.getItem(IDENTITY);
            } catch (e) { /* storage blocked */ }

            if (last !== null && last !== who()) {
                clearAll();
            }

            try {
                storage.setItem(IDENTITY, who());
            } catch (e) { /* storage blocked */ }
        }

        return {
            keep: keep, keepHidden: keepHidden, take: take, drop: drop, notice: notice, takeNotices: takeNotices,
            discard: discard, clearAll: clearAll, checkIdentity: checkIdentity
        };
    }

    function button(doc, label, onClick) {
        const b = doc.createElement('button');
        b.type = 'button';
        b.textContent = label;
        b.style.cssText = 'font:inherit;color:inherit;background:transparent;border:1px solid rgba(255,255,255,.5);border-radius:6px;padding:2px 10px;cursor:pointer;';
        b.addEventListener('click', onClick);
        return b;
    }

    function banner(doc, text, href, onDiscard) {
        const el = doc.createElement('div');
        el.className = 'bw-deploy-notice';
        el.setAttribute('role', 'status');
        el.style.cssText = 'position:fixed;z-index:2100;left:50%;bottom:24px;transform:translateX(-50%);max-width:min(92vw,520px);'
            + 'display:flex;gap:12px;align-items:center;padding:10px 14px;border-radius:10px;'
            + 'font:500 14px/1.35 system-ui,sans-serif;color:#fff;background:rgba(20,20,28,0.92);box-shadow:0 4px 18px rgba(0,0,0,.3);';
        const message = doc.createElement(href ? 'a' : 'span');
        message.textContent = text;
        if (href) {
            message.href = href;
            message.style.color = 'inherit';
        }

        const close = button(doc, 'OK', function () { el.remove(); });
        close.style.marginLeft = 'auto';
        el.appendChild(message);
        el.appendChild(close);
        if (onDiscard) {
            el.appendChild(button(doc, 'Discard', function () { el.remove(); onDiscard(); }));
        }
        doc.body.appendChild(el);
    }

    function install(win) {
        const doc = win.document;
        let storage = null;
        try {
            storage = win.localStorage;
        } catch (e) { /* blocked: every call below degrades to a no-op */ }

        let store = null;
        let discarding = false;
        const show = function (text, href, key) {
            banner(doc, text, href, key ? function () {
                if (store.discard(key)) {
                    // The reload's pagehide must not copy the discarded state straight back.
                    discarding = true;
                    win.location.reload();
                }
            } : null);
        };
        // The identity the server rendered onto <body> (BlocwerkApp.razor), in the same shape KeptCopyKey.cs builds.
        const identity = function () {
            const b = doc.body ? doc.body.dataset : {};
            return (b.bwKiosk ? 'k' + b.bwKiosk + '-' : '') + (b.bwUser || 'anon');
        };
        store = createStore(storage || { length: 0, getItem: function () { return null; }, setItem: function () { throw new Error('no storage'); }, removeItem: function () { } },
            function () { return Date.now(); }, show, identity);
        store.checkIdentity();

        // The shared "the person at this browser changed" hook (sign-out, kiosk act-as end, account switch).
        win.addEventListener('bw:identity-changed', function () { store.clearAll(); });

        // An editor's form left the page another way (the offline queue took it): forget its copies, and do not let
        // this page's unload copy it straight back.
        const forgetVisible = function () {
            discarding = true;
            doc.querySelectorAll('[data-bw-keep-key]').forEach(function (el) { store.drop(el.dataset.bwKeepKey); });
        };

        const captureHidden = function () {
            if (discarding) {
                return;
            }

            doc.querySelectorAll('[data-bw-keep-key]').forEach(function (el) {
                if (el.dataset.bwKeepPayload) {
                    store.keepHidden(el.dataset.bwKeepKey, el.dataset.bwKeepPayload);
                }
            });
        };
        doc.addEventListener('visibilitychange', function () {
            if (doc.visibilityState === 'hidden') {
                captureHidden();
            }
        });
        win.addEventListener('pagehide', captureHidden);
        doc.addEventListener('freeze', captureHidden);

        const showQueued = function () {
            store.takeNotices().forEach(function (n) { show(n.text, n.href, n.key); });
        };
        if (doc.readyState === 'loading') {
            doc.addEventListener('DOMContentLoaded', showQueued);
        } else {
            showQueued();
        }

        return {
            keep: store.keep, notice: store.notice, take: store.take, drop: store.drop, clearAll: store.clearAll,
            forgetVisible: forgetVisible, show: function (text, href) { show(text, href, null); }
        };
    }

    return { createStore: createStore, install: install, MAX_AGE_MS: MAX_AGE_MS, RESTORED: RESTORED };
});
