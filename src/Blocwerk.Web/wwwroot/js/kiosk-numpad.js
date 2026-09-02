/*
 * bwKioskNumpad — the on-screen numpad for the kiosk PIN step.
 *
 * The tablet is touch-only and wall-mounted, so raising the OS keyboard for four to eight digits is
 * both slow and, at arm's length, unreadable. This drives a SERVER-RENDERED form instead, exactly
 * like kiosk-idle.js and kiosk-pair.js: the page renders
 *   <form id="bw-kiosk-pin-form" method="post" action="/kiosk/act-as" data-pin-length="N">
 *     antiforgery token + hidden userId + <input name="pin" readonly>
 * and this file only ever appends a digit to that input, repaints the mask, and calls submit(). It
 * never reads, copies or reconstructs the antiforgery token, and it makes no fetch/XHR of any kind —
 * so the CSRF protection on /kiosk/act-as is untouched.
 *
 * Nothing here verifies anything. The PIN is checked server-side against a hash by
 * KioskService.VerifyPinAsync; the only thing the page knows is HOW MANY digits to expect, rendered
 * into data-pin-length. That number exists purely so the entry submits ONCE, when it is complete.
 * Submitting at 4, then 5, then 6 digits would spend three of the five attempts KioskThrottleRegistry
 * allows per minute and lock a legitimate climber out of their own wall.
 *
 * Listeners are bound to the document once and re-resolve the form on every event, so enhanced
 * navigation morphing the DOM cannot leave a stale or doubled binding behind.
 */
(function () {
    'use strict';

    const FORM_ID = 'bw-kiosk-pin-form';
    const DISPLAY_ID = 'bw-kiosk-pin-display';
    const COUNT_ID = 'bw-kiosk-pin-count';

    // Matches the 4-8 digit shape KioskService.ConsentAsync enforces when the PIN is set.
    const MAX_DIGITS = 8;

    // Set the moment a completed entry is handed to the browser. A submit is a full navigation, so
    // this only has to hold for the instant between submit() and the page going away — but it is what
    // guarantees "exactly one post per completed entry" even if a stray tap lands in that window.
    let submitting = false;

    function form() {
        return document.getElementById(FORM_ID);
    }

    function expectedLength(f) {
        const raw = parseInt(f.getAttribute('data-pin-length'), 10);
        if (isNaN(raw) || raw < 1 || raw > MAX_DIGITS) {
            // Unknown length (a PIN set before lengths were recorded). The page renders its fallback
            // submit button in that case; the pad still types, it just never auto-submits.
            return 0;
        }

        return raw;
    }

    function render(f) {
        const input = f.querySelector('input[name="pin"]');
        const display = document.getElementById(DISPLAY_ID);
        const count = document.getElementById(COUNT_ID);
        if (!input) {
            return;
        }

        const entered = input.value.length;
        const expected = expectedLength(f);

        if (display) {
            // One dot per expected digit when we know the length, otherwise one per entered digit so
            // the read-out still shows how much has been typed.
            const slots = expected > 0 ? expected : entered;
            while (display.children.length < slots) {
                const dot = document.createElement('span');
                dot.className = 'kiosk-pin-dot';
                display.appendChild(dot);
            }

            while (display.children.length > slots) {
                display.removeChild(display.lastChild);
            }

            for (let i = 0; i < display.children.length; i++) {
                display.children[i].classList.toggle('is-filled', i < entered);
            }
        }

        if (count) {
            // Digit COUNT only — never the digits themselves, so reading the screen from across the
            // room (or through a screen reader) gives a bystander nothing.
            if (expected > 0) {
                count.textContent = entered + ' of ' + expected + ' digits';
            } else if (entered === 0) {
                count.textContent = 'No digits yet';
            } else {
                count.textContent = entered + (entered === 1 ? ' digit' : ' digits');
            }
        }
    }

    function submitIfComplete(f) {
        const input = f.querySelector('input[name="pin"]');
        const expected = expectedLength(f);
        if (!input || expected < 1 || input.value.length !== expected || submitting) {
            return;
        }

        submitting = true;

        // Freeze the pad so a second tap in the navigation window cannot start another attempt.
        f.querySelectorAll('[data-bw-pin-key]').forEach(function (key) {
            key.disabled = true;
        });

        f.submit();
    }

    function press(f, key) {
        if (submitting) {
            return;
        }

        const input = f.querySelector('input[name="pin"]');
        if (!input) {
            return;
        }

        if (key === 'back') {
            input.value = input.value.slice(0, -1);
            render(f);
            return;
        }

        if (!/^[0-9]$/.test(key)) {
            return;
        }

        const expected = expectedLength(f);
        const cap = expected > 0 ? expected : MAX_DIGITS;
        if (input.value.length >= cap) {
            return;
        }

        input.value += key;
        render(f);
        submitIfComplete(f);
    }

    document.addEventListener('click', function (event) {
        const target = event.target instanceof Element ? event.target.closest('[data-bw-pin-key]') : null;
        if (!target) {
            return;
        }

        const f = form();
        if (!f || !f.contains(target)) {
            return;
        }

        event.preventDefault();
        press(f, target.getAttribute('data-bw-pin-key'));
    });

    // A physical keyboard (or a barcode-style numeric pad) still works. The keys themselves are real
    // <button> elements, so Tab/Enter/Space and screen readers already work without this.
    document.addEventListener('keydown', function (event) {
        const f = form();
        if (!f || event.metaKey || event.ctrlKey || event.altKey) {
            return;
        }

        if (event.key === 'Backspace') {
            event.preventDefault();
            press(f, 'back');
            return;
        }

        if (/^[0-9]$/.test(event.key)) {
            event.preventDefault();
            press(f, event.key);
        }
    });

    function arm() {
        const f = form();
        if (!f) {
            return;
        }

        // A fresh render of the step — including the one after a wrong PIN — starts empty and
        // re-opens submitting, so the climber can simply type again.
        submitting = false;
        const input = f.querySelector('input[name="pin"]');
        if (input) {
            input.value = '';
        }

        render(f);
    }

    document.addEventListener('DOMContentLoaded', arm);
    document.addEventListener('enhancedload', arm);
    arm();
})();
