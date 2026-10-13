/*
 * Outlines on demand for the wall view. The panel photo is shown without the hold overlay; this finds
 * the hold under a tap in the browser, from a compact list sent once per panel (HoldTapList.cs), and
 * draws only that hold's outline — plus, darker, the holds that overlap it — into svg.hold-tap-layer.
 * No server round-trip per tap and no SVG node per hold.
 *
 * An item is { x, y, r, p, h, f, c, n, u, o }: centre and radius, outline and holes as flat x,y lists,
 * foot, colour, colour name, boulders using it, positions of the overlapping holds. Coordinates are in
 * 1/10000 of the photo; the layer's viewBox is 0-100.
 */
(function () {
    'use strict';

    const SCALE = 10000;
    const NS = 'http://www.w3.org/2000/svg';
    const SHOW_ALL_KEY = 'blocwerk-show-all-holds';
    const TAP_MOVE_PX = 8;    // further than this and the press was a pan
    const TAP_MS = 500;       // longer than this and it was a long press (the magnifier)
    const NEAR_PX = 10;       // a tap this close to a hold still picks it
    const LABEL_FLIP = 0.12;  // a hold this near the top gets its label underneath

    const states = new WeakMap();

    function v(units) {
        return (units / 100).toFixed(2);
    }

    function ringPath(ring) {
        let d = '';
        for (let i = 0; i < ring.length; i += 2) {
            d += (i === 0 ? 'M' : ' L') + v(ring[i]) + ',' + v(ring[i + 1]);
        }

        return d + ' Z';
    }

    function shapeNode(item) {
        let node;
        if (item.p) {
            node = document.createElementNS(NS, 'path');
            node.setAttribute('d', [item.p].concat(item.h || []).map(ringPath).join(' '));
        } else if (item.f) {
            node = document.createElementNS(NS, 'rect');
            node.setAttribute('x', v(item.x - item.r));
            node.setAttribute('y', v(item.y - item.r));
            node.setAttribute('width', v(item.r * 2));
            node.setAttribute('height', v(item.r * 2));
            node.setAttribute('rx', '0.3');
        } else {
            node = document.createElementNS(NS, 'circle');
            node.setAttribute('cx', v(item.x));
            node.setAttribute('cy', v(item.y));
            node.setAttribute('r', v(item.r));
        }

        return node;
    }

    function shine(item, dark) {
        const group = document.createElementNS(NS, 'g');
        group.setAttribute('class', dark ? 'hold-shine dark' : 'hold-shine');
        if (item.c) {
            group.style.setProperty('--hold-c', item.c);
        }

        (dark ? ['case', 'main'] : ['glow', 'case', 'main', 'light']).forEach(function (layer) {
            const g = document.createElementNS(NS, 'g');
            g.setAttribute('class', 'hold-shine-' + layer);
            g.appendChild(shapeNode(item));
            group.appendChild(g);
        });
        return group;
    }

    function box(item) {
        if (!item.p) {
            return { minX: item.x - item.r, maxX: item.x + item.r, minY: item.y - item.r, maxY: item.y + item.r };
        }

        const b = { minX: Infinity, maxX: -Infinity, minY: Infinity, maxY: -Infinity };
        for (let i = 0; i < item.p.length; i += 2) {
            b.minX = Math.min(b.minX, item.p[i]);
            b.maxX = Math.max(b.maxX, item.p[i]);
            b.minY = Math.min(b.minY, item.p[i + 1]);
            b.maxY = Math.max(b.maxY, item.p[i + 1]);
        }

        return b;
    }

    function inRing(ring, x, y) {
        let inside = false;
        for (let i = 0, j = ring.length - 2; i < ring.length; j = i, i += 2) {
            const xi = ring[i], yi = ring[i + 1], xj = ring[j], yj = ring[j + 1];
            if ((yi > y) !== (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) {
                inside = !inside;
            }
        }

        return inside;
    }

    function contains(item, x, y) {
        if (item.p) {
            return inRing(item.p, x, y);
        }

        const dx = x - item.x;
        const dy = y - item.y;
        return item.f ? Math.abs(dx) <= item.r && Math.abs(dy) <= item.r : dx * dx + dy * dy <= item.r * item.r;
    }

    /** Screen distance from the point to the hold's edge (its outline's nearest corner), in pixels. */
    function distancePx(item, x, y, sx, sy) {
        if (!item.p) {
            return Math.hypot((x - item.x) * sx, (y - item.y) * sy) - item.r * Math.min(sx, sy);
        }

        let best = Infinity;
        for (let i = 0; i < item.p.length; i += 2) {
            best = Math.min(best, Math.hypot((x - item.p[i]) * sx, (y - item.p[i + 1]) * sy));
        }

        return best;
    }

    /**
     * The hold at a point: the SMALLEST one containing it (so a hold sitting on a larger one can be
     * picked), else the nearest within NEAR_PX on screen. -1 for empty wall. Exposed for tests.
     */
    function findAt(items, x, y, sx, sy) {
        let hit = -1;
        let hitArea = Infinity;
        let near = -1;
        let nearPx = NEAR_PX;
        items.forEach(function (item, i) {
            const b = item.b || (item.b = box(item));
            const padX = NEAR_PX / sx;
            const padY = NEAR_PX / sy;
            if (x < b.minX - padX || x > b.maxX + padX || y < b.minY - padY || y > b.maxY + padY) {
                return;
            }

            if (contains(item, x, y)) {
                const area = (b.maxX - b.minX) * (b.maxY - b.minY);
                if (area < hitArea) {
                    hit = i;
                    hitArea = area;
                }
            } else {
                const d = distancePx(item, x, y, sx, sy);
                if (d <= nearPx) {
                    near = i;
                    nearPx = d;
                }
            }
        });
        return hit >= 0 ? hit : near;
    }

    function labelText(item) {
        const parts = [];
        if (item.n) {
            parts.push(item.n);
        }

        if (item.u === 0) {
            parts.push('not used by a boulder');
        } else if (item.u > 0) {
            parts.push('used by ' + item.u + (item.u === 1 ? ' boulder' : ' boulders'));
        }

        if (item.o && item.o.length) {
            parts.push(item.o.length + ' overlapping');
        }

        return parts.join(' · ');
    }

    function showLabel(label, item) {
        if (!label) {
            return;
        }

        const text = item ? labelText(item) : '';
        label.hidden = !text;
        if (!text) {
            return;
        }

        const b = item.b || (item.b = box(item));
        const below = b.minY / SCALE < LABEL_FLIP;
        label.classList.toggle('below', below);
        label.style.top = ((below ? b.maxY : b.minY) / 100).toFixed(2) + '%';
        label.style.setProperty('--hold-c', item.c || '');
        label.replaceChildren();
        const dot = document.createElement('span');
        dot.className = 'hold-tap-dot';
        label.appendChild(dot);
        label.appendChild(document.createTextNode(text));

        // Centred on the hold, but kept inside the photo: half the label's width, in percent of the photo.
        const photo = label.parentElement.getBoundingClientRect().width;
        const half = photo > 0 ? Math.min(49, label.getBoundingClientRect().width / photo * 50 + 1) : 12;
        label.style.left = Math.min(100 - half, Math.max(half, (b.minX + b.maxX) / 2 / 100)).toFixed(2) + '%';
    }

    function draw(viewport, state) {
        const layer = viewport.querySelector('svg.hold-tap-layer');
        if (!layer) {
            return;
        }

        const item = state.selected >= 0 ? state.items[state.selected] : null;
        layer.replaceChildren();
        if (item) {
            (item.o || []).forEach(function (i) {
                if (state.items[i]) {
                    layer.appendChild(shine(state.items[i], true));
                }
            });
            layer.appendChild(shine(item, false));
        }

        showLabel(viewport.querySelector('.hold-tap-label'), item);
    }

    function onTap(viewport, state, e) {
        const layer = viewport.querySelector('svg.hold-tap-layer');
        if (!layer) {
            return;
        }

        const rect = layer.getBoundingClientRect();
        if (!(rect.width > 0 && rect.height > 0)) {
            return;
        }

        const x = (e.clientX - rect.left) / rect.width * SCALE;
        const y = (e.clientY - rect.top) / rect.height * SCALE;
        state.selected = findAt(state.items, x, y, rect.width / SCALE, rect.height / SCALE);
        draw(viewport, state);
    }

    function bind(viewport, state) {
        // Capture phase: the pan engine may stop these on their way up. Nothing is prevented here, so
        // panning, pinching, the swipe to the next panel and the long-press magnifier are untouched.
        viewport.addEventListener('pointerdown', function (e) {
            state.down = e.isPrimary && e.button === 0 ? { id: e.pointerId, x: e.clientX, y: e.clientY, t: e.timeStamp } : null;
        }, true);
        viewport.addEventListener('pointercancel', function () {
            state.down = null;
        }, true);
        viewport.addEventListener('pointerup', function (e) {
            const d = state.down;
            state.down = null;
            if (!d || d.id !== e.pointerId || e.timeStamp - d.t > TAP_MS
                || Math.hypot(e.clientX - d.x, e.clientY - d.y) > TAP_MOVE_PX) {
                return;
            }

            onTap(viewport, state, e);
        }, true);
    }

    function readShowAll() {
        try {
            return window.localStorage.getItem(SHOW_ALL_KEY) === '1';
        } catch (_) {
            return false; // storage blocked: the default, outlines on demand
        }
    }

    window.bwHoldTap = {
        findAt: findAt,

        /** Gives the viewport its panel's tappable holds; a new list clears the outline. */
        setHolds: function (viewport, items) {
            if (!viewport) {
                return;
            }

            let state = states.get(viewport);
            if (!state) {
                state = { items: [], selected: -1, down: null };
                states.set(viewport, state);
                bind(viewport, state);
            }

            state.items = items || [];
            state.selected = -1;
            draw(viewport, state);
        },

        /** The per-device "Show all holds" preference (off unless the user turned it on here). */
        showAll: readShowAll,

        setShowAll: function (on) {
            try {
                window.localStorage.setItem(SHOW_ALL_KEY, on ? '1' : '0');
            } catch (_) { /* storage blocked: the choice lasts for this page only */ }
        },
    };
})();
