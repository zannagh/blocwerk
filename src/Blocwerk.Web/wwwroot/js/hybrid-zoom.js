/*
 * Hybrid zoom (experimental, HybridZoom:Enabled): once a panel viewer is zoomed in past the layer's
 * data-bw-hybrid-min-zoom, the visible part of the panel photo is swapped for a sharper close-up from the
 * wall's 3D capture photos. The server (WallPanelCloseUpEndpoint) picks the photo and warps it so it lies
 * exactly over a panel rectangle (X-Bw-Closeup-Rect); this script only places it there, inside the content
 * layer, so it zooms and pans with the photo and the hold overlay above it stays registered.
 *
 * Read-only and passive: it never handles a gesture (the layer takes no pointer events) and never touches
 * the viewport transform. It listens for the gesture having settled (scroll / wheel / pointer up, debounced)
 * and then measures the visible rectangle from the DOM, so it works with any zoom model and crop frame.
 * The request is padded around the visible rectangle, so small pans reuse the close-up already shown.
 */
(function () {
    'use strict';

    const PAD = 0.3;            // grow the request by this share of the visible size on each side
    const SETTLE_MS = 220;      // a gesture is over after this long without events
    const FADE_MS = 300;        // matches the CSS transition of .hybrid-closeup
    const MAX_PX = 2048;
    const GRID = 256;           // the server snaps to this grid too, so equal requests share a render
    const REZOOM = 1.6;         // a close-up fetched at zoom z is re-fetched sharper past z * REZOOM
    const DISABLING = ['no-model', 'stale-capture'];

    const states = new WeakMap();
    let timer = 0;

    function stateOf(layer) {
        let st = states.get(layer);
        if (!st || st.base !== layer.dataset.bwHybrid) {
            if (st) {
                reset(layer, st);
            }

            // misses: url → the on-volume spot it answered (or null), so a revisited view needs no request.
            st = { base: layer.dataset.bwHybrid, shown: null, pending: null, misses: new Map(), disabled: false, volume: null };
            states.set(layer, st);
        }

        return st;
    }

    function reset(layer, st) {
        if (st.pending) {
            st.pending.abort();
        }

        if (st.shown) {
            URL.revokeObjectURL(st.shown.url);
        }

        layer.replaceChildren();
        showVolume(layer, null);
    }

    /** The "Look underneath" link of the viewer (a Blazor-rendered empty slot outside the zooming content). */
    function showVolume(layer, spot) {
        const viewer = layer.closest('.panel-viewer');
        const slot = viewer && viewer.querySelector('.hybrid-volume-slot');
        if (!slot) {
            return;
        }

        if (!spot) {
            slot.hidden = true;
            closeInset(slot);
            return;
        }

        const href = slot.dataset.bwHybrid3d + '?at=' + encodeURIComponent(spot);
        let link = slot.querySelector('a.hybrid-volume-chip');
        if (!link) {
            link = document.createElement('a');
            link.className = 'hybrid-volume-chip';
            const label = document.createElement('span');
            label.textContent = 'Look underneath';
            link.append(label);
            link.addEventListener('click', openInset);
            slot.append(link);
        }

        link.href = href;
        link.dataset.spot = spot;
        link.title = 'This part of the wall is a volume: look underneath it in 3D';
        slot.hidden = false;
    }

    /** Shows the link while the visible view lies inside a view the server answered on-volume for. */
    function syncVolume(layer, st, vis) {
        showVolume(layer, st.volume && vis && contains(st.volume.rect, vis) ? st.volume.spot : null);
    }

    function viewportOf(layer) {
        // The full-screen stage renders the same layers inside a rotated world; its rectangles are not the photo's.
        return layer.closest('.bw-stage-overlay') ? null : layer.closest('.photo-viewport');
    }

    function zoomOf(viewport) {
        const model = viewport._bwModel;
        return model && typeof model.getZoom === 'function' ? model.getZoom() : 1;
    }

    /** The photo's visible part, normalised (x, y, w, h), and its size on screen in CSS px; null when off screen. */
    function visibleRect(viewport, img) {
        const r = img.getBoundingClientRect();
        const v = viewport.getBoundingClientRect();
        const x0 = Math.max(v.left, r.left), y0 = Math.max(v.top, r.top);
        const x1 = Math.min(v.right, r.right), y1 = Math.min(v.bottom, r.bottom);
        if (r.width <= 0 || r.height <= 0 || x1 <= x0 || y1 <= y0) {
            return null;
        }

        return {
            x: (x0 - r.left) / r.width, y: (y0 - r.top) / r.height,
            w: (x1 - x0) / r.width, h: (y1 - y0) / r.height,
            cssW: x1 - x0, cssH: y1 - y0,
        };
    }

    function padded(vis) {
        const snap = (value, up) => (up ? Math.ceil(value * GRID) : Math.floor(value * GRID)) / GRID;
        const x0 = snap(Math.max(0, vis.x - (vis.w * PAD)), false);
        const y0 = snap(Math.max(0, vis.y - (vis.h * PAD)), false);
        const x1 = snap(Math.min(1, vis.x + (vis.w * (1 + PAD))), true);
        const y1 = snap(Math.min(1, vis.y + (vis.h * (1 + PAD))), true);
        return { x: x0, y: y0, w: x1 - x0, h: y1 - y0 };
    }

    function contains(outer, inner) {
        const e = 1e-4;
        return inner.x >= outer.x - e && inner.y >= outer.y - e &&
            inner.x + inner.w <= outer.x + outer.w + e && inner.y + inner.h <= outer.y + outer.h + e;
    }

    function parseRect(text) {
        const p = (text || '').split(',').map(Number);
        return p.length === 4 && p.every(Number.isFinite) && p[2] > 0 && p[3] > 0 ? { x: p[0], y: p[1], w: p[2], h: p[3] } : null;
    }

    function urlFor(base, rect, px, aspect) {
        const q = 'x=' + rect.x.toFixed(6) + '&y=' + rect.y.toFixed(6) + '&w=' + rect.w.toFixed(6) + '&h=' + rect.h.toFixed(6) +
            '&px=' + px + '&ar=' + aspect.toFixed(3);
        return base + (base.indexOf('?') >= 0 ? '&' : '?') + q;
    }

    function hide(st) {
        if (st.pending) {
            st.pending.abort();
            st.pending = null;
        }

        const shown = st.shown;
        if (!shown) {
            return;
        }

        st.shown = null;
        shown.el.classList.remove('shown');
        setTimeout(function () {
            shown.el.remove();
            URL.revokeObjectURL(shown.url);
        }, FADE_MS);
    }

    function place(layer, st, rect, url, zoom, photo) {
        const el = document.createElement('img');
        el.className = 'hybrid-closeup' + (photo.classList.contains('boulder-photo') ? ' boulder-photo' : '');
        el.alt = '';
        el.draggable = false;
        el.style.left = (rect.x * 100) + '%';
        el.style.top = (rect.y * 100) + '%';
        el.style.width = (rect.w * 100) + '%';
        el.style.height = (rect.h * 100) + '%';
        el.src = url;
        const ready = el.decode ? el.decode() : Promise.resolve();
        ready.catch(function () { /* shown anyway once loaded */ }).then(function () {
            if (stateOf(layer) !== st) {
                URL.revokeObjectURL(url);
                return;
            }

            const old = st.shown;
            st.shown = { rect: rect, zoom: zoom, el: el, url: url };
            layer.appendChild(el);
            requestAnimationFrame(function () { el.classList.add('shown'); });
            if (old) {
                setTimeout(function () {
                    old.el.remove();
                    URL.revokeObjectURL(old.url);
                }, FADE_MS);
            }
        });
    }

    function request(layer, st, vis, zoom, photo) {
        const rect = padded(vis);
        const dpr = window.devicePixelRatio || 1;
        const px = Math.min(MAX_PX, Math.round(Math.max(vis.cssW, vis.cssH) * (1 + (2 * PAD)) * dpr));
        const aspect = (rect.w * photo.naturalWidth) / (rect.h * photo.naturalHeight);
        const url = urlFor(st.base, rect, px, aspect);
        if (st.misses.has(url)) {
            const spot = st.misses.get(url);
            st.volume = spot ? { rect: rect, spot: spot } : null;
            syncVolume(layer, st, vis);
            return;
        }

        if (st.pending && st.pending.url === url) {
            return;
        }

        if (st.pending) {
            st.pending.abort();
        }

        const ctl = new AbortController();
        ctl.url = url;
        st.pending = ctl;
        fetch(url, { credentials: 'same-origin', signal: ctl.signal }).then(function (res) {
            // A volume in the view: either the whole answer (204 on-volume) or cut out of the close-up (200).
            const spot = res.status === 200 || res.headers.get('X-Bw-Closeup-Status') === 'on-volume'
                ? res.headers.get('X-Bw-Closeup-Spot')
                : null;
            st.volume = spot ? { rect: rect, spot: spot } : null;
            syncVolume(layer, st, vis);
            if (res.status === 200) {
                const got = parseRect(res.headers.get('X-Bw-Closeup-Rect')) || rect;
                return res.blob().then(function (blob) {
                    if (st.pending === ctl) {
                        st.pending = null;
                        place(layer, st, got, URL.createObjectURL(blob), zoom, photo);
                    }
                });
            }

            st.pending = st.pending === ctl ? null : st.pending;
            const status = res.headers.get('X-Bw-Closeup-Status');
            if (res.status === 404 || DISABLING.indexOf(status) >= 0) {
                st.disabled = true;
            } else if (res.status === 204 && st.misses.size < 200) {
                st.misses.set(url, spot);
            }

            return null;
        }).catch(function () {
            st.pending = st.pending === ctl ? null : st.pending;
        });
    }

    function evaluate(layer) {
        const st = stateOf(layer);
        const viewport = viewportOf(layer);
        const photo = layer.parentElement && layer.parentElement.querySelector('img.wall-photo');
        if (st.disabled || !viewport || !photo || !(photo.naturalWidth > 0)) {
            return;
        }

        const zoom = zoomOf(viewport);
        const minZoom = parseFloat(layer.dataset.bwHybridMinZoom) || 2.5;
        const vis = zoom >= minZoom ? visibleRect(viewport, photo) : null;
        syncVolume(layer, st, vis);
        if (!vis) {
            hide(st);
            return;
        }

        if (st.shown && contains(st.shown.rect, vis) && zoom <= st.shown.zoom * REZOOM) {
            return;
        }

        request(layer, st, vis, zoom, photo);
    }

    function settle() {
        clearTimeout(timer);
        timer = setTimeout(function () {
            document.querySelectorAll('.hybrid-zoom-layer[data-bw-hybrid]').forEach(evaluate);
        }, SETTLE_MS);
    }

    const opts = { capture: true, passive: true };
    ['scroll', 'wheel', 'pointerup', 'touchend', 'keyup', 'dblclick'].forEach(function (type) {
        document.addEventListener(type, settle, opts);
    });
    window.addEventListener('resize', settle, { passive: true });

    // A panel swap rewrites the layer's url on the same node: drop the old panel's close-up at once, before
    // the next gesture, so it can never sit over the new photo.
    new MutationObserver(function (records) {
        records.forEach(function (r) {
            if (r.target.classList && r.target.classList.contains('hybrid-zoom-layer')) {
                stateOf(r.target);
            }
        });
    }).observe(document.documentElement, { subtree: true, attributes: true, attributeFilter: ['data-bw-hybrid'] });

    // The volume inset (VolumeInset3D.razor): the chip opens it through .NET when the component registered
    // itself on the slot; without that (no circuit) the chip stays a plain link to the 3D page.
    function openInset(e) {
        const slot = e.currentTarget.closest('.hybrid-volume-slot');
        const inset = slot && slot._bwInset;
        if (!inset || e.ctrlKey || e.metaKey || e.shiftKey || e.button !== 0) {
            return;
        }

        e.preventDefault();
        inset.ref.invokeMethodAsync('OpenAsync', e.currentTarget.dataset.spot).catch(function () { /* circuit gone */ });
    }

    function closeInset(slot) {
        const inset = slot._bwInset;
        if (inset && inset.open) {
            inset.ref.invokeMethodAsync('CloseFromJs').catch(function () { /* circuit gone */ });
        }
    }

    // One open inset per page: its Escape / tap-outside listeners.
    let watched = null;

    function unwatchInset() {
        if (!watched) {
            return;
        }

        document.removeEventListener('pointerdown', watched.down, true);
        document.removeEventListener('keydown', watched.key);
        if (watched.slot._bwInset) {
            watched.slot._bwInset.open = false;
        }

        watched = null;
    }

    function watchInset(insetEl, slot, ref) {
        unwatchInset();
        if (!insetEl || !slot || !slot._bwInset) {
            return;
        }

        const close = function () { ref.invokeMethodAsync('CloseFromJs').catch(function () { /* circuit gone */ }); };
        const down = function (e) {
            if (!insetEl.contains(e.target) && !slot.contains(e.target)) {
                close();
            }
        };
        // Bubbling, so the 3D view's own Escape (clearing a picked hold) goes first and a second one closes.
        const key = function (e) {
            if (e.key === 'Escape' && !e.defaultPrevented) {
                e.preventDefault();
                close();
            }
        };
        document.addEventListener('pointerdown', down, true);
        document.addEventListener('keydown', key);
        slot._bwInset.open = true;
        watched = { slot: slot, down: down, key: key };
    }

    /** Registers the inset on its slot; true on a light device, which gets the schematic inset (no splat download). */
    function registerInset(slot, ref) {
        if (slot && slot.dataset) {
            slot._bwInset = { ref: ref, open: false };
        }

        const memory = navigator.deviceMemory || 8;
        const saveData = navigator.connection && navigator.connection.saveData;
        return memory < 4 || !!saveData;
    }

    window.bwHybridZoom = { refresh: settle, registerInset: registerInset, watchInset: watchInset, unwatchInset: unwatchInset };
})();
