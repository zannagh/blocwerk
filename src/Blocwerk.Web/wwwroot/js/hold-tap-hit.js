/*
 * Hit-testing for the wall view's outlines on demand (hold-tap.js): which hold of a panel's compact
 * list (HoldTapList.cs) lies under a point, and what sits on what.
 *
 * An item's `s` is the position of the hold it sits ON (a hold on a volume); a hold on the wall has
 * none. Holds that sit on a hold are in front of it: under a finger the front-most one is met first,
 * then the hold it sits on. Two holds that merely overlap have no order, so the smaller one wins.
 */
(function () {
    'use strict';

    const NEAR_PX = 10; // a tap this close to a hold still picks it

    function box(item) {
        if (item.b) {
            return item.b;
        }

        if (!item.p) {
            item.b = { minX: item.x - item.r, maxX: item.x + item.r, minY: item.y - item.r, maxY: item.y + item.r };
            return item.b;
        }

        const b = { minX: Infinity, maxX: -Infinity, minY: Infinity, maxY: -Infinity };
        for (let i = 0; i < item.p.length; i += 2) {
            b.minX = Math.min(b.minX, item.p[i]);
            b.maxX = Math.max(b.maxX, item.p[i]);
            b.minY = Math.min(b.minY, item.p[i + 1]);
            b.maxY = Math.max(b.maxY, item.p[i + 1]);
        }

        item.b = b;
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

    /** The positions of the holds a hold sits on, nearest first. A loop in the data ends the walk. */
    function below(items, i) {
        const chain = [];
        const seen = new Set([i]);
        let s = items[i] ? items[i].s : null;
        while (s !== null && s !== undefined && items[s] && !seen.has(s)) {
            chain.push(s);
            seen.add(s);
            s = items[s].s;
        }

        return chain;
    }

    /** The positions of everything that sits on a hold, at any depth. */
    function above(items, i) {
        const found = [];
        for (let j = 0; j < items.length; j++) {
            if (j !== i && items[j].s !== null && items[j].s !== undefined && below(items, j).indexOf(i) >= 0) {
                found.push(j);
            }
        }

        return found;
    }

    /** Of the holds under the point, the one in front: what sits on the smallest one, as far up as it goes. */
    function front(items, hits) {
        let best = hits[0];
        hits.forEach(function (h) {
            if (h.area < best.area) {
                best = h;
            }
        });

        let top = best.i;
        let height = 0;
        hits.forEach(function (h) {
            const at = below(items, h.i).indexOf(best.i);
            if (at >= height) {
                top = h.i;
                height = at + 1;
            }
        });
        return top;
    }

    /**
     * The hold at a point: of those containing it the front-most (see above), else the nearest within
     * NEAR_PX on screen. -1 for empty wall. sx and sy are screen pixels per unit.
     */
    function findAt(items, x, y, sx, sy) {
        const hits = [];
        let near = -1;
        let nearPx = NEAR_PX;
        const padX = NEAR_PX / sx;
        const padY = NEAR_PX / sy;
        items.forEach(function (item, i) {
            const b = box(item);
            if (x < b.minX - padX || x > b.maxX + padX || y < b.minY - padY || y > b.maxY + padY) {
                return;
            }

            if (contains(item, x, y)) {
                hits.push({ i: i, area: (b.maxX - b.minX) * (b.maxY - b.minY) });
            } else {
                const d = distancePx(item, x, y, sx, sy);
                if (d <= nearPx) {
                    near = i;
                    nearPx = d;
                }
            }
        });
        return hits.length ? front(items, hits) : near;
    }

    window.bwHoldHit = { box: box, findAt: findAt, below: below, above: above };
})();
