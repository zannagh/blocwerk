/*
 * bwMaintenance — the browser half of the "server is updating" notice.
 *
 * The deploy hook posts an announcement seconds before the container is recreated. The server
 * pushes that over the circuit (Components/Shared/MaintenanceWatcher.razor calls begin() here) and
 * also reports it on GET /alive, so a client that connects mid-window, or misses the push, still
 * learns about it.
 *
 * The whole point is what happens NEXT: once the announcement lands the circuit is about to die,
 * so nothing server-rendered can be trusted to update. This file therefore owns the notice and the
 * recovery entirely on the client:
 *
 *   1. On load, remember the serving process's `instanceId` from /alive. That is the baseline.
 *   2. While watching, poll /alive. The server WILL be unreachable for part of that.
 *   3. Reload ONLY when a SUCCESSFUL response carries a DIFFERENT instanceId. A reachable socket,
 *      a 5xx, or a failed fetch proves nothing — and reloading on any of those is precisely how a
 *      user ends up staring at offline.html, because the service worker answers a navigation it
 *      cannot fetch with the offline page.
 *   4. Underneath all of that, a slow heartbeat runs for the life of the page in EVERY phase. It
 *      applies rule 3 on its own, so no combination of "gave up", "never told" and "was asleep"
 *      can leave a tablet parked on a dead circuit. Watching is an optimisation; the heartbeat is
 *      the guarantee.
 *
 * Every scheduled delay carries a small random spread, and the reload itself is spread further:
 * a gym's worth of tablets loaded the same page from the same container and would otherwise hit a
 * cold one in perfect lockstep.
 *
 * The notice itself is NOT rendered here: it is fed into the existing connection pill
 * (wwwroot/js/offline-status.js), which already renders outside the circuit, already survives in
 * both MainLayout and EmptyLayout, and already owns the one screen position a status message may
 * occupy. See summarise() there — maintenance is the top rung of its ladder.
 *
 * Exposes `window.bwMaintenance`:
 *   begin(announcement)  -> show the notice and start watching. announcement: { message } | null
 *   active()             -> bool, true while the notice should be on screen
 *   state()              -> { phase, message, reason } for the pill to render
 *   reloadNow()          -> take the offered reload immediately (pill button)
 *   stop()               -> drop the notice and stop polling
 *   refresh()            -> Promise, force one /alive read now (used by begin and on wake)
 */
