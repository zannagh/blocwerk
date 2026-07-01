window.wallCarousel = {
    scrollToPage(el, idx, smooth) {
        if (!el) return;
        el.scrollTo({ left: idx * el.clientWidth, behavior: smooth ? 'smooth' : 'auto' });
    },
    currentPage(el) {
        if (!el || el.clientWidth === 0) return 0;
        return Math.round(el.scrollLeft / el.clientWidth);
    }
};
