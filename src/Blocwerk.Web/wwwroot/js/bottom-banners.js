// Bottom banners (cookie consent, "install the app", "get notified") never cover the page.
//
// The banners are fixed above the tab bar, one at a time (app.css). This keeps --bw-banner-h on
// <html> at the height of the one showing (plus a gap), and app.css adds it to --bottomnav-h, the
// token every page already budgets for the tab bar with: .content's bottom padding and every
// dvh-sized view (the wall carousel and its dots, the 3D stage and its preset buttons) shrink by
// the banner instead of sliding under it. Dismissing the banner gives the space back.
(function () {
    'use strict';

    const SELECTOR = '.bw-consent, .bw-install';
    const GAP_PX = 8;
    let observed = null;
    let last = -1;

    const resizer = typeof ResizeObserver === 'function' ? new ResizeObserver(update) : null;

    function visibleBanner() {
        for (const el of document.querySelectorAll(SELECTOR)) {
            if (el.getClientRects().length > 0 && getComputedStyle(el).display !== 'none') {
                return el;
            }
        }
        return null;
    }

    function update() {
        const banner = visibleBanner();
        if (banner !== observed) {
            if (observed && resizer) {
                resizer.unobserve(observed);
            }
            observed = banner;
            if (banner && resizer) {
                resizer.observe(banner);
            }
        }
        const h = banner ? Math.ceil(banner.getBoundingClientRect().height) + GAP_PX : 0;
        if (h === last) {
            return;
        }
        last = h;
        document.documentElement.style.setProperty('--bw-banner-h', `${h}px`);
    }

    // Blazor renders the banners after the circuit starts and removes them on a tap, so watch the
    // DOM. Debounced on a timer (not a frame: the 3D view's scale text changes every frame while it
    // moves, and this must not keep a phone's frame loop awake); the check is one querySelectorAll.
    let queued = false;
    const onMutation = () => {
        if (queued) {
            return;
        }
        queued = true;
        setTimeout(() => {
            queued = false;
            update();
        }, 120);
    };

    function start() {
        new MutationObserver(onMutation).observe(document.body, { childList: true, subtree: true });
        window.addEventListener('resize', onMutation);
        update();
    }

    if (document.body) {
        start();
    } else {
        document.addEventListener('DOMContentLoaded', start, { once: true });
    }
})();
