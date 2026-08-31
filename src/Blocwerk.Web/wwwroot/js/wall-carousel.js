window.wallCarousel = {
    // First landing: jump straight to the target page with no animation, before the carousel is
    // revealed, so the very first frame the user sees is already the target page (no visible swipe
    // from page 1).
    //
    // Returns a promise that resolves only once the offset is actually applied, so the caller keeps
    // the carousel hidden (.carousel-pending) until then.
    //
    // Two things must be true for this to land exactly on `idx`:
    //   1. The flex track must already be wide enough to hold page `idx`. We wait for that and then
    //      measure the real page element (targetOffset) rather than assuming a uniform clientWidth.
    //   2. The positioning must be INSTANT. The stylesheet sets `scroll-behavior: smooth` on
    //      `.wall-carousel`, which means a plain `el.scrollLeft = x` assignment ANIMATES instead of
    //      jumping. If we reveal mid-animation, `scroll-snap-type: x mandatory` snaps to whatever
    //      near page the animation has reached in those few frames (e.g. page 1 instead of 3). So we
    //      override scroll-behavior to `auto` inline while positioning, then restore it so ordinary
    //      user-driven navigation stays smooth afterward.
    initPage(el, idx) {
        if (!el) {
            return Promise.resolve();
        }
        return new Promise((resolve) => {
            const prevBehavior = el.style.scrollBehavior;
            el.style.scrollBehavior = 'auto';
            let frames = 0;
            const settle = () => {
                frames++;
                const w = el.clientWidth;
                // Track wide enough to actually scroll to page `idx`? (Give up after ~30 frames so a
                // degenerate layout never leaves the carousel hidden forever.)
                const ready = w > 0 && el.scrollWidth >= (idx + 1) * w - 1;
                if (ready || frames >= 30) {
                    el.scrollLeft = this.targetOffset(el, idx);
                    // Re-assert once more next frame in case a late reflow nudges the track, then
                    // restore smooth behavior and reveal.
                    requestAnimationFrame(() => {
                        el.scrollLeft = this.targetOffset(el, idx);
                        el.style.scrollBehavior = prevBehavior;
                        resolve();
                    });
                    return;
                }
                requestAnimationFrame(settle);
            };
            requestAnimationFrame(settle);
        });
    },
    // Real horizontal offset of page `idx` inside the scroll container, measured from the actual page
    // element so it stays correct even if a page isn't exactly clientWidth wide. Falls back to the
    // uniform-width estimate if the child isn't present yet.
    targetOffset(el, idx) {
        const child = el.children && el.children[idx];
        if (child) {
            return Math.round(child.getBoundingClientRect().left - el.getBoundingClientRect().left + el.scrollLeft);
        }
        return idx * el.clientWidth;
    },
    scrollToPage(el, idx, smooth) {
        if (!el) return;
        // `behavior: 'auto'` would resolve to the element's CSS scroll-behavior (smooth here), so a
        // non-smooth caller must ask for 'instant' explicitly to actually jump.
        el.scrollTo({ left: this.targetOffset(el, idx), behavior: smooth ? 'smooth' : 'instant' });
    },
    currentPage(el) {
        if (!el || el.clientWidth === 0) return 0;
        return Math.round(el.scrollLeft / el.clientWidth);
    }
};
