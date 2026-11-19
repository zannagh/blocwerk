/*
 * bwBrowseTier: whether the boulder browser's landing shows the whole list under its cards.
 *
 * pages.css shows that list on the desktop tier only: a window at least 900px wide that is not forced into the phone
 * column (html.bw-compact, theme.js). Hiding it with CSS alone would still render every boulder card on a phone, so
 * the wall page renders the list only once this says the tier is desktop, and again whenever that changes (a resized
 * window, the layout toggle). Until it has answered, the page renders no list.
 */
window.bwBrowseTier = (function (win, doc) {
    const DESKTOP = '(min-width: 900px)';
    const watchers = new Map();
    let next = 1;

    function isDesktop() {
        return win.matchMedia(DESKTOP).matches && !doc.documentElement.classList.contains('bw-compact');
    }

    /** Tells the component its tier now and on every change; returns an id for unwatch. */
    function watch(component) {
        const id = next++;
        let last = isDesktop();
        const report = () => {
            const now = isDesktop();
            if (now !== last) {
                last = now;
                component.invokeMethodAsync('SetDesktopTier', now).catch(() => { /* a closed circuit */ });
            }
        };
        const query = win.matchMedia(DESKTOP);
        query.addEventListener('change', report);
        const observer = new MutationObserver(report);
        observer.observe(doc.documentElement, { attributes: true, attributeFilter: ['class'] });
        watchers.set(id, () => {
            query.removeEventListener('change', report);
            observer.disconnect();
        });
        component.invokeMethodAsync('SetDesktopTier', last).catch(() => { /* a closed circuit */ });
        return id;
    }

    function unwatch(id) {
        const stop = watchers.get(id);
        if (stop) {
            watchers.delete(id);
            stop();
        }
    }

    return { isDesktop, watch, unwatch };
})(window, document);
