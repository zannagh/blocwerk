/*
 * Outlines on demand for the wall view. The panel photo is shown without the hold overlay; this finds
 * the hold under a tap in the browser (hold-tap-hit.js), from a compact list sent once per panel
 * (HoldTapList.cs), and draws only that hold's outline into svg.hold-tap-layer: with, darker, the holds
 * that sit on it and the holds that overlap it, and, subdued, the hold it sits on itself.
 * No server round-trip per tap and no SVG node per hold.
 *
 * An item is { x, y, r, p, h, f, c, n, u, o, s }: centre and radius, outline and holes as flat x,y
 * lists, foot, colour, colour name, boulders using it, positions of the overlapping holds, position of
 * the hold it sits on. Coordinates are in 1/10000 of the photo; the layer's viewBox is 0-100.
 */
(function () {
    'use strict';

    const SCALE = 10000;
    const NS = 'http://www.w3.org/2000/svg';
    const SHOW_ALL_KEY = 'blocwerk-show-all-holds';
    const TAP_MOVE_PX = 8;    // further than this and the press was a pan
    const TAP_MS = 500;       // longer than this and it was a long press (the magnifier)
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

    /** The outline of one hold: look '' is the tapped hold, 'dark' a hold on or over it, 'base' the hold under it. */
    function shine(item, look) {
        const group = document.createElementNS(NS, 'g');
        group.setAttribute('class', look ? 'hold-shine ' + look : 'hold-shine');
        if (item.c) {
            group.style.setProperty('--hold-c', item.c);
        }

        (look ? ['case', 'main'] : ['glow', 'case', 'main', 'light']).forEach(function (layer) {
            const g = document.createElementNS(NS, 'g');
            g.setAttribute('class', 'hold-shine-' + layer);
            g.appendChild(shapeNode(item));
            group.appendChild(g);
        });
        return group;
    }

    function labelText(items, index) {
        const item = items[index];
        const parts = [];
        if (item.n) {
            parts.push(item.n);
        }

        if (item.u === 0) {
            parts.push('not used by a boulder');
        } else if (item.u > 0) {
            parts.push('used by ' + item.u + (item.u === 1 ? ' boulder' : ' boulders'));
        }

        const under = window.bwHoldHit.below(items, index);
        if (under.length) {
            const name = items[under[0]].n;
            parts.push(name ? 'on the ' + name.toLowerCase() + ' hold' : 'on another hold');
        }

        const onIt = window.bwHoldHit.above(items, index).length;
        if (onIt) {
            parts.push(onIt + (onIt === 1 ? ' hold on it' : ' holds on it'));
        }

        if (item.o && item.o.length) {
            parts.push(item.o.length + ' overlapping');
        }

        return parts.join(' · ');
    }

    function showLabel(label, items, index) {
        if (!label) {
            return;
        }

        const item = index >= 0 ? items[index] : null;
        const text = item ? labelText(items, index) : '';
        label.hidden = !text;
        if (!text) {
            return;
        }

        const b = window.bwHoldHit.box(item);
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
            // Back to front: the hold it sits on, the hold itself, then what overlaps it and what sits on it.
            const under = window.bwHoldHit.below(state.items, state.selected);
            if (under.length) {
                layer.appendChild(shine(state.items[under[0]], 'base'));
            }

            layer.appendChild(shine(item, ''));
            (item.o || []).concat(window.bwHoldHit.above(state.items, state.selected)).forEach(function (i) {
                if (state.items[i]) {
                    layer.appendChild(shine(state.items[i], 'dark'));
                }
            });
        }

        showLabel(viewport.querySelector('.hold-tap-label'), state.items, state.selected);
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
        state.selected = window.bwHoldHit.findAt(state.items, x, y, rect.width / SCALE, rect.height / SCALE);
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
