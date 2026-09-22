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
    // Moves exactly one page in `dir` from wherever the track currently rests, and reports it
    // straight away so the dots follow the gesture instead of waiting for the settle timer.
    // `swiped` is set because this IS user input: the correction the settle may add should glide.
    stepPage(el, state, dir) {
        const count = el.children ? el.children.length : 0;
        if (count === 0) {
            return;
        }
        const target = Math.min(count - 1, Math.max(0, this.currentPage(el) + dir));
        state.swiped = true;
        this.scrollToPage(el, target, true);
        if (target !== state.reported) {
            state.reported = target;
            this.report(state, target);
        }
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
    },

    // ---- settle handling -------------------------------------------------------------------
    //
    // Why this exists: the track is `scroll-snap-type: x mandatory`, so a swipe should always come
    // to rest on a page boundary. It sometimes doesn't, because a pending snap is CANCELLED by a
    // DOM/layout mutation inside the snap container while momentum scrolling is still running (on
    // WebKit the scroller then simply stops where it is). Nothing re-snapped it afterwards, and the
    // dots could be left on the page the last coalesced scroll event happened to see.
    //
    // So: don't react per scroll event. Wait until the container is actually STILL, then make one
    // authoritative correction — that covers a cancelled snap, sub-pixel rounding and a fractional
    // page width alike — and report the settled page to .NET exactly ONCE, so no re-render can
    // happen while a scroll is in flight.
    //
    // `scrollend` is the precise signal; Safari doesn't have it, so an idle timer runs alongside.
    // Both funnel into the same idempotent settle().
    //
    // The idle timer is the delicate one, because the browsers that need it (iOS Safari) are exactly
    // the touch tablets this runs on. A finger that drags and then simply HOLDS STILL stops emitting
    // scroll events while the gesture is very much still in progress, so a short idle timeout fires
    // mid-gesture and smooth-scrolls the track out from under the finger. Two guards:
    //   1. a pointer/touch that is currently DOWN suppresses the timer entirely — the gesture ends on
    //      pointerup/touchend, and that is when the idle countdown is (re)started;
    //   2. the countdown itself is well past a human pause, not the ~120 ms one that caused the yank.
    // `scrollend`, where it exists, still settles immediately; nothing waits on the timer there.
    observe(el, dotNet) {
        if (!el || el._bwCarousel) {
            return;
        }
        // Momentum scrolling continues after the finger lifts, so this is only the floor for a settle
        // that the idle timer — not scrollend — has to decide on its own.
        const idleMs = 450;
        const state = {
            dotNet: dotNet,
            locked: false,
            timer: 0,
            reported: -1,
            pointers: 0,
            // Whether the scroll this settle is correcting came from a FINGER. Only a genuine swipe
            // gets the smooth correction; a scroll nobody asked for — a panel switch calling
            // bwViewport.fitBox rewrites the viewport's aspect-ratio, which perturbs the mandatory
            // snap container — realigns instantly, so a button-initiated switch never plays the
            // swipe animation. markPage (a dot/arrow/deep-link move) clears it for the same reason.
            swiped: false,
        };
        state.arm = () => {
            if (state.timer) {
                clearTimeout(state.timer);
                state.timer = 0;
            }
            // A gesture is in progress: the user's finger decides where this lands, not a timer.
            if (state.pointers > 0) {
                return;
            }
            state.timer = setTimeout(state.settle, idleMs);
        };
        state.settle = () => {
            if (state.timer) {
                clearTimeout(state.timer);
                state.timer = 0;
            }
            // Edit mode freezes the track on purpose (scroll-snap-type: none, overflow hidden); a
            // correction then would scroll the edit surface out from under the user.
            if (state.locked || !el.isConnected || el.clientWidth === 0) {
                return;
            }
            // A settle raised while the finger is still down (a stray scrollend during a gesture)
            // must not move anything either; the release re-arms it.
            if (state.pointers > 0) {
                return;
            }
            const page = this.currentPage(el);
            const smooth = state.swiped;
            state.swiped = false;
            // Correct only when there is something to correct. Re-aiming at an offset we are already
            // on is not free on fractional device-pixel ratios: the smooth scroll lands a rounding
            // remainder away, which emits scroll events, which settle again — an oscillation that
            // never converges. One device pixel of slack ends it.
            if (Math.abs(el.scrollLeft - this.targetOffset(el, page)) > 1) {
                this.scrollToPage(el, page, smooth);
            }
            if (page !== state.reported) {
                state.reported = page;
                this.report(state, page);
            }
        };
        state.onScroll = () => {
            state.arm();
        };
        // ---- trackpad paging ------------------------------------------------------------------
        //
        // A trackpad flick is a user gesture just as much as a swipe, but the browser handles it
        // nothing like one. `scroll-snap-type: x mandatory` only commits a page once the scroller
        // has travelled past roughly half a slide, and a wheel burst doesn't accumulate: each small
        // deltaX is snapped straight back before the next arrives. Measured on the 5-page wall
        // carousel at 520px wide, a SINGLE 300px deltaX changed page, while ten 30px events (the
        // same 300px, the shape a real trackpad emits) changed nothing at all. That is the
        // "swipe reaaallly far" the user hit.
        //
        // So a horizontally dominant wheel no longer scrolls this container natively at all: we
        // cancel it and page it ourselves off an accumulator. That gives a threshold we control,
        // removes the springback entirely, and cannot fight the step we make. A vertically dominant
        // wheel is never cancelled — it still scrolls the slide (and the page) as before.
        //
        // Three guards keep it from feeling twitchy:
        //   * the accumulated horizontal travel must beat `wheelStepPx`, and must also dominate the
        //     accumulated VERTICAL travel, so a vertical scroll with a little sideways drift pages
        //     nothing however long it runs;
        //   * one commit latches the gesture: a trackpad's long tail of follow-up events is
        //     swallowed, so a gesture moves exactly one page;
        //   * a short idle gap ends the gesture (clears the accumulator and the latch), and a
        //     direction reversal restarts the count.
        const wheelStepPx = 40;
        const wheelIdleMs = 220;
        state.wheelX = 0;
        state.wheelY = 0;
        state.wheelLatched = false;
        state.wheelTimer = 0;
        state.wheelIdle = () => {
            state.wheelTimer = 0;
            state.wheelX = 0;
            state.wheelY = 0;
            state.wheelLatched = false;
        };
        state.onWheel = (e) => {
            // Edit mode freezes the track on purpose; the wheel must behave as it did before.
            if (state.locked) {
                return;
            }
            // deltaMode 1 is lines and 2 is pages (Firefox, some mice); normalise to pixels so the
            // threshold means the same thing everywhere.
            const scale = e.deltaMode === 1 ? 16 : (e.deltaMode === 2 ? el.clientWidth : 1);
            const dx = e.deltaX * scale;
            const dy = e.deltaY * scale;
            if (Math.abs(dx) > Math.abs(dy)) {
                if (e.cancelable) {
                    e.preventDefault();
                }
            }
            if (state.wheelTimer) {
                clearTimeout(state.wheelTimer);
            }
            state.wheelTimer = setTimeout(state.wheelIdle, wheelIdleMs);
            if (dx !== 0 && state.wheelX !== 0 && Math.sign(dx) !== Math.sign(state.wheelX)) {
                state.wheelX = 0;
            }
            state.wheelX += dx;
            state.wheelY += dy;
            if (state.wheelLatched || Math.abs(state.wheelX) < wheelStepPx) {
                return;
            }
            if (Math.abs(state.wheelX) <= Math.abs(state.wheelY) * 1.5) {
                return;
            }
            state.wheelLatched = true;
            this.stepPage(el, state, state.wheelX > 0 ? 1 : -1);
        };
        state.onPointerDown = () => {
            state.pointers++;
            state.swiped = true;
            if (state.timer) {
                clearTimeout(state.timer);
                state.timer = 0;
            }
        };
        state.onPointerUp = () => {
            state.pointers = Math.max(0, state.pointers - 1);
            state.arm();
        };
        el.addEventListener('scroll', state.onScroll, { passive: true });
        // NOT passive: a horizontally dominant wheel is cancelled and paged by hand (see onWheel).
        el.addEventListener('wheel', state.onWheel, { passive: false });
        // pointer* covers touch and mouse on everything current; the touch* pair is the fallback for
        // an engine without Pointer Events, and the counter tolerates both firing.
        if (window.PointerEvent) {
            el.addEventListener('pointerdown', state.onPointerDown, { passive: true });
            el.addEventListener('pointerup', state.onPointerUp, { passive: true });
            el.addEventListener('pointercancel', state.onPointerUp, { passive: true });
        } else {
            el.addEventListener('touchstart', state.onPointerDown, { passive: true });
            el.addEventListener('touchend', state.onPointerUp, { passive: true });
            el.addEventListener('touchcancel', state.onPointerUp, { passive: true });
        }
        if ('onscrollend' in window) {
            state.onScrollEnd = state.settle;
            el.addEventListener('scrollend', state.onScrollEnd, { passive: true });
        }
        el._bwCarousel = state;
    },
    report(state, page) {
        if (!state.dotNet) {
            return;
        }
        try {
            // Fire and forget: the circuit may already be gone during teardown, and a settle is not
            // worth failing over.
            const result = state.dotNet.invokeMethodAsync('CarouselSettled', page);
            if (result && typeof result.catch === 'function') {
                result.catch(() => { });
            }
        } catch (e) {
            /* circuit gone */
        }
    },
    // Keeps the JS side in step with the component's Locked (edit-mode) parameter. Locking mid-swipe
    // freezes the track wherever it is (the stylesheet drops scroll snapping while editing), so
    // unlocking settles once to put it back on a page boundary.
    setLocked(el, locked) {
        const state = el && el._bwCarousel;
        if (!state) {
            return;
        }
        const was = state.locked;
        state.locked = !!locked;
        if (was && !state.locked) {
            state.settle();
        }
    },
    // Lets a programmatic move (dot click, arrow key, deep link) tell the settle handler which page
    // is already reported, so the scroll it causes doesn't echo back to .NET.
    markPage(el, idx) {
        if (!el || !el._bwCarousel) {
            return;
        }
        el._bwCarousel.reported = idx;
        // The scroll it is about to cause is its own, not a swipe: any correction that follows must
        // not animate on top of it (see `swiped`).
        el._bwCarousel.swiped = false;
    },
    // ---- fold marker ----------------------------------------------------------------------
    //
    // A carousel page is its own vertical scroller, so the document reports no scroll at all and the
    // bottom edge of the slide looks exactly like the end of the page. The boulder list is 4+ slides
    // of cards deep, so it needs to SAY that it continues: the page carries `has-more` while it is
    // scrolled anywhere above its own bottom, and the stylesheet fades the last 48px.
    //
    // `child` is an element inside the page (the .boulder-section) — the page itself is created by
    // WallCarousel, so the caller can only hand us something within it.
    //
    // Deliberately no unobserve: both listeners and the observer are attached to nodes that die with
    // the page, and a detached node takes its listeners with it. Nothing here writes layout, so the
    // mandatory snap container is never perturbed — only a class that drives an opacity.
    watchFold(child) {
        const el = child && child.closest ? child.closest('.wall-page') : null;
        if (!el || el._bwFold) {
            return;
        }
        const state = {};
        state.apply = () => {
            // 2px of slack: a fractional device-pixel ratio leaves a sub-pixel remainder at the very
            // bottom that would otherwise keep the fade on forever.
            const more = el.scrollHeight - el.clientHeight - el.scrollTop > 2;
            if (more !== state.last) {
                state.last = more;
                el.classList.toggle('has-more', more);
            }
        };
        state.last = undefined;
        el.addEventListener('scroll', state.apply, { passive: true });
        if (window.ResizeObserver) {
            // The list changes height without scrolling: a filter, a new boulder, the archive
            // section opening. Watch both the page (viewport) and the content.
            state.ro = new ResizeObserver(state.apply);
            state.ro.observe(el);
            state.ro.observe(child);
        }
        el._bwFold = state;
        state.apply();
    },
    unobserve(el) {
        const state = el && el._bwCarousel;
        if (!state) {
            return;
        }
        if (state.timer) {
            clearTimeout(state.timer);
        }
        if (state.wheelTimer) {
            clearTimeout(state.wheelTimer);
        }
        el.removeEventListener('scroll', state.onScroll);
        el.removeEventListener('wheel', state.onWheel);
        el.removeEventListener('pointerdown', state.onPointerDown);
        el.removeEventListener('pointerup', state.onPointerUp);
        el.removeEventListener('pointercancel', state.onPointerUp);
        el.removeEventListener('touchstart', state.onPointerDown);
        el.removeEventListener('touchend', state.onPointerUp);
        el.removeEventListener('touchcancel', state.onPointerUp);
        if (state.onScrollEnd) {
            el.removeEventListener('scrollend', state.onScrollEnd);
        }
        state.dotNet = null;
        delete el._bwCarousel;
    }
};
