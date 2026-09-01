/*
 * bwNav — hide/show toggle for the bottom tab bar.
 *
 * MainLayout is rendered statically (no @rendermode), so the chevron buttons can't use Blazor
 * @onclick — the handlers never fire. Instead the collapsed STATE lives as a class on
 * document.body, toggled here and reflected purely in CSS:
 *   body.bw-tabbar-collapsed .bw-tabbar   { display: none; }   (hides the bar + its down-chevron)
 *   body.bw-tabbar-collapsed .tabbar-restore { display: flex; } (shows the floating up-chevron)
 *
 * The state persists to localStorage so it survives the static layout's per-navigation
 * re-renders. Enhanced navigation morphs the server DOM over ours without a full page load, so we
 * re-apply on `enhancedload` (mirroring theme.js) as well as on the first paint.
 */
(function () {
    'use strict';

    const KEY = 'blocwerk-tabbar-collapsed';
    const CLASS = 'bw-tabbar-collapsed';

    function isCollapsed() {
        try {
            return localStorage.getItem(KEY) === '1';
        } catch (e) {
            return false;
        }
    }

    function persist(collapsed) {
        try {
            if (collapsed) {
                localStorage.setItem(KEY, '1');
            } else {
                localStorage.removeItem(KEY);
            }
        } catch (e) {
            /* storage blocked (private mode) — the class still toggles for this view. */
        }
    }

    // Body may not exist yet when this script runs in <head>, so guard the access.
    function apply() {
        if (!document.body) {
            return;
        }
        document.body.classList.toggle(CLASS, isCollapsed());
    }

    function install() {
        apply();

        // Blazor.start() runs later (blazor-boot.js), so poll briefly for the API and attach the
        // enhancedload listener once it exists rather than assuming it's ready at head-parse time.
        try {
            let tries = 0;
            const timer = setInterval(function () {
                tries++;
                if (window.Blazor && window.Blazor.addEventListener) {
                    window.Blazor.addEventListener('enhancedload', apply);
                    clearInterval(timer);
                } else if (tries > 100) {
                    clearInterval(timer);
                }
            }, 100);
        } catch (e) {
            /* never block page startup on wiring the re-apply hook. */
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', install);
    } else {
        install();
    }

    window.bwNav = {
        toggleTabbar: function () {
            const next = !isCollapsed();
            persist(next);
            if (document.body) {
                document.body.classList.toggle(CLASS, next);
            }
            return next;
        }
    };
})();
