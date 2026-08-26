"""Refine a hold's centre, extent and colour from the image, not from its box.

Both point clouds we register are noisy in the same avoidable way. The detector emits
boxes that are frequently a small patch on the textured middle of a big hold, so its
centroid is not the hold's centre; the stored holds carry a hand/YOLO radius and only
70 of 463 have an outline. Registering box centroids against box centroids therefore
bakes in tens of pixels of error that has nothing to do with the geometry.

A hold is a compact patch of NON-WOOD colour on a wooden wall, so it segments cleanly:
model the background from the window's border ring, threshold the colour distance from
it, and keep the component under the seed. That yields a centroid, an equivalent
radius, and a colour averaged over the hold's ACTUAL pixels rather than over a disc
that is part wood.
"""
import cv2
import numpy as np

WEIGHTS = np.array([0.5, 1.0, 1.0])   # Lab, lightness down-weighted (shading)


def _one(lab, cx, cy, r, expand, min_frac, max_frac):
    h, w = lab.shape[:2]
    rr = max(6.0, expand * r)
    x0, x1 = int(max(0, cx - rr)), int(min(w, cx + rr + 1))
    y0, y1 = int(max(0, cy - rr)), int(min(h, cy + rr + 1))
    if x1 - x0 < 8 or y1 - y0 < 8:
        return None
    win = lab[y0:y1, x0:x1]
    ys, xs = np.mgrid[y0:y1, x0:x1]
    d = np.sqrt((xs - cx) ** 2 + (ys - cy) ** 2)
    ring = d >= 0.85 * rr
    if ring.sum() < 30:
        return None
    bg = np.median(win[ring], 0)
    dist = np.linalg.norm((win - bg) * WEIGHTS, axis=-1)
    scaled = np.clip(dist / max(dist.max(), 1e-6) * 255.0, 0, 255).astype(np.uint8)
    thr, _ = cv2.threshold(scaled, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
    fg = (scaled >= max(thr, 30)).astype(np.uint8)
    fg = cv2.morphologyEx(fg, cv2.MORPH_OPEN, np.ones((3, 3), np.uint8))
    fg = cv2.morphologyEx(fg, cv2.MORPH_CLOSE, np.ones((5, 5), np.uint8))
    n, labels, stats, cents = cv2.connectedComponentsWithStats(fg, 8)
    if n <= 1:
        return None
    seed = labels[int(np.clip(cy - y0, 0, win.shape[0] - 1)),
                  int(np.clip(cx - x0, 0, win.shape[1] - 1))]
    if seed == 0:
        # the seed pixel landed on background: take the biggest component that still
        # overlaps the nominal hold disc, otherwise give up rather than guess
        cand = [k for k in range(1, n)
                if np.hypot(cents[k][0] + x0 - cx, cents[k][1] + y0 - cy) <= 1.1 * r]
        if not cand:
            return None
        seed = max(cand, key=lambda k: stats[k, cv2.CC_STAT_AREA])
    area = float(stats[seed, cv2.CC_STAT_AREA])
    frac = area / float(win.shape[0] * win.shape[1])
    if not (min_frac <= frac <= max_frac):
        return None
    mask = labels == seed
    cx2, cy2 = float(cents[seed][0] + x0), float(cents[seed][1] + y0)
    r2 = float(np.sqrt(area / np.pi))
    colour = np.median(win[mask], 0)
    # compactness: a real hold is a blob, a lighting gradient or a seam is not
    per = cv2.arcLength(cv2.findContours(mask.astype(np.uint8), cv2.RETR_EXTERNAL,
                                         cv2.CHAIN_APPROX_SIMPLE)[0][0], True)
    compact = float(4 * np.pi * area / max(per * per, 1e-6))
    return (cx2, cy2), r2, colour, compact


def refine(img_lab, centres, radii, expand=1.9, min_frac=0.02, max_frac=0.75):
    """Segment each hold. Returns centres, radii, colours, confidence (0 = fell back)."""
    cen = np.asarray(centres, np.float64).copy()
    rad = np.asarray(radii, np.float64).copy()
    col = np.full((len(cen), 3), np.nan)
    conf = np.zeros(len(cen))
    for i, ((cx, cy), r) in enumerate(zip(centres, radii)):
        got = _one(img_lab, float(cx), float(cy), max(float(r), 5.0),
                   expand, min_frac, max_frac)
        if got is None:
            continue
        (nx, ny), nr, colour, compact = got
        # a refinement that runs away from the seed is not this hold
        if np.hypot(nx - cx, ny - cy) > 1.2 * max(r, 6.0):
            continue
        cen[i] = (nx, ny)
        rad[i] = nr
        col[i] = colour
        conf[i] = min(1.0, compact)
    return cen, rad, col, conf
