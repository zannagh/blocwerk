"""Per-hold colour signatures, sampled the same way from both photos.

The stored `Color` field is a coarse palette key, and the two photos have different
white balance, so neither is used directly. Instead the same statistic (median Lab
over the hold's inner disc) is measured in BOTH images and the two are compared
after a global illumination alignment estimated from the confident matches. Colour
is what separates a hold from the neighbour 60 px away when the geometry alone
cannot.
"""
import cv2
import numpy as np


def sample(img_lab, centres, radii, frac=0.55):
    """Median Lab inside `frac` * radius of each centre."""
    h, w = img_lab.shape[:2]
    out = np.zeros((len(centres), 3), np.float64)
    for i, ((cx, cy), r) in enumerate(zip(centres, radii)):
        rr = max(2.0, frac * r)
        x0, x1 = int(max(0, cx - rr)), int(min(w, cx + rr + 1))
        y0, y1 = int(max(0, cy - rr)), int(min(h, cy + rr + 1))
        if x1 <= x0 or y1 <= y0:
            out[i] = np.nan
            continue
        patch = img_lab[y0:y1, x0:x1].reshape(-1, 3)
        ys, xs = np.mgrid[y0:y1, x0:x1]
        m = ((xs - cx) ** 2 + (ys - cy) ** 2).ravel() <= rr * rr
        out[i] = np.median(patch[m], 0) if m.sum() >= 4 else np.median(patch, 0)
    return out


def align(src, dst, pairs):
    """Per-channel affine illumination alignment src->dst estimated on `pairs`."""
    i, j = pairs
    a = np.ones(3)
    b = np.zeros(3)
    for c in range(3):
        x, y = src[i, c], dst[j, c]
        ok = np.isfinite(x) & np.isfinite(y)
        if ok.sum() >= 10:
            m = np.polyfit(x[ok], y[ok], 1)
            a[c], b[c] = float(m[0]), float(m[1])
    return a, b


def distance(src, dst, a, b, weights=(0.45, 1.0, 1.0)):
    """Pairwise Lab distance matrix, L down-weighted (shading differs between views)."""
    s = src * a + b
    w = np.asarray(weights, np.float64)
    d = (s[:, None, :] - dst[None, :, :]) * w
    out = np.sqrt((d ** 2).sum(-1))
    return np.where(np.isfinite(out), out, 999.0)


def lab(img):
    return cv2.cvtColor(img, cv2.COLOR_BGR2LAB).astype(np.float64)


def quantile_align(src, dst, bins=512):
    """Per-channel quantile map src->dst, estimated from the DISTRIBUTIONS alone.

    No correspondences are needed, which is the point: it lets colour be used to FIND
    the correspondences instead of depending on them. It is valid here because both
    clouds are largely the same physical holds, so their colour marginals match even
    when we do not yet know which hold is which.
    """
    out = np.zeros_like(src)
    qs = np.linspace(0, 1, bins)
    for c in range(src.shape[1]):
        s, d = src[:, c], dst[:, c]
        so, do = s[np.isfinite(s)], d[np.isfinite(d)]
        if len(so) < 20 or len(do) < 20:
            out[:, c] = s
            continue
        out[:, c] = np.interp(s, np.quantile(so, qs), np.quantile(do, qs))
    return out
