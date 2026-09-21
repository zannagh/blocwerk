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
    const DESKTOP = '(min-width: 900px)';

    /*
     * Layout tier override.
     *
     * The desktop tier is now a CSS breakpoint (pages.css, 900px), so this toggle is no longer
     * "off by default" — it is the manual override at BOTH ends, which is what keeps it from
     * becoming a no-op on a wide window:
     *   stored 'wide'   -> html.bw-fullscreen, force the wide layout on a narrow window
     *   stored 'narrow' -> html.bw-compact,    force the phone column on a wide window
     *   nothing stored  -> follow the breakpoint
     *
     * Legacy values: '1' was the old opt-in and still means 'wide'. '0' was merely "never turned
     * it on" — under the old default that was everybody, so it must NOT be read as an explicit
     * request for the phone column on desktop; it is migrated away to "follow the breakpoint".
     */
    function stored() {
        const v = localStorage.getItem(key);
        if (v === '1' || v === 'wide') return 'wide';
        if (v === 'narrow') return 'narrow';
        if (v === '0') localStorage.removeItem(key);
        return null;
    }

    function isWide() {
        const pref = stored();
        if (pref) return pref === 'wide';
        return window.matchMedia(DESKTOP).matches;
    }

    function apply(pref) {
        const html = document.documentElement;
        html.classList.toggle('bw-fullscreen', pref === 'wide');
        html.classList.toggle('bw-compact', pref === 'narrow');
    }

    apply(stored());

    window.blocwerkLayout = {
        toggle: function () {
            const next = !isWide();
            localStorage.setItem(key, next ? 'wide' : 'narrow');
            apply(next ? 'wide' : 'narrow');
            return next;
        },
        // Reported to TopBarActions as the button's active state: the EFFECTIVE tier, not the
        // stored override, so the icon matches what the user is looking at on a desktop window.
        isFullscreen: isWide
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

        // Mirrors the tri-state above ('wide' / 'narrow' / follow the breakpoint); '1' is the
        // legacy spelling of 'wide'. No stored value means neither class belongs on <html>.
        const pref = localStorage.getItem('blocwerk-fullscreen');
        const wantWide = pref === 'wide' || pref === '1';
        const wantCompact = pref === 'narrow';
        if (html.classList.contains('bw-fullscreen') !== wantWide) {
            html.classList.toggle('bw-fullscreen', wantWide);
        }
        if (html.classList.contains('bw-compact') !== wantCompact) {
            html.classList.toggle('bw-compact', wantCompact);
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
