"""Seam harmonisation between facet textures: no brightness / colour jump where two facets meet.

Even with one global exposure solve, two facets that meet at an edge are usually drawn from different
photos (and the light falls on them differently), so the 3D view can show a step along the shared
edge. Here every pair of facets whose extents meet gets a thin band on each side of the edge
(`seamBandMm`). Along the edge, in bins of `seamBinMm`, the band's median log colour is compared per
channel; each side is corrected by half the difference so both meet in the middle AT the edge (a
multiplicative gain in log space, so the band's mean and spread are matched together). The correction
is smoothed along the edge and fades out quickly with the distance from the edge (Gaussian,
`seamFalloffMm`): no hard line at the seam, while each facet keeps its own level everywhere but right
at the edge (a vertical kickboard lit brighter than the overhang above it; see flatten.py for the
even shading within facets of the same overhang).

Works on low-resolution copies (`seamDownscale`) and changes only covered pixels.
"""
import cv2
import numpy as np

SEAM_DEFAULTS = {"seamHarmonise": True, "seamBandMm": (30.0, 130.0), "seamBinMm": 150.0, "seamSmoothMm": 150.0,
                 "seamFalloffMm": 100.0, "seamDownscale": 4, "seamPasses": 1, "seamMinPixels": 12,
                 "seamMaxLogStep": 0.7, "seamMinEdgeMm": 300.0}
DARK, BRIGHT = 12.0, 243.0


def _vec(f, k):
    return np.array(f[k], float)


def _plane(f, g, cols, rows):
    a = g["aMin"] + (cols + 0.5) * g["res"]
    b = g["bMax"] - (rows + 0.5) * g["res"]
    return _vec(f, "origin") + a[..., None] * _vec(f, "u") + b[..., None] * _vec(f, "v")


def _ab(f, X):
    O = _vec(f, "origin")
    return (X - O) @ _vec(f, "u"), (X - O) @ _vec(f, "v")


def _inside(f, X):
    a, b = _ab(f, X)
    e = f["extentMm"]
    return (a >= e["aMin"]) & (a <= e["aMax"]) & (b >= e["bMin"]) & (b <= e["bMax"])


def _dist_to(f, X):
    """Distance of world points X to facet f's extent rectangle (3D)."""
    a, b = _ab(f, X)
    e = f["extentMm"]
    a, b = np.clip(a, e["aMin"], e["aMax"]), np.clip(b, e["bMin"], e["bMax"])
    P = _vec(f, "origin") + a[..., None] * _vec(f, "u") + b[..., None] * _vec(f, "v")
    return np.linalg.norm(P - X, axis=-1)


