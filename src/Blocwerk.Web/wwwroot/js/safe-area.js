/*
 * bwSafeArea — re-applies the safe-area insets an installed iOS app can start without.
 *
 * iOS can start a home-screen app with env(safe-area-inset-*) still 0 (and `dvh` short) until the first rotation: the
 * top bar's content then sits under the translucent status bar (black-translucent draws the page behind it) and the
 * bottom bar above a dead strip. Re-applying viewport-fit=cover makes WebKit recompute the insets (the approach from
 * https://gist.github.com/fozzedout/5e77925381991a9570151550992baf14).
 *
 * Only an installed iOS app (navigator.standalone is WebKit's own flag), and only when both insets came up empty: an
 * installed app in portrait always has a status bar above it, so a correct start is left alone.
 */
window.bwSafeArea = (function () {
    'use strict';

    function probeInset(side) {
        const probe = document.createElement('div');
        probe.style.cssText = 'position:fixed;visibility:hidden;pointer-events:none;height:env(safe-area-inset-' + side + ',0px)';
        document.body.appendChild(probe);
        const px = probe.offsetHeight;
        probe.remove();
        return px;
    }

    function recompute() {
        const meta = document.querySelector('meta[name="viewport"]');
        if (!meta || navigator.standalone !== true || probeInset('top') > 0 || probeInset('bottom') > 0) {
            return;
        }

        const content = meta.getAttribute('content');
        meta.setAttribute('content', content.replace('viewport-fit=cover', 'viewport-fit=auto'));
        requestAnimationFrame(function () {
            meta.setAttribute('content', content);
        });
    }

    if (document.body) {
        recompute();
    } else {
        document.addEventListener('DOMContentLoaded', recompute, { once: true });
    }
    // A home-screen app restored from the background comes back through pageshow, with the same risk.
    window.addEventListener('pageshow', recompute);

    return {
        /** The inset WebKit reports right now, in CSS px ('top', 'bottom', 'left', 'right'). For the console. */
        inset: probeInset,
        recompute: recompute,
    };
})();
