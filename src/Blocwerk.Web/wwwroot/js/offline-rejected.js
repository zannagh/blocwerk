/*
 * The offline queue's rejected actions (offline-queue.js `reject`), kept until their owner dismisses them.
 *
 * They used to live in memory only, so a conflict nobody had looked at yet was gone on the next full load (the
 * post-deploy reload included). Kept in localStorage, not in the queue's IndexedDB store: that store holds actions to
 * send, and every reader of it (flush, pending) would have to learn to skip a second kind of row.
 *
 * Each item carries its owner: the identity of the page that queued it (entry.queuedForUserId, else the user signed in
 * when it failed) and the kiosk it was on. Only the owner's items are ever returned, so a kiosk's next member, or the
 * next account on a shared browser, never sees them. Dismiss clears the owner's items; sign-out and every other
 * identity change (bw:identity-changed) clear them all. Every storage access is guarded: without storage, the items
 * live as long as the page, as before.
 *
 * window.blocwerkOfflineRejected:
 *   add(entry, message)  keeps a rejected entry for its owner
 *   current()            the current identity's items, oldest first: [{ kind, message, at }]
 *   clearCurrent()       the current identity's items go (Dismiss)
 *   clearAll()           everything goes (an identity change)
 */
window.blocwerkOfflineRejected = (function (win) {
    'use strict';

    const KEY = 'blocwerk-offline-rejected';
    const MAX = 20;
    let memory = [];

    function identity() {
        const data = win.document.body && win.document.body.dataset ? win.document.body.dataset : {};
        return { user: data.bwUser || '', kiosk: data.bwKiosk || '' };
    }

    function read() {
        try {
            const items = JSON.parse(win.localStorage.getItem(KEY) || '[]');
            return Array.isArray(items) ? items : [];
        } catch (e) {
            return memory;
        }
    }

    function write(items) {
        memory = items;
        try {
            if (items.length === 0) {
                win.localStorage.removeItem(KEY);
            } else {
                win.localStorage.setItem(KEY, JSON.stringify(items));
            }
        } catch (e) {
            /* no storage: kept in memory for this page */
        }
    }

    const owns = (item, who) => item.user === who.user && item.kiosk === who.kiosk;

    function add(entry, message) {
        const who = identity();
        const item = {
            kind: entry.kind, message: message, at: Date.now(),
            user: entry.queuedForUserId || who.user, kiosk: who.kiosk
        };
        write(read().concat([item]).slice(-MAX));
    }

    function current() {
        const who = identity();
        return read().filter(item => owns(item, who)).map(item => ({ kind: item.kind, message: item.message, at: item.at }));
    }

    function clearCurrent() {
        const who = identity();
        write(read().filter(item => !owns(item, who)));
    }

    function clearAll() {
        write([]);
    }

    win.addEventListener('bw:identity-changed', clearAll);

    return { add, current, clearCurrent, clearAll };
})(window);
