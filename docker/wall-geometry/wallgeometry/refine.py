"""Edge-line corner refinement (the app ships a C# port; here it serves the CLI and the texture check).

The markers are screwed to the wall AT their corners, and the ArUco sub-pixel corner refinement is
pulled toward the screw heads / paper edge. This refits each side of the black square as a line from
edge samples taken away from the corners (15..85 % of the side), then intersects adjacent lines.
"""
import cv2
import numpy as np
from scipy.ndimage import map_coordinates


def prepare(gray_u8):
    """uint8 gray image -> the float32, sigma-1 blurred image refine_corners expects."""
    return cv2.GaussianBlur(gray_u8.astype(np.float32), (0, 0), 1.0)


def _edge_points(img, a, b, centre, side, method="centroid"):
    """Sub-pixel dark->bright (inside->outside) edge points along segment a->b."""
    d = (b - a) / np.linalg.norm(b - a)
    n = np.array([d[1], -d[0]])
    if n @ ((a + b) / 2 - centre) < 0:
        n = -n
    w = max(3.0, side / 7.0)  # < black border width (side/6): never reaches the data cells
    s = np.arange(-w, w + 1e-9, 0.25)
    ts = np.linspace(0.15, 0.85, int(np.clip(np.linalg.norm(b - a) / 2, 8, 80)))
    pts = []
    h, wd = img.shape
    for t in ts:
        p = a + t * (b - a)
        q = p[None, :] + s[:, None] * n[None, :]
        if q[:, 0].min() < 1 or q[:, 1].min() < 1 or q[:, 0].max() > wd - 2 or q[:, 1].max() > h - 2:
            continue
        prof = map_coordinates(img, [q[:, 1], q[:, 0]], order=1)
        g = np.gradient(prof)
        k = int(np.argmax(g))
        if g[k] <= 2.0 or k == 0 or k == len(g) - 1:
            continue
        pts.append(p + _peak_position(s, g, k, method) * n)
    return np.array(pts)


def _peak_position(s, g, k, method):
    """Sub-sample position of the gradient peak at index k along the profile positions s.

    "centroid" (default, matches the app's C# port): gradient-weighted centroid of the samples
    contiguous with the peak whose gradient is >= half the peak. "parabola": the original 3-point
    fit; the bilinear profile's gradient is piecewise-flat within a pixel, so it snaps edge points
    to pixel cells (a 1 px staircase on near-axis sides, corners up to ~1.1 px off). Kept only to
    reproduce old results.
    """
    if method == "parabola":
        den = g[k - 1] - 2 * g[k] + g[k + 1]
        off = 0.5 * (g[k - 1] - g[k + 1]) / den if den != 0 else 0.0
        return s[k] + off * (s[1] - s[0])
    half = 0.5 * g[k]
    lo, hi = k, k
    while lo > 0 and g[lo - 1] >= half:
        lo -= 1
    while hi < len(g) - 1 and g[hi + 1] >= half:
        hi += 1
    w = g[lo:hi + 1]
    return float(np.sum(s[lo:hi + 1] * w) / np.sum(w))


def _fit_line(pts, thr=1.0, rng=np.random.default_rng(0)):
    """RANSAC line (1 px consensus), then total-least-squares on the inliers.

    Returns (point, direction) or None when fewer than half the samples agree.
    """
    if len(pts) < 6:
        return None
    best = None
    for _ in range(200):
        i, j = rng.choice(len(pts), 2, replace=False)
        d = pts[j] - pts[i]
        if np.linalg.norm(d) < 1e-6:
            continue
        d = d / np.linalg.norm(d)
        r = np.abs((pts - pts[i]) @ np.array([-d[1], d[0]]))
        inl = r < thr
        if best is None or inl.sum() > best.sum():
            best = inl
    if best is None or best.sum() < max(5, 0.5 * len(pts)):
        return None
    c = pts[best].mean(0)
    _, _, vt = np.linalg.svd(pts[best] - c)
    return c, vt[0]


def _intersect(l1, l2):
    (p, d), (q, e) = l1, l2
    A = np.array([d, -e]).T
    if abs(np.linalg.det(A)) < 1e-6:
        return None
    t = np.linalg.solve(A, q - p)
    return p + t[0] * d


def refine_corners(img, corners, mean_side, method="centroid"):
    """img: float32 gray (see prepare). Return (refined 4x2 corners, per-corner shift px, ok flags)."""
    c = np.asarray(corners, float)
    centre = c.mean(0)
    lines = []
    for i in range(4):
        pts = _edge_points(img, c[i], c[(i + 1) % 4], centre, mean_side, method)
        lines.append(_fit_line(pts))
    out, ok = c.copy(), np.zeros(4, bool)
    for i in range(4):
        # corner i is the intersection of side (i-1 -> i) and side (i -> i+1)
        la, lb = lines[(i - 1) % 4], lines[i]
        if la is None or lb is None:
            continue
        p = _intersect(la, lb)
        if p is None or np.linalg.norm(p - c[i]) > 0.25 * mean_side:
            continue
        out[i], ok[i] = p, True
    return out, np.linalg.norm(out - c, axis=1), ok
