/*
 * Focus on a boulder view, kept per device: the lightbulb over a boulder's photo (BoulderFocusButton)
 * dims the photo so the boulder's outlined holds stand out.
 *
 * Like theme.js: a localStorage key and an apply() that writes a class to <html>, run at parse and on
 * every toggle, so it costs no server round-trip and no Blazor re-render can undo it. The class styles
 * img.boulder-photo only (boulder-focus.css), which only the boulder view's photos carry, so no other
 * photo in the app is ever dimmed by it.
 *
 *   blocwerk-boulder-focus : "1" | "0"  -> html.bw-boulder-focus
 */
(function () {
    'use strict';

    const key = 'blocwerk-boulder-focus';

    // The dim slider and focus button this replaces kept their state here, and styled every wall photo.
    const retired = ['blocwerk-wall-dim', 'blocwerk-wall-focus'];

    function read() {
        try {
            return window.localStorage.getItem(key) === '1';
        } catch (_) {
            return false; // storage blocked: focus starts off
        }
    }

    let on = read();

    function apply() {
        document.documentElement.classList.toggle('bw-boulder-focus', on);
        document.querySelectorAll('.boulder-focus-tool').forEach(function (button) {
            button.setAttribute('aria-pressed', on ? 'true' : 'false');
        });
    }

    try {
        retired.forEach(function (k) { window.localStorage.removeItem(k); });
    } catch (_) { /* storage blocked: nothing stored there either */ }

    apply();

    window.blocwerkBoulderFocus = {
        toggle: function () {
            on = !on;
            try {
                window.localStorage.setItem(key, on ? '1' : '0');
            } catch (_) { /* storage blocked: the choice lasts for this page only */ }

            apply();
            return on;
        },

        /** Puts the stored state on freshly rendered buttons (Blazor renders them without aria-pressed). */
        hydrate: apply,
    };
})();
