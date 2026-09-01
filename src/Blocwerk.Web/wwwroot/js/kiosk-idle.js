/*
 * bwKioskIdle — client-side courtesy timer for a kiosk "acting as" session.
 *
 * This is UX, NOT the gate. The real 30-minute idle window is enforced server-side, on every
 * cookie validation (see KioskSessionValidator): a tablet whose browser was killed, whose clock was
 * moved, or whose JS was disabled is still dropped back to anonymous the moment anybody uses it
 * again. What this adds is the visible half — the tablet returns to the picker on its own, so the
 * next climber doesn't walk up to somebody else's logged-in session still on screen.
 *
 * It expects the page to render the release form (Phase 3):
 *   <form id="bw-kiosk-release" method="post" action="/kiosk/release">antiforgery token</form>
 * Submitting that form is how the timer fires, which keeps the antiforgery token server-rendered
 * and means this file never has to know anything about it.
 *
 * Enhanced navigation morphs the DOM without a full load, so the timer is re-armed on
 * `enhancedload` as well as on first paint (mirroring nav.js / theme.js).
 */
(function () {
    'use strict';

    const FORM_ID = 'bw-kiosk-release';

    // A minute short of the server's 30, so the tablet has usually released itself before the
    // server would have rejected the cookie — the two never disagree on screen.
    const IDLE_MS = 29 * 60 * 1000;

    const ACTIVITY_EVENTS = ['pointerdown', 'keydown', 'touchstart', 'wheel'];

    let timer = null;

    function release() {
        const form = document.getElementById(FORM_ID);
        if (form) {
            form.submit();
        }
    }

    function reset() {
        if (timer !== null) {
            clearTimeout(timer);
        }

        timer = setTimeout(release, IDLE_MS);
    }

    function arm() {
        if (!document.getElementById(FORM_ID)) {
            // Not a kiosk session (or not acting as anybody): nothing to time out.
            if (timer !== null) {
                clearTimeout(timer);
                timer = null;
            }

            return;
        }

        reset();
    }

    ACTIVITY_EVENTS.forEach(function (name) {
        document.addEventListener(name, function () {
            if (timer !== null) {
                reset();
            }
        }, { passive: true, capture: true });
    });

    document.addEventListener('DOMContentLoaded', arm);
    document.addEventListener('enhancedload', arm);
    arm();
})();
