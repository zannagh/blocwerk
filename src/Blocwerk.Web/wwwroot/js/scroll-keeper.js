/*
 * bwScrollKeeper — puts the page back where it was after the circuit behind it was replaced.
 *
 * A dropped connection normally reconnects to the same circuit and nothing on screen moves. When
 * the server no longer has the circuit there are two ways back, and both rebuild the page:
 *
 *   - resume: Blazor opens a new circuit in place and renders the page again, no navigation.
 *   - reload: maintenance.js reloads once the server answers again (a deploy, or a circuit that
 *     could not be resumed).
 *
 * Either way every scrolled element starts at the top again, which on a phone reads as "the app
 * threw me out". This file remembers the scroll offsets just before and applies them again once
 * the new content is there. Horizontal offsets are kept too, so a panel carousel comes back on the
 * panel it was showing.
 *
 * Exposes `window.bwScrollKeeper`:
 *   keep()           -> remember the offsets in memory (blazor-boot.js, before a resume)
 *   restore()        -> apply what keep() remembered (blazor-boot.js, after a resume)
 *   keepForReload()  -> remember them across a location.reload() (maintenance.js)
 */
(function () {
    'use strict';

    const KEY = 'bwScrollKeep';

    // A kept position is only meant for the page load or resume that follows right away.
    const MAX_AGE_MS = 60 * 1000;

    // The new content arrives in several renders, so the offsets are applied repeatedly until they
    // have held for a moment, and given up on if the page never grows tall enough.
    const TRY_MS = 250;
    const SETTLED_TRIES = 6;
    const GIVE_UP_MS = 8000;

    const MAX_SPOTS = 8;
    const CANCEL_EVENTS = ['pointerdown', 'touchstart', 'wheel', 'keydown'];

    let kept = null;

    function here() {
        return location.pathname + location.search;
    }

    function selectorFor(el) {
        if (el.id) {
            return '#' + CSS.escape(el.id);
        }

        const first = el.classList.length > 0 ? '.' + CSS.escape(el.classList[0]) : '';
        return el.tagName.toLowerCase() + first;
    }

    function capture() {
        const spots = [];
        const all = document.body ? document.body.getElementsByTagName('*') : [];

        for (let i = 0; i < all.length && spots.length < MAX_SPOTS; i++) {
            const el = all[i];
            if (el.scrollTop > 0 || el.scrollLeft > 0) {
                const selector = selectorFor(el);
                spots.push({
                    selector: selector,
                    index: Array.prototype.indexOf.call(document.querySelectorAll(selector), el),
                    top: el.scrollTop,
                    left: el.scrollLeft
                });
            }
        }

        return { url: here(), at: Date.now(), x: window.scrollX, y: window.scrollY, spots: spots };
    }

    /** Applies the offsets once. True when every one of them is where it was. */
    function apply(saved) {
        let settled = true;

        if (saved.x > 0 || saved.y > 0) {
            window.scrollTo(saved.x, saved.y);
            settled = Math.abs(window.scrollY - saved.y) <= 2;
        }

        saved.spots.forEach(function (spot) {
            const el = document.querySelectorAll(spot.selector)[spot.index];
            if (!el) {
                settled = false;
                return;
            }

            el.scrollTop = spot.top;
            el.scrollLeft = spot.left;
            if (Math.abs(el.scrollTop - spot.top) > 2 || Math.abs(el.scrollLeft - spot.left) > 2) {
                settled = false;
            }
        });

        return settled;
    }

    function restoreFrom(saved) {
        if (!saved || saved.url !== here() || Date.now() - saved.at > MAX_AGE_MS) {
            return;
        }

        const started = Date.now();
        let settledFor = 0;
        let cancelled = false;

        // The moment the user touches the page it is theirs again; never scroll under a finger.
        function cancel() {
            cancelled = true;
        }
        CANCEL_EVENTS.forEach(function (name) {
            window.addEventListener(name, cancel, { once: true, passive: true, capture: true });
        });

        (function attempt() {
            if (cancelled || saved.url !== here()) {
                return;
            }

            settledFor = apply(saved) ? settledFor + 1 : 0;
            if (settledFor < SETTLED_TRIES && Date.now() - started < GIVE_UP_MS) {
                setTimeout(attempt, TRY_MS);
            }
        })();
    }

    function takeStored() {
        try {
            const raw = window.sessionStorage.getItem(KEY);
            window.sessionStorage.removeItem(KEY);
            return raw ? JSON.parse(raw) : null;
        } catch (e) {
            // No storage (private browsing) or a value we did not write: start at the top.
            return null;
        }
    }

    window.bwScrollKeeper = {
        keep: function () {
            kept = capture();
        },
        restore: function () {
            const saved = kept;
            kept = null;
            restoreFrom(saved);
        },
        keepForReload: function () {
            try {
                window.sessionStorage.setItem(KEY, JSON.stringify(capture()));
            } catch (e) {
                // Storage is unavailable: the reload still happens, it just lands at the top.
            }
        }
    };

    const stored = takeStored();
    if (stored) {
        if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', function () {
                restoreFrom(stored);
            });
        } else {
            restoreFrom(stored);
        }
    }
})();
