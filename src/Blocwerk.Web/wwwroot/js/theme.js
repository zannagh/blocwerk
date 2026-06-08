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
