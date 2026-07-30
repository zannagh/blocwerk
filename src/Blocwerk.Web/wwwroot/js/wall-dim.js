/*
 * Background (wall photo) visibility, persisted per device.
 *
 * Follows the theme.js pattern exactly: a localStorage key, an `apply()` that writes
 * to <html>, applied once at load and again whenever the control changes it. The
 * value lands on a CSS custom property so it costs no server round-trips and cannot
 * be undone by a Blazor re-render.
 *
 *   blocwerk-wall-dim   : "0".."100"  -> --wall-dim (0..1)
 *   blocwerk-wall-focus : "1" | "0"   -> html.bw-wall-focus
 */
(function () {
    const dimKey = 'blocwerk-wall-dim';
    const focusKey = 'blocwerk-wall-focus';

    function getDim() {
        const raw = parseInt(localStorage.getItem(dimKey) || '0', 10);
        return isNaN(raw) ? 0 : Math.max(0, Math.min(100, raw));
    }

    function isFocus() {
        return localStorage.getItem(focusKey) === '1';
    }

    function apply() {
        document.documentElement.style.setProperty('--wall-dim', getDim() / 100);
        document.documentElement.classList.toggle('bw-wall-focus', isFocus());
    }

    apply();

    window.blocwerkWallDim = {
        setDim: function (value) {
            const pct = Math.max(0, Math.min(100, parseInt(value, 10) || 0));
            localStorage.setItem(dimKey, String(pct));
            apply();
            return pct;
        },
        toggleFocus: function () {
            const next = !isFocus();
            localStorage.setItem(focusKey, next ? '1' : '0');
            apply();
            return next;
        },
        getDim: getDim,
        isFocus: isFocus,

        /**
         * Re-syncs freshly rendered controls with the stored value. Blazor renders
         * the slider with a static value, so after every (re)render we push the
         * persisted state back into the DOM.
         */
        hydrate: function () {
            apply();
            document.querySelectorAll('.wall-dim-bar input[type="range"]').forEach(function (el) {
                el.value = getDim();
            });
        },
    };
})();
