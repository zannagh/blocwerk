/*
 * bwInstall — the "add Blocwerk to your home screen" banner's client half.
 *
 * Two jobs, and they are separate on purpose:
 *
 *   1. Capture `beforeinstallprompt` as early as possible. Chromium fires it once, shortly after
 *      load, and only a listener that already exists gets it — which is why this file is loaded
 *      from <head> rather than with the deferred bundle at the end of <body>. The event is stashed
 *      so the Install button can replay it later, on a real user gesture, which is the only time
 *      prompt() is permitted.
 *
 *   2. Answer `shouldShow()` for the Blazor component, which renders nothing until it has. That
 *      ordering is what keeps the banner from flashing on a device that already has the app.
 *
 * NEVER shown when the app is already running installed (display-mode: standalone, or iOS Safari's
 * navigator.standalone), when the device isn't a phone/small tablet, or when the user said no. The
 * kiosk case is decided server-side in the component — this file never sees it.
 *
 * Persistence is localStorage, mirroring nav.js's collapsed-tab-bar flag: it is a per-device UI
 * choice the server never needs to read. bwPrefs deliberately writes cookies instead, for the
 * values the server DOES read on the first paint, so this is not a parallel mechanism to it.
 */
window.bwInstall = (function () {
    'use strict';

    const NEVER_KEY = 'blocwerk-install-never';
    const SNOOZE_KEY = 'blocwerk-install-snoozed-until';

    // "No thanks" is a not-right-now, so it expires. Two weeks is long enough that the banner is
    // never a per-visit nag and short enough that a regular user gets one more chance in a season.
    const SNOOZE_DAYS = 14;

    // Above this width it isn't a phone or a small tablet, whatever the pointer says.
    const MOBILE_MAX_WIDTH = 900;

    let deferredPrompt = null;

    // Must be attached at parse time: the event fires once and is not replayed for late listeners.
    window.addEventListener('beforeinstallprompt', function (e) {
        // Suppress Chromium's own mini-infobar; the banner is our affordance for the same thing.
        e.preventDefault();
        deferredPrompt = e;
    });

    // An install that completes through any route (our button, the browser's menu) makes the
    // stashed event useless, and the banner pointless for the rest of the visit.
    window.addEventListener('appinstalled', function () {
        deferredPrompt = null;
        setNever();
    });

    function readItem(key) {
        try {
            return localStorage.getItem(key);
        } catch (e) {
            return null;
        }
    }

    function writeItem(key, value) {
        try {
            localStorage.setItem(key, value);
        } catch (e) {
            /* storage blocked (private mode) — the banner still hides for this view. */
        }
    }

    function setNever() {
        writeItem(NEVER_KEY, '1');
    }

    function isInstalled() {
        try {
            if (window.matchMedia && window.matchMedia('(display-mode: standalone)').matches) {
                return true;
            }
        } catch (e) {
            /* matchMedia unavailable — fall through to the iOS check. */
        }

        // iOS Safari never reports the standalone display-mode; this is its equivalent.
        return window.navigator.standalone === true;
    }

    // Capability, not user-agent: a coarse pointer says "finger", and the width keeps a touch
    // laptop or a big desktop out. Both have to hold, so a mouse-driven narrow window and a
    // wide touchscreen are equally excluded — installing there is not what the banner is for.
    function isMobile() {
        try {
            if (!window.matchMedia || !window.matchMedia('(pointer: coarse)').matches) {
                return false;
            }
        } catch (e) {
            return false;
        }

        return Math.min(window.innerWidth, window.screen ? window.screen.width : window.innerWidth)
            <= MOBILE_MAX_WIDTH;
    }

    function isSuppressed() {
        if (readItem(NEVER_KEY) === '1') {
            return true;
        }

        const until = parseInt(readItem(SNOOZE_KEY), 10);
        return !isNaN(until) && Date.now() < until;
    }

    // UA sniffing, but only to pick the WORDS: iPhone/iPad Safari has no install API, so the
    // instructions have to name its actual menu. Nothing is gated on this. iPadOS reports itself
    // as a Mac, so the touch-point count is what separates it from a desktop Safari.
    function isIos() {
        const ua = navigator.userAgent || '';
        if (/iPhone|iPad|iPod/i.test(ua)) {
            return true;
        }
        return /Macintosh/.test(ua) && navigator.maxTouchPoints > 1;
    }

    return {
        // The component renders nothing until this resolves, so there is no banner to un-render.
        shouldShow: function () {
            try {
                return !isInstalled() && isMobile() && !isSuppressed();
            } catch (e) {
                return false;
            }
        },

        // Returns 'accepted', 'dismissed', or 'unavailable' — the last one meaning this browser
        // never offered a programmatic install, so the caller falls back to instructions.
        install: async function () {
            if (!deferredPrompt) {
                return 'unavailable';
            }

            try {
                const prompt = deferredPrompt;
                // Single-use: the browser refuses a second prompt() on the same event.
                deferredPrompt = null;
                prompt.prompt();
                const choice = await prompt.userChoice;
                if (choice && choice.outcome === 'accepted') {
                    setNever();
                    return 'accepted';
                }
                return 'dismissed';
            } catch (e) {
                return 'unavailable';
            }
        },

        // Short, and true for the platform in front of the user.
        instructions: function () {
            if (isIos()) {
                return 'In Safari: tap Share, then "Add to Home Screen".';
            }
            return 'Open your browser menu and choose "Install app" or "Add to Home screen".';
        },

        snooze: function () {
            writeItem(SNOOZE_KEY, String(Date.now() + SNOOZE_DAYS * 24 * 60 * 60 * 1000));
        },

        never: setNever
    };
})();
