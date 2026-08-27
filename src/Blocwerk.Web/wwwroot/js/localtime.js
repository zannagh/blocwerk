/*
 * bwTime — client-side localization of server-rendered UTC timestamps.
 *
 * This is Blazor Server, so timestamps are localized on the CLIENT. The <LocalTime> component
 * renders `<time datetime="{utc-iso}" data-bw-fmt="{key}">{utc fallback}</time>`; this module
 * rewrites the text to the viewer's local time. Re-formatting is driven purely off the
 * `datetime` attribute, so it is idempotent and safe to re-run after any Blazor re-render.
 *
 * Install mirrors prefs.js (DOMContentLoaded + enhancedload) with a MutationObserver backstop
 * (like theme.js) so interactive re-renders — new comments, updated panels — get localized too.
 */
window.bwTime = (function () {
    'use strict';

    // 24-hour time to match the app's "HH:mm" sites. The browser's own locale drives everything
    // else (month names, ordering) via Intl / toLocale*.
    var OPTS = {
        'date': { year: 'numeric', month: 'short', day: 'numeric' },
        'date-dmy': { day: 'numeric', month: 'short', year: 'numeric' },
        'day-month': { day: 'numeric', month: 'short' },
        'datetime': { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit', hour12: false },
        'time': { hour: '2-digit', minute: '2-digit', hour12: false },
        'month-year': { year: 'numeric', month: 'long' },
        'weekday-date': { weekday: 'long', day: '2-digit', month: 'short', year: 'numeric' },
        'full': { weekday: 'long', day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit', hour12: false },
    };

    // Mirrors Blocwerk.Web.Components.Shared.TimeText.Relative — keep the thresholds and wording
    // in lockstep with that C# file.
    function relative(date) {
        var diffMs = Date.now() - date.getTime();
        var minutes = diffMs / 60000;
        var hours = minutes / 60;
        var days = hours / 24;

        if (minutes < 1) {
            return 'just now';
        }
        if (hours < 1) {
            return Math.floor(minutes) + 'm ago';
        }
        if (days < 1) {
            return Math.floor(hours) + 'h ago';
        }
        if (days < 1.5) {
            return 'yesterday';
        }
        if (days < 7) {
            return Math.floor(days) + 'd ago';
        }
        // "MMM d" in the browser's locale.
        return date.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
    }

    function format(date, key) {
        if (key === 'relative') {
            return relative(date);
        }
        var opts = OPTS[key] || OPTS['date'];
        return new Intl.DateTimeFormat(undefined, opts).format(date);
    }

    // Format a UTC instant (Unix epoch milliseconds, or an ISO string) to the viewer's local time
    // using the same option maps as the <time> sweep. Used by charts that format point times in JS
    // rather than via a DOM node. Returns '' on any bad input so it never breaks a caller.
    function formatUtc(value, key) {
        try {
            var date = new Date(value);
            if (isNaN(date.getTime())) {
                return '';
            }
            return format(date, key || 'datetime');
        } catch (e) {
            return '';
        }
    }

    function localizeOne(el) {
        try {
            var raw = el.getAttribute('datetime');
            if (!raw) {
                return;
            }
            var date = new Date(raw);
            if (isNaN(date.getTime())) {
                return;
            }
            var key = el.getAttribute('data-bw-fmt') || 'date';
            el.textContent = format(date, key);
            // A full local datetime on hover — nice-to-have, cheap, never throws on its own.
            el.title = date.toLocaleString();
        } catch (e) {
            /* never let one bad node break the sweep. */
        }
    }

    function localizeAll(root) {
        try {
            var scope = root || document;
            var nodes = scope.querySelectorAll('time[data-bw-fmt]');
            for (var i = 0; i < nodes.length; i++) {
                localizeOne(nodes[i]);
            }
        } catch (e) {
            /* never throw from a localization sweep. */
        }
    }

    function install() {
        try {
            localizeAll();

            // Blazor.start() runs later (blazor-boot.js), so poll briefly for the API and attach
            // enhancedload once it exists (mirrors prefs.js).
            var tries = 0;
            var timer = setInterval(function () {
                tries++;
                if (window.Blazor && window.Blazor.addEventListener) {
                    window.Blazor.addEventListener('enhancedload', function () { localizeAll(); });
                    clearInterval(timer);
                } else if (tries > 100) {
                    clearInterval(timer);
                }
            }, 100);

            // Backstop for interactive re-renders (new comments, updated panels): localize any
            // time[data-bw-fmt] node Blazor adds after the initial paint. Mirrors theme.js's
            // MutationObserver approach.
            if (document.body) {
                var observer = new MutationObserver(function (mutations) {
                    for (var m = 0; m < mutations.length; m++) {
                        var added = mutations[m].addedNodes;
                        for (var n = 0; n < added.length; n++) {
                            var node = added[n];
                            if (node.nodeType !== 1) {
                                continue;
                            }
                            if (node.matches && node.matches('time[data-bw-fmt]')) {
                                localizeOne(node);
                            }
                            if (node.querySelectorAll) {
                                localizeAll(node);
                            }
                        }
                    }
                });
                observer.observe(document.body, { childList: true, subtree: true });
            }
        } catch (e) {
            /* never block page startup on localization. */
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', install);
    } else {
        install();
    }

    return {
        localizeAll: localizeAll,
        localizeOne: localizeOne,
        formatUtc: formatUtc
    };
})();
