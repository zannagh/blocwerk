/*
 * bwKioskPair — submits the kiosk pairing completion form.
 *
 * This exists for one structural reason: the tablet's pairing page is a Blazor circuit, and a
 * circuit cannot write a cookie. The device registration has to be written on a real HTTP response
 * on the TABLET'S OWN connection, so the circuit renders a form and this submits it.
 *
 * Same contract as kiosk-idle.js: the page renders
 *   <form id="bw-kiosk-pair-complete" method="post" action="/kiosk/pair/complete">…</form>
 * with the antiforgery token and the two hidden fields already server-rendered, and this file
 * submits it without knowing anything about either. Nothing here handles a credential — the claim
 * ticket is a form field the server put there, and this never reads it.
 */
(function () {
    'use strict';

    const FORM_ID = 'bw-kiosk-pair-complete';

    window.bwKioskPair = {
        complete: function () {
            const form = document.getElementById(FORM_ID);
            if (!form) {
                // Nothing to submit. The page keeps its visible "Finish setup" button in the
                // approved state, so the tablet is not stranded.
                return;
            }

            form.submit();
        },
    };
})();
