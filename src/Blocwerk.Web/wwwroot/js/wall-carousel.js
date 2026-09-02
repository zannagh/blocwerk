window.wallCarousel = {
    // First landing: jump straight to the target page with no animation, before the carousel is
    // revealed, so the very first frame the user sees is already the target page (no visible swipe
    // from page 1).
    //
    // Returns a promise that resolves only once the offset is actually applied, so the caller keeps
    // the carousel hidden (.carousel-pending) until then.
    //
    // Two things must be true for this to land exactly on `idx`:
    //   1. The container must be laid out and the flex track wide enough to hold page `idx`. We wait
    //      for that — however many frames it takes, up to a generous cap — rather than giving up
    //      after a fixed handful and positioning against a half-built track.
    //   2. The positioning must be INSTANT, or `scroll-snap-type: x mandatory` snaps to whatever near
    //      page an animation has reached. The stylesheet deliberately sets no scroll-behavior, and we
    //      pin it to `auto` inline anyway so a future rule can't reintroduce the animation here.
    initPage(el, idx) {
        if (!el) {
            return Promise.resolve();
        }
        return new Promise((resolve) => {
            const prevBehavior = el.style.scrollBehavior;
            el.style.scrollBehavior = 'auto';
            const done = () => {
                el.style.scrollBehavior = prevBehavior;
                resolve();
            };
            // ~4s at 60fps. Only a container that never gets laid out reaches this, and then the
            // carousel is revealed wherever it is rather than staying hidden forever.
            const maxFrames = 240;
            let frames = 0;
            const laidOut = () => el.clientWidth > 0 && el.scrollWidth >= (idx + 1) * el.clientWidth - 1;
            // Assign, then confirm next frame that the offset actually stuck: a late reflow can move
            // the track after we wrote scrollLeft, and revealing then would show the wrong page.
            const position = () => {
                const target = this.targetOffset(el, idx);
                el.scrollLeft = target;
                requestAnimationFrame(() => {
                    frames++;
                    const settled = Math.abs(el.scrollLeft - this.targetOffset(el, idx)) <= 2;
                    if (settled || frames >= maxFrames) {
                        done();
                        return;
                    }
                    position();
                });
            };
            const settle = () => {
                frames++;
                if (laidOut()) {
                    position();
                    return;
                }
                if (frames >= maxFrames) {
                    done();
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
        // `behavior: 'auto'` would resolve to the element's CSS scroll-behavior, so a non-smooth
        // caller asks for 'instant' explicitly to guarantee a jump whatever the stylesheet says.
        el.scrollTo({ left: this.targetOffset(el, idx), behavior: smooth ? 'smooth' : 'instant' });
    },
    // Nearest page by MEASURED offset, so the dots agree with what initPage/scrollToPage aimed at
    // even when a page isn't exactly clientWidth wide (a uniform-width estimate drifts on those).
    currentPage(el) {
        if (!el || el.clientWidth === 0) return 0;
        const count = el.children ? el.children.length : 0;
        if (count === 0) return 0;
        let best = 0;
        let bestDistance = Infinity;
        for (let i = 0; i < count; i++) {
            const distance = Math.abs(this.targetOffset(el, i) - el.scrollLeft);
            if (distance < bestDistance) {
                bestDistance = distance;
                best = i;
            }
        }
        return best;
    }
};