class _Tex:
    """One facet texture at low resolution: log colour, usable mask, world points."""

    def __init__(self, f, r, ds):
        self.f, self.r, self.ds = f, r, ds
        img = r["image"]
        H, W = img.shape[:2]
        self.h, self.w = max(1, H // ds), max(1, W // ds)
        small = cv2.resize(img.astype(np.float32), (self.w, self.h), interpolation=cv2.INTER_AREA)
        cover = cv2.resize((r["mask"] >= 250).astype(np.float32), (self.w, self.h), interpolation=cv2.INTER_AREA)
        self.ok = (cover > 0.99) & np.all((small > DARK) & (small < BRIGHT), -1)
        self.log = np.log(np.maximum(small, 1.0))
        b = r["bounds"]
        self.g = {"aMin": b["aMin"], "bMax": b["bMax"], "res": r["mmPerPx"] * W / self.w}
        cols, rows = np.meshgrid(np.arange(self.w, dtype=float), np.arange(self.h, dtype=float))
        self.X = _plane(f, self.g, cols, rows)
        self.ok &= _inside(f, self.X)
        self.field = np.zeros((self.h, self.w, 3), np.float32)

    def cur(self):
        return self.log + self.field


def _band(A, B, p):
    lo, hi = p["seamBandMm"]
    d = _dist_to(B.f, A.X)
    return A.ok & (d >= lo) & (d < hi)


def _seam_pairs(texs, p):
    """Index pairs of facets whose extents meet along at least `seamMinEdgeMm`."""
    out = []
    for i in range(len(texs)):
        for j in range(i + 1, len(texs)):
            A, B = texs[i], texs[j]
            bandA, bandB = _band(A, B, p), _band(B, A, p)
            if bandA.sum() < p["seamMinPixels"] or bandB.sum() < p["seamMinPixels"]:
                continue
            pts = np.concatenate([A.X[bandA], B.X[bandB]])
            t = np.linalg.svd(pts - pts.mean(0), full_matrices=False)[2][0]
            s = pts @ t
            if s.max() - s.min() >= p["seamMinEdgeMm"]:
                out.append((i, j, bandA, bandB, t))
    return out


def _seam_targets(A, B, bandA, bandB, t, p):
    """Per-pixel log corrections on both bands (half the per-bin difference each), NaN where unknown."""
    sA, sB = A.X[bandA] @ t, B.X[bandB] @ t
    lo, step = min(sA.min(), sB.min()), float(p["seamBinMm"])
    kA, kB = ((sA - lo) // step).astype(int), ((sB - lo) // step).astype(int)
    vA, vB = A.cur()[bandA], B.cur()[bandB]
    cA = np.full(vA.shape, np.nan, np.float32)
    cB = np.full(vB.shape, np.nan, np.float32)
    cap, n0 = float(p["seamMaxLogStep"]), int(p["seamMinPixels"])
    for k in np.intersect1d(kA, kB):
        ma, mb = kA == k, kB == k
        if ma.sum() < n0 or mb.sum() < n0:
            continue
        d = np.clip(np.median(vA[ma], 0) - np.median(vB[mb], 0), -cap, cap)
        cA[ma], cB[mb] = -d / 2, d / 2
    return cA, cB


def _field(T, seam_vals, p):
    """Log-gain field of one facet from its seams: list of (band mask, band corrections, other facet).
    Per seam the band values are smoothed along the edge, carried to every pixel from its nearest band
    pixel and weighted by a Gaussian of the pixel's distance to the edge: full half-step at the edge,
    gone within a few `seamFalloffMm`, so the facet keeps its own level further in."""
    out = np.zeros((T.h, T.w, 3), np.float32)
    sig = max(float(p["seamSmoothMm"]) / T.g["res"], 0.5)
    for m, v, other in seam_vals:
        good = ~np.isnan(v[:, 0])
        if not good.any():
            continue
        num = np.zeros((T.h, T.w, 3), np.float32)
        den = np.zeros((T.h, T.w), np.float32)
        ys, xs = np.nonzero(m)
        num[ys[good], xs[good]] = v[good]
        den[ys[good], xs[good]] = 1.0
        nb, db = cv2.GaussianBlur(num, (0, 0), sig), cv2.GaussianBlur(den, (0, 0), sig)
        band = den > 0
        val = nb / np.maximum(db, 1e-9)[..., None]
        src = np.where(band, 0, 255).astype(np.uint8)
        _, lab = cv2.distanceTransformWithLabels(src, cv2.DIST_L2, 5, labelType=cv2.DIST_LABEL_PIXEL)
        by, bx = np.nonzero(band)
        idx = np.zeros(lab.max() + 1, np.intp)
        idx[lab[by, bx]] = np.arange(by.size)
        ext = val[by, bx][idx[lab]]
        fall = np.exp(-0.5 * (_dist_to(other, T.X) / float(p["seamFalloffMm"])) ** 2)
        out += ext * fall[..., None].astype(np.float32)
    return out


def harmonise(results, facets, params=None):
    """results: render_textures' list (image BGR uint8, mask, bounds, mmPerPx); facets: id -> facet.
    Corrects the images in place; returns a small report per seam (median |log step| before/after)."""
    p = {**SEAM_DEFAULTS, **(params or {})}
    ds = int(p["seamDownscale"])
    texs = [_Tex(facets[r["facet"]], r, ds) for r in results]
    pairs = _seam_pairs(texs, p)
    report = {f"{texs[i].r['facet']}-{texs[j].r['facet']}": {"before": _step(texs[i], texs[j], a, b, t, p)}
              for i, j, a, b, t in pairs}
    for _ in range(int(p["seamPasses"])):
        per = {k: [] for k in range(len(texs))}
        for i, j, bandA, bandB, t in pairs:
            cA, cB = _seam_targets(texs[i], texs[j], bandA, bandB, t, p)
            per[i].append((bandA, cA, texs[j].f))
            per[j].append((bandB, cB, texs[i].f))
        for k, T in enumerate(texs):
            if per[k]:
                T.field += _field(T, per[k], p)
    for (i, j, a, b, t) in pairs:
        report[f"{texs[i].r['facet']}-{texs[j].r['facet']}"]["after"] = _step(texs[i], texs[j], a, b, t, p)
    for T in texs:
        _apply(T)
    return report


def _step(A, B, bandA, bandB, t, p):
    cA, _ = _seam_targets(A, B, bandA, bandB, t, {**p, "seamMaxLogStep": 10.0})
    good = ~np.isnan(cA[:, 0])
    return round(float(np.median(np.abs(2 * cA[good].mean(1)))), 4) if good.any() else None


def _apply(T):
    if not np.any(T.field):
        return
    img = T.r["image"]
    H, W = img.shape[:2]
    F = cv2.resize(T.field, (W, H), interpolation=cv2.INTER_LINEAR)
    out = img.astype(np.float32) * np.exp(F)
    T.r["image"] = np.clip(out + 0.5, 0, 255).astype(np.uint8)