(function () {
    'use strict';

    const ALIVE_URL = '/alive';

    // Fast enough that the gap between "new container serves" and "page reloads" is not noticed,
    // slow enough that a hundred kiosk tablets do not stampede a starting container.
    const POLL_MS = 2000;

    // While /alive is failing — which is the normal middle of a deploy — back off so a container
    // that takes a while to start is not fighting the poll for its own CPU.
    const POLL_MAX_MS = 8000;

    // Spread applied to EVERY scheduled delay. A gym full of tablets all loaded the same page from
    // the same container and would otherwise tick in lockstep: without this, a cold container's
    // first second is a wall of simultaneous /alive requests, and its first seconds after that are
    // a wall of simultaneous full page loads. See jittered() and doReload().
    const JITTER_MS = 700;

    // How far the reload itself is spread. Bigger than the poll jitter on purpose: a poll is one
    // small JSON response, a reload is a whole page plus its assets against a container that is
    // still warming up.
    const RELOAD_SPREAD_MS = 4000;

    // A cancelled or failed deploy must not leave a spinner on a kiosk forever. When nothing has
    // changed by here we drop the notice; the connection pill's ordinary offline/reconnecting
    // wording takes over, which is at that point the truthful one.
    //
    // This MUST outlast the server's own ceiling (MaintenanceAnnouncer.MaxTtl, 30 minutes) or the
    // client gives up while the server is still announcing — the banner vanishes and is replaced by
    // "Session ended" mid-deploy. Five minutes of headroom over that ceiling.
    const MAX_WATCH_MS = 35 * 60 * 1000;

    // In 'ready' the new build is already up and the ONLY thing being waited on is a human emptying
    // a text field. The server has nothing left to tell us, so polling it twice a second is pure
    // noise; check occasionally, and stop checking eventually.
    const READY_POLL_MS = 30 * 1000;
    const MAX_READY_MS = 60 * 60 * 1000;

    // The safety net, and the reason a watch can never be abandoned for good. This runs for the
    // whole life of the page — in 'idle' too — so a tab that gave up, was never told anything, or
    // was asleep through the entire window still notices that the process it loaded from is gone
    // and reloads itself. Deliberately slow: it is a background heartbeat, not a poll.
    const HEARTBEAT_MS = 60 * 1000;

    // Baseline retries when the very first /alive read fails (offline start, cold service worker).
    const BASELINE_RETRY_MS = 5000;

    // 'idle' | 'watching' | 'ready' — 'ready' means a new instance is up but the reload is being
    // held back because somebody is typing. See safeToReload().
    let phase = 'idle';

    let baselineId = null;
    let message = null;

    // Why we started watching: 'announced' (the server told us) or 'rejected' (the circuit's
    // server-side state vanished, which is what a container recreate looks like from here).
    let reason = null;

    let watchStartedAt = 0;
    let readyStartedAt = 0;
    let pollTimer = null;
    let pollDelay = POLL_MS;
    let baselineTimer = null;
    let heartbeatTimer = null;
    let reloaded = false;

    /**
     * A delay with a small random spread, so clients that started together do not stay together.
     * Never negative, and never so large that a 2s poll becomes a 4s one.
     */
    function jittered(ms) {
        return Math.max(0, Math.round(ms + (Math.random() * 2 - 1) * JITTER_MS));
    }

    /**
     * Repaint the pill. Deliberately swallowing: the renderer reaches into offline-status.js, which
     * reads blocwerkOfflineQueue.state() — and that THROWS where IndexedDB is unavailable (Safari
     * private browsing, a quota-exhausted profile). An exception escaping here used to travel up
     * the poll chain, leave pollTimer null and freeze the banner on screen permanently. Drawing the
     * notice is the least important thing this file does; the reload is the point, and a render
     * failure must never be able to stop it.
     */
    function repaint() {
        try {
            if (window.blocwerkStatus) {
                window.blocwerkStatus.render();
            }
        } catch (e) {
            // Nothing to do and nobody to tell: the pill simply does not update this tick.
        }
    }

    /**
     * One /alive read. Resolves with the parsed body, or null when the server could not be reached
     * or answered anything other than 200 — the caller must treat null as "no information", never
     * as "the server is gone" or "the server is back".
     */
    function readAlive() {
        return fetch(ALIVE_URL, {
            method: 'GET',
            cache: 'no-store',
            credentials: 'omit',
            headers: { 'Accept': 'application/json' }
        }).then(function (response) {
            if (!response.ok) {
                return null;
            }
            return response.json();
        }).then(function (body) {
            return body && typeof body.instanceId === 'string' && body.instanceId.length > 0
                ? body
                : null;
        }).catch(function () {
            return null;
        });
    }

    /**
     * Captures the baseline instance id, retrying lazily until it succeeds. Never blocks page
     * start: everything here is fire-and-forget, and until it lands no reload can be decided.
     */
    function captureBaseline() {
        if (baselineId !== null) {
            return;
        }

        readAlive().then(function (body) {
            if (!body) {
                retryBaseline();
                return;
            }

            baselineId = body.instanceId;

            // Loaded into an announcement that was already live — the push never reached this
            // client because it was not connected when it went out.
            if (body.maintenance && phase === 'idle') {
                begin({ message: body.message });
            }
        }).catch(function () {
            // readAlive() already swallows fetch failures, so reaching here means the HANDLER threw
            // — begin() -> repaint() -> a renderer that could not read its store. Without this the
            // rejection is unhandled AND the retry is never armed, so the baseline is never captured
            // and no reload can ever be decided for the life of the page.
            retryBaseline();
        });
    }

    function retryBaseline() {
        if (baselineId !== null || baselineTimer !== null) {
            return;
        }

        baselineTimer = setTimeout(function () {
            baselineTimer = null;
            captureBaseline();
        }, jittered(BASELINE_RETRY_MS));
    }

    /**
     * An automatic reload is a data-loss event for anybody mid-typing. The deploy hook already
     * waits for EditActivityRegistry to report idle, so a genuine mid-edit case is rare by construction;
     * this is the cheap second belt for the input that server-side gate cannot see — a half-typed
     * comment, a boulder name, a search box.
     */
    function safeToReload() {
        const el = document.activeElement;
        if (!el) {
            return true;
        }

        if (el.isContentEditable) {
            return (el.textContent || '').trim().length === 0;
        }

        const tag = (el.tagName || '').toLowerCase();
        if (tag === 'textarea' || tag === 'input' || tag === 'select') {
            return !el.value || String(el.value).length === 0;
        }

        return true;
    }

    function doReload() {
        if (reloaded) {
            return;
        }

        // Latched before the delay, so the heartbeat, the poll and the pill button cannot each
        // queue their own reload.
        reloaded = true;
        stopPolling();
        stopHeartbeat();

        // Every client watching this deploy learned the new instance id within the same tick, so
        // reloading immediately means a cold container's first job is serving the whole gym at
        // once. A few seconds of spread costs one user nothing and costs the container everything.
        setTimeout(function () {
            location.reload();
        }, Math.round(Math.random() * RELOAD_SPREAD_MS));
    }

    function stopPolling() {
        if (pollTimer !== null) {
            clearTimeout(pollTimer);
            pollTimer = null;
        }
    }

    function schedule(delay) {
        stopPolling();
        pollTimer = setTimeout(poll, jittered(delay));
    }

    /**
     * Acts on one SUCCESSFUL /alive body and reports what it decided: 'reloaded' when a reload was
     * taken or is now pending, 'ready' when a new instance is up but the reload is being held back,
     * 'same' when the process we loaded from is still the one answering.
     *
     * Shared by the fast watch poll and the slow background heartbeat so the two can never disagree
     * about the only event that matters.
     */
    function consider(body) {
        if (baselineId === null) {
            // First id we ever managed to read. It is a baseline, not a change: reloading here
            // would fire on nothing more than "the page could not reach the server at startup".
            baselineId = body.instanceId;
            return 'same';
        }

        if (body.instanceId === baselineId) {
            return 'same';
        }

        // The one and only reload condition: a process that is not the one we loaded from answered
        // a real request. The server is definitely serving, so the reload cannot land on offline.html.
        if (safeToReload()) {
            doReload();
            return 'reloaded';
        }

        if (phase !== 'ready') {
            phase = 'ready';
            readyStartedAt = Date.now();
            repaint();
        }

        return 'ready';
    }

    function poll() {
        pollTimer = null;

        if (phase === 'idle' || reloaded) {
            return;
        }

        if (phase === 'watching' && Date.now() - watchStartedAt > MAX_WATCH_MS) {
            // Nothing changed in the whole window the server could possibly have announced for
            // (MAX_WATCH_MS deliberately outlasts MaintenanceAnnouncer.MaxTtl). Whatever happened,
            // this is no longer an update in progress, and a stale banner is worse than none.
            //
            // Giving up here is NOT permanent any more, which was the bug: the heartbeat keeps
            // running through 'idle' and re-arms a watch — or takes the reload outright — the
            // moment the instance id actually changes. An unattended tablet cannot be stranded.
            stop();
            return;
        }

        if (phase === 'ready' && Date.now() - readyStartedAt > MAX_READY_MS) {
            // An hour of "Update ready" that nobody tapped, on a page whose text field was never
            // emptied. The person has gone; drop the pill and let the heartbeat take the reload.
            stop();
            return;
        }

        // 'ready' is waiting on a HUMAN, not on the server — the new build is already up and /alive
        // has nothing left to say. Checking twice a second for an hour is pure noise.
        var interval = phase === 'ready' ? READY_POLL_MS : POLL_MS;

        readAlive().then(function (body) {
            if (phase === 'idle' || reloaded) {
                return;
            }

            if (!body) {
                // No information. This is the expected state for most of a deploy: the old
                // container is gone and the new one is not listening yet. Back off and keep the
                // notice up — deliberately NOT a reload.
                if (phase === 'watching') {
                    pollDelay = Math.min(Math.round(pollDelay * 1.5), POLL_MAX_MS);
                    schedule(pollDelay);
                } else {
                    schedule(interval);
                }
                return;
            }

            pollDelay = POLL_MS;

            var outcome = consider(body);
            if (outcome === 'reloaded') {
                return;
            }

            if (phase === 'ready') {
                schedule(READY_POLL_MS);
                return;
            }

            if (!body.maintenance && phase === 'watching') {
                // Same process, and it no longer claims to be updating: the announcement expired
                // or the deploy was cancelled (and for a 'rejected' watch, the server never went
                // away at all — the circuit was dropped for some other reason). Hand the screen
                // back to the ordinary connection pill.
                stop();
                return;
            }

            if (body.maintenance && body.message !== message && phase === 'watching') {
                message = body.message || null;
                repaint();
            }

            schedule(POLL_MS);
        }).catch(function () {
            // readAlive() swallows fetch failures, so reaching here means a HANDLER threw —
            // consider() touches the DOM through safeToReload(), and repaint() reaches into a
            // renderer that reads an IndexedDB-backed store. Without this catch the rejection is
            // unhandled, pollTimer stays null, and the banner is frozen on screen with nothing
            // polling behind it for the rest of the page's life. Keep the loop alive instead.
            schedule(interval);
        });
    }

    /**
     * The background safety net, and the reason a watch can no longer be abandoned for good.
     *
     * Runs for the whole life of the page, in EVERY phase — 'idle' above all, which is the case
     * that used to be terminal: a watch that hit its cap, a tab that was never told anything, or
     * one that slept through the entire deploy. Nothing could re-arm a watch from there, so an
     * unattended kiosk tablet sat on a dead circuit forever.
     *
     * It decides on exactly the same evidence the watch does — a DIFFERENT instance id on a
     * SUCCESSFUL response — so it can never reload a tab onto a server that is not there.
     */
    function heartbeat() {
        heartbeatTimer = null;

        if (reloaded) {
            return;
        }

        readAlive().then(function (body) {
            if (!body || reloaded) {
                return;
            }

            var outcome = consider(body);
            if (outcome === 'reloaded') {
                return;
            }

            if (outcome === 'ready' && pollTimer === null) {
                // Entered 'ready' straight from 'idle': start the slow loop that takes the reload
                // once the text field is empty.
                schedule(READY_POLL_MS);
                return;
            }

            // An announcement raised while this tab was idle — it missed the push, or gave up
            // earlier. This is what re-arms the watch.
            if (outcome === 'same' && body.maintenance && phase === 'idle') {
                begin({ message: body.message });
            }
        }).catch(function () {
            // Same reasoning as the poll chain: a throwing handler must not silently end the one
            // loop that guarantees an unattended tablet eventually recovers.
        }).then(function () {
            armHeartbeat();
        });
    }

    function armHeartbeat() {
        if (reloaded || heartbeatTimer !== null) {
            return;
        }

        heartbeatTimer = setTimeout(heartbeat, jittered(HEARTBEAT_MS));
    }

    function stopHeartbeat() {
        if (heartbeatTimer !== null) {
            clearTimeout(heartbeatTimer);
            heartbeatTimer = null;
        }
    }

    /**
     * Shows the notice and enters watch mode. Safe to call repeatedly — a second announcement only
     * refreshes the wording.
     */
    function begin(announcement) {
        const text = announcement && typeof announcement.message === 'string' && announcement.message.length > 0
            ? announcement.message
            : null;

        if (phase !== 'idle') {
            if (text !== message) {
                message = text;
                repaint();
            }
            return;
        }

        message = text;
        reason = (announcement && announcement.reason) || 'announced';
        phase = 'watching';
        watchStartedAt = Date.now();
        readyStartedAt = 0;
        pollDelay = POLL_MS;

        captureBaseline();
        repaint();
        schedule(0);
    }

    /**
     * Drops the notice and stops the fast watch. Deliberately leaves the heartbeat running: 'idle'
     * is a resting state, not a terminal one, and the heartbeat is what lets the watch be re-armed
     * (or the reload taken outright) later.
     */
    function stop() {
        stopPolling();
        if (baselineTimer !== null) {
            clearTimeout(baselineTimer);
            baselineTimer = null;
        }

        phase = 'idle';
        message = null;
        reason = null;
        readyStartedAt = 0;
        repaint();
        armHeartbeat();
    }

    window.bwMaintenance = {
        begin: begin,
        active: function () {
            return phase !== 'idle';
        },
        state: function () {
            return { phase: phase, message: message, reason: reason };
        },
        reloadNow: function () {
            doReload();
        },
        stop: stop,
        refresh: function () {
            return readAlive();
        }
    };

    // The circuit's server-side state is gone. That is exactly what a container recreate looks
    // like from the client, so start watching: once the instance id has actually changed, reloading
    // is strictly better than the "Session ended — tap to reload" the pill would otherwise offer.
    // If the id turns out NOT to have changed, poll() stops the watch on the next tick and the old
    // wording comes straight back. blazor-boot.js's own handling is untouched.
    if (window.blocwerkConnection) {
        window.blocwerkConnection.subscribe(function (status) {
            if (status === 'rejected' && phase === 'idle') {
                begin({ message: null, reason: 'rejected' });
            }
        });
    }

    // Enhanced navigation morphs the server DOM over ours and can strip the JS-appended pill.
    // Re-render after it, mirroring nav.js / kiosk-idle.js.
    document.addEventListener('enhancedload', function () {
        if (phase !== 'idle') {
            repaint();
        }
    });

    // A tablet that was asleep through the whole window wakes up to a dead circuit; re-read the
    // beacon so it either reloads onto the new build or clears a notice that is no longer true.
    document.addEventListener('visibilitychange', function () {
        if (document.visibilityState !== 'visible' || reloaded) {
            return;
        }

        if (phase === 'idle') {
            // NOT skipped any more. A tablet that slept through a deploy wakes in 'idle' — either
            // it was never told, or its watch hit the cap while the screen was off — and the old
            // guard here meant waking up changed nothing at all. Check immediately instead of
            // waiting out the heartbeat interval.
            stopHeartbeat();
            heartbeat();
            return;
        }

        schedule(0);
    });

    captureBaseline();
    armHeartbeat();
})();
