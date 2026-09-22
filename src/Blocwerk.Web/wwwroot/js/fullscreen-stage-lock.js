/*
 * bwStageLock — the real-fullscreen + screen-orientation half of the full-screen takeover.
 *
 * Separate from fullscreen-stage.js because it is the only part that runs WITHOUT an overlay: real
 * fullscreen and screen.orientation.lock('landscape') both need transient user activation, which a
 * Blazor Server round-trip destroys, so they are requested from a capture-phase click in the same
 * task as the tap — long before the overlay exists.
 *
 * Which is exactly why the two must be strictly paired. The request arms a watchdog; if no overlay
 * has attached by the time it fires (script missing, circuit dead, stale ElementReference) the lock
 * and the fullscreen are released again. Without that the user is stranded locked in landscape
 * fullscreen on an ordinary page, with nothing on screen to close.
 *
 * Talks to bwStage only through window, lazily, inside functions — so neither script's position in
 * the shell can break the other.
 */
window.bwStageLock = (function () {
    'use strict';

    // How long a request may stay armed without an overlay behind it. Generous, because it covers a
    // Blazor Server round-trip on a slow connection.
    const ARM_TIMEOUT_MS = 6000;

    let armTimer = 0;

    function stageOpen() {
        return !!(window.bwStage && window.bwStage.isOpen && window.bwStage.isOpen());
    }

    function clearWatchdog() {
        if (armTimer) {
            clearTimeout(armTimer);
            armTimer = 0;
        }
    }

    /** Releases the lock unless an overlay actually came up in the meantime. */
    function armWatchdog() {
        clearWatchdog();
        armTimer = setTimeout(function () {
            armTimer = 0;
            if (!stageOpen()) {
                release();
            }
        }, ARM_TIMEOUT_MS);
    }

    /** Asks for landscape. Both halves can reject (iOS has neither); the CSS rotation then carries it. */
    function request() {
        const root = document.documentElement;
        const requestFs = root.requestFullscreen || root.webkitRequestFullscreen;
        if (!requestFs) {
            return;
        }

        let promise;
        try {
            promise = requestFs.call(root, { navigationUI: 'hide' });
        } catch (e) {
            return;
        }

        if (!promise || !promise.then) {
            return;
        }

        // Armed BEFORE the lock, so a fullscreen that succeeds and an overlay that never arrives
        // still unwinds.
        armWatchdog();
        promise.then(function () {
            if (screen.orientation && screen.orientation.lock) {
                screen.orientation.lock('landscape').catch(function () { /* not lockable here */ });
            }
        }).catch(function () {
            // Fullscreen refused: nothing was taken, so there is nothing to unwind.
            clearWatchdog();
        });
    }

    function release() {
        clearWatchdog();
        try {
            if (screen.orientation && screen.orientation.unlock) {
                screen.orientation.unlock();
            }
        } catch (e) { /* never lockable to begin with */ }

        if (document.fullscreenElement && document.exitFullscreen) {
            document.exitFullscreen().catch(function () { /* already out */ });
        }
    }

    document.addEventListener('click', function (e) {
        const btn = e.target && e.target.closest && e.target.closest('[data-bw-stage-open]');
        if (btn) {
            request();
        }
    }, true);

    return {
        request: request,
        release: release,
        /** The overlay is up: the request it was opened with is accounted for. */
        settled: clearWatchdog,
    };
})();
