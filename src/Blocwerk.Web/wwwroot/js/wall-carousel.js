window.wallCarousel = {
    // First landing: jump straight to the target page with no animation, before the carousel is
    // revealed, so the very first frame the user sees is already the target page (no visible swipe
    // from page 1). The element stays visually hidden (.carousel-pending) until Blazor re-renders
    // after this runs, so the instant scrollLeft is applied while nothing is painted yet.
    initPage(el, idx) {
        if (!el) return;
        el.scrollLeft = idx * el.clientWidth;
    },
    scrollToPage(el, idx, smooth) {
        if (!el) return;
        el.scrollTo({ left: idx * el.clientWidth, behavior: smooth ? 'smooth' : 'auto' });
    },
    currentPage(el) {
        if (!el || el.clientWidth === 0) return 0;
        return Math.round(el.scrollLeft / el.clientWidth);
    }
};
