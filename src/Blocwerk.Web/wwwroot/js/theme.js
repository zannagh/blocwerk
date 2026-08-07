(function () {
    const key = 'blocwerk-theme';

    function getPreferred() {
        const stored = localStorage.getItem(key);
        if (stored === 'light' || stored === 'dark') return stored;
        return null;
    }

    function apply(theme) {
        if (theme) {
            document.documentElement.setAttribute('data-theme', theme);
        } else {
            document.documentElement.removeAttribute('data-theme');
        }
    }

    apply(getPreferred());

    window.blocwerkTheme = {
        toggle: function () {
            const current = getPreferred();
            const isDark = current === 'dark' ||
                (!current && window.matchMedia('(prefers-color-scheme: dark)').matches);
            const next = isDark ? 'light' : 'dark';
            localStorage.setItem(key, next);
            apply(next);
            return next;
        },
        isDark: function () {
            const stored = getPreferred();
            if (stored) return stored === 'dark';
            return window.matchMedia('(prefers-color-scheme: dark)').matches;
        }
    };
})();

(function () {
    const key = 'blocwerk-fullscreen';

    function isOn() {
        return localStorage.getItem(key) === '1';
    }

    function apply(on) {
        document.documentElement.classList.toggle('bw-fullscreen', on);
    }

    apply(isOn());

    window.blocwerkLayout = {
        toggle: function () {
            const next = !isOn();
            localStorage.setItem(key, next ? '1' : '0');
            apply(next);
            return next;
        },
        isFullscreen: isOn
    };
})();

/*
 * Re-apply the <html> chrome (theme + fullscreen/desktop layout) from localStorage.
 *
 * Blazor's enhanced navigation morphs the incoming server DOM over the current one, and the
 * server never emits these client-only classes/attributes — so a plain navigation strips
 * `data-theme` and `.bw-fullscreen` off <html>, dropping the user back into light/mobile until
 * they re-toggle. blazor-boot.js calls this on every `enhancedload` to restore them.
 */
(function () {
    const html = document.documentElement;

    // Restore chrome from localStorage, but only touch the DOM when it actually drifts —
    // so the MutationObserver below never sees a self-inflicted mutation and can't loop.
    function reapply() {
        const theme = localStorage.getItem('blocwerk-theme');
        const desiredTheme = (theme === 'light' || theme === 'dark') ? theme : null;
        if (desiredTheme) {
            if (html.getAttribute('data-theme') !== desiredTheme) {
                html.setAttribute('data-theme', desiredTheme);
            }
        } else if (html.hasAttribute('data-theme')) {
            html.removeAttribute('data-theme');
        }

        const wantFullscreen = localStorage.getItem('blocwerk-fullscreen') === '1';
        if (html.classList.contains('bw-fullscreen') !== wantFullscreen) {
            html.classList.toggle('bw-fullscreen', wantFullscreen);
        }
    }

    window.blocwerkChrome = { reapply: reapply };

    // Blazor's enhanced navigation morphs the server DOM over the current one and never emits
    // these client-only class/attribute values, so any navigation can strip them off <html>.
    // The `enhancedload` hook (blazor-boot.js) is the fast path; this observer is the backstop
    // that catches every other removal cause (timing gaps, interactive-render navigations) by
    // restoring chrome the moment <html>'s class/data-theme drifts from localStorage.
    const observer = new MutationObserver(reapply);
    observer.observe(html, { attributes: true, attributeFilter: ['class', 'data-theme'] });
})();
