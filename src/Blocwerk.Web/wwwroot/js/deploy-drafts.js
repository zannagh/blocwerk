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
 * when the page is hidden, frozen or unloaded. Taking such a copy back says so ("restored").
 *
 * Exposes window.bwDeployDrafts (called from DeployDraftInterop.cs):
 *   keep(key, payloadJson, notice?, href?)  notice(text, href?)  take(key) -> payloadJson|null  drop(key)
 *   show(text, href?)  (a notice right now, for state the server already saved)
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

    // storage: a Storage; now(): ms; show(text, href): displays a notice now.
    function createStore(storage, now, show) {
        function notice(text, href) {
            if (!text) {
                return;
            }

            const list = read(storage, NOTICES) || [];
            list.push({ text: String(text), href: href || null, at: now() });
            write(storage, NOTICES, list);
        }

        function keep(key, payload, text, href) {
            write(storage, PREFIX + key, { payload: String(payload), reason: 'deploy', at: now() });
            notice(text, href);
        }

        function keepHidden(key, payload) {
            const existing = read(storage, PREFIX + key);
            const reason = existing && existing.reason === 'deploy' ? 'deploy' : 'hidden';
            write(storage, PREFIX + key, { payload: String(payload), reason: reason, at: now() });
        }

        function take(key) {
            const entry = read(storage, PREFIX + key);
            remove(storage, PREFIX + key);
            if (!entry || now() - entry.at > MAX_AGE_MS) {
                return null;
            }

            if (entry.reason === 'hidden') {
                show(RESTORED, null);
            }

            return entry.payload;
        }

        function drop(key) {
            remove(storage, PREFIX + key);
        }

        // The queued notices, oldest first, removed from storage.
        function takeNotices() {
            const list = read(storage, NOTICES) || [];
            remove(storage, NOTICES);
            return list.filter(function (n) { return now() - n.at <= MAX_AGE_MS; });
        }

        return { keep: keep, keepHidden: keepHidden, take: take, drop: drop, notice: notice, takeNotices: takeNotices };
    }

    function banner(doc, text, href) {
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

        const close = doc.createElement('button');
        close.type = 'button';
        close.textContent = 'OK';
        close.setAttribute('aria-label', 'Dismiss');
        close.style.cssText = 'margin-left:auto;font:inherit;color:inherit;background:transparent;border:1px solid rgba(255,255,255,.5);border-radius:6px;padding:2px 10px;cursor:pointer;';
        close.addEventListener('click', function () { el.remove(); });
        el.appendChild(message);
        el.appendChild(close);
        doc.body.appendChild(el);
    }

    function install(win) {
        const doc = win.document;
        let storage = null;
        try {
            storage = win.localStorage;
        } catch (e) { /* blocked: every call below degrades to a no-op */ }

        const show = function (text, href) { banner(doc, text, href); };
        const store = createStore(storage || { getItem: function () { return null; }, setItem: function () { throw new Error('no storage'); }, removeItem: function () { } },
            function () { return Date.now(); }, show);

        const captureHidden = function () {
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
            store.takeNotices().forEach(function (n) { show(n.text, n.href); });
        };
        if (doc.readyState === 'loading') {
            doc.addEventListener('DOMContentLoaded', showQueued);
        } else {
            showQueued();
        }

        return { keep: store.keep, notice: store.notice, take: store.take, drop: store.drop, show: show };
    }

    return { createStore: createStore, install: install, MAX_AGE_MS: MAX_AGE_MS, RESTORED: RESTORED };
});
