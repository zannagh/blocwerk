/*
 * bwCarouselNav: the wall carousel and the browser history.
 *
 * 1. Landing before the first paint. A carousel arrives hidden (.carousel-pending) with the page it opens on in
 *    data-initial-page. The component used to reveal it only after its first render had come back from the browser
 *    and several more interop round-trips, so a back press showed a blank page (and page 1's dot) first. A
 *    MutationObserver positions it the moment the DOM batch lands, before the browser paints, and reveals it there.
 *    One that is not laid out yet is left to wallCarousel.initPage, which waits for layout.
 *
 * 2. The URL names the page. data-views lists each page's ?view= value ("" for the page a bare URL opens), and every
 *    settled or programmatic page change replaces the current entry's URL with it. A boulder opened from the list
 *    therefore sits on top of "?view=boulders", and back returns to the list. A navigation that lands on a carousel
 *    which is still on screen (the Home tab while the URL says ?view=boulders) moves it to the page the new URL names.
 *
 * 3. Each page's own scroll offset is remembered per history entry (sessionStorage, keyed by an id kept in
 *    history.state) and put back when back/forward returns to that entry.
 */
(function () {
    'use strict';

    const PENDING = '.wall-carousel.carousel-pending[data-initial-page]';
    const STORE = 'bwCarouselScroll';
    const MAX_ENTRIES = 30;
    const RESTORE_WINDOW_MS = 10000;
    const RESTORE_TRY_MS = 2500;
    const CANCEL_EVENTS = ['pointerdown', 'touchstart', 'wheel', 'keydown'];

    // The entry a back/forward traversal landed on, until a carousel on it has been restored.
    let restoreEntry = null;
    let restoreUntil = 0;

    function views(el) {
        const value = el && el.dataset ? el.dataset.views : undefined;
        return typeof value === 'string' ? value.split('|') : null;
    }

    function pageForLocation(el) {
        const list = views(el);
        if (!list) {
            return null;
        }
        const view = (new URLSearchParams(location.search).get('view') || '').toLowerCase();
        const page = list.indexOf(view);
        return page >= 0 ? page : list.indexOf('');
    }

    function entryId(create) {
        const state = history.state && typeof history.state === 'object' ? history.state : null;
        if (state && state.bwEntry) {
            return state.bwEntry;
        }
        if (!create) {
            return null;
        }
        const id = Math.random().toString(36).slice(2) + Date.now().toString(36);
        try {
            // Keep whatever Blazor stores in the entry (its _index); only add the id.
            history.replaceState(Object.assign({}, state, { bwEntry: id }), '');
        } catch (e) {
            return null;
        }
        return id;
    }

    function readStore() {
        try {
            return JSON.parse(sessionStorage.getItem(STORE) || '{}') || {};
        } catch (e) {
            return {};
        }
    }

    function writeStore(store) {
        try {
            const ids = Object.keys(store);
            if (ids.length > MAX_ENTRIES) {
                ids.sort((a, b) => (store[a].at || 0) - (store[b].at || 0));
                ids.slice(0, ids.length - MAX_ENTRIES).forEach(id => delete store[id]);
            }
            sessionStorage.setItem(STORE, JSON.stringify(store));
        } catch (e) {
            /* storage blocked: scroll positions are a convenience */
        }
    }

    // The carousel belongs to the URL it landed on. A settle that fires after a link already pushed the next page
    // must not rewrite THAT entry.
    function stillHome(el) {
        return el.isConnected && el._bwNavPath === location.pathname;
    }

    function pageChanged(el, page) {
        try {
            const list = views(el);
            if (!list || page < 0 || page >= list.length || !stillHome(el)) {
                return;
            }
            const url = new URL(location.href);
            if (list[page]) {
                url.searchParams.set('view', list[page]);
            } else {
                url.searchParams.delete('view');
            }
            if (url.href !== location.href) {
                history.replaceState(history.state, '', url.href);
                if (window.bwNav && window.bwNav.replaced) {
                    window.bwNav.replaced();
                }
            }
        } catch (e) {
            /* never break a swipe over the URL */
        }
    }

    function restore(el) {
        const id = entryId(false);
        if (!restoreEntry || id !== restoreEntry || Date.now() > restoreUntil) {
            return;
        }
        restoreEntry = null;
        const saved = readStore()[id];
        if (!saved || saved.path !== location.pathname || !saved.tops) {
            return;
        }

        // The page may still grow (images, a late render): keep applying until the offset holds, the page cannot get
        // there, or the user takes over.
        let cancelled = false;
        const cancel = () => { cancelled = true; };
        CANCEL_EVENTS.forEach(type => el.addEventListener(type, cancel, { passive: true, capture: true, once: true }));
        const until = performance.now() + RESTORE_TRY_MS;
        const apply = () => {
            if (cancelled || !el.isConnected) {
                return;
            }
            let pending = false;
            Object.keys(saved.tops).forEach(key => {
                const page = el.children[Number(key)];
                const top = saved.tops[key];
                if (!page) {
                    return;
                }
                if (Math.abs(page.scrollTop - top) > 1) {
                    page.scrollTop = top;
                }
                if (Math.abs(page.scrollTop - top) > 1) {
                    pending = true;
                }
            });
            if (pending && performance.now() < until) {
                requestAnimationFrame(apply);
            }
        };
        apply();
    }

    // Idempotent per element: called by the observer below and by initPage once it has positioned a carousel.
    function landed(el) {
        if (!el || el._bwNavLanded) {
            return;
        }
        el._bwNavLanded = true;
        el._bwNavPath = location.pathname;
        restore(el);
    }

    function land(el) {
        if (el.clientWidth === 0 || !window.wallCarousel) {
            return;
        }
        const idx = Number(el.dataset.initialPage);
        if (!(idx >= 0) || !el.children || el.children.length <= idx) {
            return;
        }
        const prev = el.style.scrollBehavior;
        el.style.scrollBehavior = 'auto';
        const target = window.wallCarousel.targetOffset(el, idx);
        el.scrollLeft = target;
        el.style.scrollBehavior = prev;
        if (Math.abs(el.scrollLeft - target) > 2) {
            // Not settled into this geometry yet; initPage's frame loop finishes the job.
            return;
        }
        el.classList.remove('carousel-pending');
        landed(el);
    }

    function landPending() {
        document.querySelectorAll(PENDING).forEach(land);
    }

    // A navigation that kept the carousel (same wall, another ?view=, or one the URL replace made look different)
    // moves it to the page the URL names.
    function followLocation() {
        document.querySelectorAll('.wall-carousel[data-views]').forEach(el => {
            const state = el._bwCarousel;
            if (!state || el.classList.contains('carousel-pending')) {
                return;
            }
            el._bwNavPath = location.pathname;
            const page = pageForLocation(el);
            if (page === null || page < 0 || page === window.wallCarousel.currentPage(el)) {
                return;
            }
            window.wallCarousel.markPage(el, page);
            window.wallCarousel.scrollToPage(el, page, false);
            window.wallCarousel.report(state, page);
        });
    }

    let saveTimer = 0;
    const pendingTops = new Map();

    function onScroll(event) {
        const page = event.target;
        const el = page && page.parentElement;
        if (!el || !el.classList || !el.classList.contains('wall-carousel') || !page.classList.contains('wall-page')
            || !el._bwNavLanded || !stillHome(el)) {
            return;
        }
        // The entry is decided now, while it is certainly the one being scrolled; the write may come later.
        const id = entryId(true);
        if (!id) {
            return;
        }
        const index = Array.prototype.indexOf.call(el.children, page);
        pendingTops.set(id + '|' + index, { id, index, top: Math.round(page.scrollTop), path: location.pathname });
        clearTimeout(saveTimer);
        saveTimer = setTimeout(flush, 150);
    }

    function flush() {
        clearTimeout(saveTimer);
        if (pendingTops.size === 0) {
            return;
        }
        const store = readStore();
        pendingTops.forEach(({ id, index, top, path }) => {
            const entry = store[id] && store[id].path === path ? store[id] : { path, tops: {} };
            entry.tops[index] = top;
            entry.at = Date.now();
            store[id] = entry;
        });
        pendingTops.clear();
        writeStore(store);
    }

    function install() {
        try {
            new MutationObserver(landPending).observe(document.documentElement, { childList: true, subtree: true });
            landPending();
            document.addEventListener('scroll', onScroll, { capture: true, passive: true });
            // A tap that navigates writes the offsets before the next page exists.
            document.addEventListener('click', flush, { capture: true });
            window.addEventListener('pagehide', flush);
            window.addEventListener('popstate', () => {
                flush();
                restoreEntry = entryId(false);
                restoreUntil = Date.now() + RESTORE_WINDOW_MS;
            });

            // Blazor.start() runs later (blazor-boot.js): attach the enhancedload hook once the API exists.
            let tries = 0;
            const timer = setInterval(() => {
                tries++;
                if (window.Blazor && window.Blazor.addEventListener) {
                    window.Blazor.addEventListener('enhancedload', followLocation);
                    clearInterval(timer);
                } else if (tries > 100) {
                    clearInterval(timer);
                }
            }, 100);
        } catch (e) {
            /* the carousel still works without any of this */
        }
    }

    window.bwCarouselNav = { landed, pageChanged, pageForLocation };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', install);
    } else {
        install();
    }
})();
