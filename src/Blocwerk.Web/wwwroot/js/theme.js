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
    window.blocwerkChrome = {
        reapply: function () {
            const theme = localStorage.getItem('blocwerk-theme');
            if (theme === 'light' || theme === 'dark') {
                document.documentElement.setAttribute('data-theme', theme);
            } else {
                document.documentElement.removeAttribute('data-theme');
            }

            document.documentElement.classList.toggle(
                'bw-fullscreen', localStorage.getItem('blocwerk-fullscreen') === '1');
        }
    };
})();
