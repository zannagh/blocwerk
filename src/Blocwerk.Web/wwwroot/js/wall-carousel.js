window.wallCarousel = {
    // First landing: jump straight to the target page with no animation, before the carousel is
    // revealed, so the very first frame the user sees is already the target page (no visible swipe
    // from page 1).
    //
    // Returns a promise that resolves only once the offset is actually applied, so the caller keeps
    // the carousel hidden (.carousel-pending) until then. The reason this is deferred and re-asserted
    // across a few animation frames: at first paint the carousel's async-loaded child pages (wall
    // photo, boulders) may not have their full laid-out width yet, so a raw
    // `scrollLeft = idx * clientWidth` set too early is clamped toward 0 — and the reveal then shows
    // page 1 instead of the intended centre page. We wait until the flex track is wide enough to hold
    // page `idx`, position off the real target page element, then reveal.
    initPage(el, idx) {
        if (!el) {
            return Promise.resolve();
        }
        return new Promise((resolve) => {
            let frames = 0;
            const settle = () => {
                frames++;
                const w = el.clientWidth;
                // Track wide enough to actually scroll to page `idx`? (Give up after ~30 frames so a
                // degenerate layout never leaves the carousel hidden forever.)
                const ready = w > 0 && el.scrollWidth >= (idx + 1) * w - 1;
                if (ready || frames >= 30) {
                    el.scrollLeft = this.targetOffset(el, idx);
                    // Re-assert once more next frame in case a late reflow nudges the track, then reveal.
                    requestAnimationFrame(() => {
                        el.scrollLeft = this.targetOffset(el, idx);
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
        el.scrollTo({ left: this.targetOffset(el, idx), behavior: smooth ? 'smooth' : 'auto' });
    },
    currentPage(el) {
        if (!el || el.clientWidth === 0) return 0;
        return Math.round(el.scrollLeft / el.clientWidth);
    }
};
