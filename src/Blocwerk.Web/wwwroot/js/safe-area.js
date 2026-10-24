/*
 * bwSafeArea — re-applies the safe-area insets an installed iOS app can start without.
 *
 * iOS can start a home-screen app with env(safe-area-inset-*) still 0 (and `dvh` short) until the first rotation: the
 * top bar's content then sits under the translucent status bar (black-translucent draws the page behind it) and the
 * bottom bar above a dead strip. Following https://gist.github.com/fozzedout/5e77925381991a9570151550992baf14:
 * in an installed app, flip viewport-fit to auto and back to cover, which makes WebKit recompute the insets, at
 * start, again after 100, 500 and 1000 ms (the insets can settle late on a cold start), and on every pageshow
 * (restored from the background) and orientationchange (rotations report stale geometry for a while).
 *
 * Only an installed iOS app (navigator.standalone is WebKit's own flag); a browser tab and every other platform are
 * left alone. The flip is idempotent and costs one relayout.
 */
window.bwSafeArea = (function () {
    'use strict';

    const RECHECK_MS = [100, 500, 1000];

    function probeInset(side) {
        const probe = document.createElement('div');
        probe.style.cssText = 'position:fixed;visibility:hidden;pointer-events:none;height:env(safe-area-inset-' + side + ',0px)';
        document.body.appendChild(probe);
        const px = probe.offsetHeight;
        probe.remove();
        return px;
    }

    function reapply() {
        const meta = document.querySelector('meta[name="viewport"]');
        if (!meta || navigator.standalone !== true) {
            return;
        }

        const content = meta.getAttribute('content');
        if (content.indexOf('viewport-fit=cover') < 0) {
            return;
        }

        meta.setAttribute('content', content.replace('viewport-fit=cover', 'viewport-fit=auto'));
        requestAnimationFrame(function () {
            meta.setAttribute('content', content);
        });
    }

    function burst() {
        reapply();
        for (let i = 0; i < RECHECK_MS.length; i++) {
            setTimeout(reapply, RECHECK_MS[i]);
        }
    }

    if (document.body) {
        burst();
    } else {
        document.addEventListener('DOMContentLoaded', burst, { once: true });
    }
    window.addEventListener('pageshow', burst);
    window.addEventListener('orientationchange', burst);

    return {
        /** The inset WebKit reports right now, in CSS px ('top', 'bottom', 'left', 'right'). For the console. */
        inset: probeInset,
        reapply: reapply,
    };
})();
