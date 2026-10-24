/*
 * What the status bubbles (status-bubble.js) are told, from the offline queue (offline-queue.js) and the circuit
 * (blazor-boot.js). Each is a change, told once:
 *
 *   offline    the device lost the network, or the circuit stayed down for OFFLINE_GRACE_MS (a blip that heals on
 *              its own says nothing). Not while a deploy notice is up: the pill says "Blocwerk is updating…" then.
 *   online     back after an `offline` was told, and only then.
 *   uploading  the queue starts sending a backlog: actions saved while offline, or left over from an earlier page.
 *              An action sent straight away while online is not a backlog and says nothing.
 *   uploaded   that backlog is all sent.
 *   conflict   the queue gave up on an action for good (offline-queue.js `rejected`). It stays until the user
 *              dismisses it in the pill's panel, which lists what failed.
 *
 * `offline-ready` comes from asset-report.js, which knows when the walls are on the device.
 */
(function (win) {
    'use strict';

    const OFFLINE_GRACE_MS = 4000;

    const Status = () => win.bwStatus;
    const queue = () => win.blocwerkOfflineQueue;
    const connection = () => win.blocwerkConnection;

    let offlineTold = false;
    let graceTimer = null;
    let firstFlush = true;
    let backlog = 0;
    let uploadingTold = false;
    let finished = 0;
    let sent = 0;
    let wasFlushing = false;
    let rejectedSeen = 0;

    function show(kind, opts) {
        if (Status()) {
            Status().show(kind, opts);
        }
    }

    function updating() {
        const m = win.bwMaintenance;
        return !!(m && typeof m.active === 'function' && m.active());
    }

    function isDown(q, c) {
        return !q.online || c === 'reconnecting' || c === 'failed';
    }

    function snapshot() {
        const q = queue() ? queue().state() : { online: true, flushing: false, count: 0, rejected: [] };
        const c = connection() ? connection().status() : 'connected';
        return { q, c };
    }

    function tellOffline() {
        graceTimer = null;
        const { q, c } = snapshot();
        if (!offlineTold && isDown(q, c) && !updating()) {
            offlineTold = true;
            show('offline');
        }
    }

    function trackConnection(q, c) {
        if (isDown(q, c)) {
            if (!offlineTold && !graceTimer) {
                // The device itself says it has no network: no reason to wait.
                graceTimer = win.setTimeout(tellOffline, q.online ? OFFLINE_GRACE_MS : 0);
            }
            return;
        }
        win.clearTimeout(graceTimer);
        graceTimer = null;
        if (offlineTold) {
            offlineTold = false;
            show('online');
        }
    }

    function trackUploads(q, c) {
        // A backlog: what is queued while down, and what the page found queued when it loaded (before its first flush).
        if (q.count > 0 && (isDown(q, c) || firstFlush)) {
            if (backlog === 0) {
                sent = 0;
            }
            backlog = Math.max(backlog, q.count);
        }
        if (q.flushing && !wasFlushing && backlog > 0 && !uploadingTold && !isDown(q, c)) {
            uploadingTold = true;
            show('uploading', { count: q.count });
        }
        if (!q.flushing && wasFlushing) {
            firstFlush = false;
            if (q.count === 0) {
                // Told by the queue's bw:offline-flushed right after this, which says whether anything was sent.
                finished = backlog;
                backlog = 0;
                uploadingTold = false;
            }
        }
        wasFlushing = q.flushing;
    }

    // Only a flush that sent something announces itself, so the count is what was sent, not what was queued: an action
    // the server refused is a conflict, not an upload. A flush that ran while the circuit was still reconnecting sends
    // the backlog without an `uploading` first; it is told as uploaded all the same.
    function onFlushed(event) {
        const summary = event && event.detail;
        sent += summary && summary.sent > 0 ? summary.sent : 0;
        if (finished > 0 && summary && summary.remaining === 0) {
            show('uploaded', { count: sent });
        }
        finished = 0;
    }

    function trackConflicts(q) {
        const n = q.rejected ? q.rejected.length : 0;
        if (n > rejectedSeen) {
            show('conflict', { count: n });
        } else if (n === 0 && Status()) {
            Status().hide('conflict');
        }
        rejectedSeen = n;
    }

    function onChange() {
        const { q, c } = snapshot();
        trackConnection(q, c);
        trackUploads(q, c);
        trackConflicts(q);
    }

    if (queue()) {
        queue().subscribe(onChange);
    }
    if (connection()) {
        connection().subscribe(onChange);
    }
    win.document.addEventListener('bw:offline-flushed', onFlushed);
})(window);
