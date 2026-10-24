/*
 * bwTabbarGuard — steps the fixed bottom tab bar aside while the on-screen keyboard is up.
 *
 * The tab bar is `position: fixed; bottom: 0`, which iOS anchors to the LAYOUT viewport. With the keyboard up the
 * visible part of the page (the visual viewport) is a smaller window that moves inside it, so the bar is drawn
 * wherever the layout viewport's bottom edge happens to fall: the middle of the screen, on top of the field being
 * typed in. While a text field has focus and the window is shrunk by a keyboard, the bar steps aside
 * (`html.bw-vv-away`, see app.css). Coming back, iOS 26 can leave fixed elements where the keyboard pushed them until
 * the page scrolls, so one no-op scroll re-places them.
 *
 * Deliberately NOT for zoom alone: a page that stays zoomed (a pinch) must keep its navigation reachable, and the
 * reported mid-screen bar came from iOS's focus zoom on fields under 16px, which app.css now prevents on touch screens.
 * The guard only ever hides the bar while focus is in a text field, so blurring it always brings the bar back.
 *
 * Touch devices only.
 */
window.bwTabbarGuard = (function () {
    'use strict';

    // A keyboard takes far more than this; URL-bar and toolbar changes take less.
    const KEYBOARD_PX = 120;
    const NOT_TYPED = /^(button|checkbox|radio|range|color|file|submit|reset|image|hidden)$/i;

    let away = false;
    let frame = 0;

    function coarse() {
        return !!(window.matchMedia && window.matchMedia('(pointer: coarse)').matches);
    }

    function typing(el) {
        if (!el) {
            return false;
        }
        if (el.isContentEditable || el.tagName === 'TEXTAREA') {
            return true;
        }

        return el.tagName === 'INPUT' && !NOT_TYPED.test(el.type || '');
    }

    /** True while a text field has focus and a keyboard has shrunk the visible window. */
    function displaced() {
        const vv = window.visualViewport;
        if (!vv || !coarse()) {
            return false;
        }
        return typing(document.activeElement) && document.documentElement.clientHeight - vv.height > KEYBOARD_PX;
    }

    // Re-places fixed elements iOS left where the keyboard had pushed them. A scroll of zero distance is
    // not always enough for WebKit, so one pixel there and back (both clamped, so harmless at either end).
    function replaceFixed() {
        const x = window.scrollX;
        const y = window.scrollY;
        window.scrollTo(x, y + 1);
        window.scrollTo(x, y);
    }

    function update() {
        frame = 0;
        const next = displaced();
        if (next === away) {
            return;
        }

        away = next;
        document.documentElement.classList.toggle('bw-vv-away', away);
        if (!away) {
            replaceFixed();
        }
    }

    function schedule() {
        if (!frame) {
            frame = requestAnimationFrame(update);
        }
    }

    function init() {
        if (window._bwTabbarGuardBound) {
            return;
        }
        window._bwTabbarGuardBound = true;

        if (window.visualViewport && window.visualViewport.addEventListener) {
            window.visualViewport.addEventListener('resize', schedule, { passive: true });
            window.visualViewport.addEventListener('scroll', schedule, { passive: true });
        }
        document.addEventListener('focusin', schedule, true);
        // The keyboard closes after focus leaves; the viewport resize that follows schedules again.
        document.addEventListener('focusout', schedule, true);
    }

    init();

    return {
        /** Re-evaluates now (no frame wait). For tests and the console. */
        refresh: update,
        isAway: function () { return away; },
    };
})();
