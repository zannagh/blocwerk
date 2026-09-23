"""Multi-view sample slots + the two ways to combine them for the facet textures.

Default ("select", see consensus.py): per pixel the single best photo that agrees with the consensus
of all photos, seams softened over `seamFeatherPx`. Averaging photos ghosts protruding holds (large
parallax between close-range views), so the blend below is kept as blendMode "blend".

Every facet pixel is drawn from up to `blendViews` photos (the best by image px per plane mm, see
`textures._score`) instead of one. Per photo the weight is how far its score clears the first photo
that did NOT make the cut, so a photo fades to zero exactly where it leaves the top-N set (no seams at
view boundaries), times (score / best score) ** `blendSharpness` so the sharpest view dominates and
protruding holds are not ghosted, times a ramp towards the photo's border.

Things in front of the wall that are not in the model (roof rafters, people, ropes) are seen by only
some photos at any given spot. They are rejected per pixel: the reference is the weighted medoid of the
candidates in CIE Lab (lightly blurred, so noise and sub-pixel parallax do not decide), and candidates
more than `outlierDeltaE` from it are dropped before the weighted mean. Two candidates that disagree
leave the better-weighted one.

Memory: K = blendViews + 2 sample slots per pixel (uint8 colour, float16 weight, int16 photo index);
the robust combine runs in row tiles. Photos are loaded once.
"""
import math

import cv2
import numpy as np

from . import exposure

BLEND_DEFAULTS = {"blendViews": 6, "blendSharpness": 8.0, "outlierDeltaE": 12.0, "outlierBlurPx": 3.0,
                  "outlierSmoothPx": 11, "blendMode": "select", "seamFeatherPx": 5,
                  "selectModeFilterCells": 9,
                  "borderRampPx": 48.0, "combineTileRows": 128, "exposureBalance": True}


def view_weights(S, n, sharp):
    """Per-cell scores S (C, ch, cw) -> per-cell blend weights (C, ch, cw), non-zero for the top-n."""
    C = S.shape[0]
    if C <= n:
        cut = np.zeros(S.shape[1:], S.dtype)
    else:
        cut = -np.partition(-S, n, axis=0)[n]  # (n+1)-th best score
    best = S.max(0)
    rel = S / np.where(best > 0, best, 1)
    W = np.clip(S - cut, 0, None) * rel ** sharp
    W[S <= 0] = 0
    return W.astype(np.float32)


class FacetAccumulator:
    """Sample slots of one facet grid, filled photo by photo."""

    def __init__(self, g, W_cells, cell, n):
        self.g, self.W, self.cell = g, W_cells, cell
        self.K = n + 2
        H, Wd = g["H"], g["W"]
        self.rgb = np.zeros((self.K, H, Wd, 3), np.uint8)
        self.wt = np.zeros((self.K, H, Wd), np.float16)
        self.cam = np.full((self.K, H, Wd), -1, np.int16)
        self.count = np.zeros((H, Wd), np.uint8)

    def region(self, c):
        """Full-res weight crop (y0, x0, weights) for photo index c, or None."""
        Wc = self.W[c]
        ys, xs = np.nonzero(Wc > 0)
        if ys.size == 0:
            return None
        cy0, cy1 = max(ys.min() - 1, 0), min(ys.max() + 2, Wc.shape[0])
        cx0, cx1 = max(xs.min() - 1, 0), min(xs.max() + 2, Wc.shape[1])
        crop = Wc[cy0:cy1, cx0:cx1]
        cell = self.cell
        up = cv2.resize(crop, ((cx1 - cx0) * cell, (cy1 - cy0) * cell), interpolation=cv2.INTER_LINEAR)
        y0, x0 = cy0 * cell, cx0 * cell
        up = up[:max(0, self.g["H"] - y0), :max(0, self.g["W"] - x0)]
        return y0, x0, up

    def add(self, c, y0, x0, rgb, w):
        """Put photo c's colours `rgb` (h, w, 3) with weights `w` (h, w) into the next free slot."""
        h, wd = w.shape
        cnt = self.count[y0:y0 + h, x0:x0 + wd]
        m = (w > 1e-6) & (cnt < self.K)
        if not m.any():
            return
        ys, xs = np.nonzero(m)
        k = cnt[ys, xs].astype(np.intp)
        Y, X = ys + y0, xs + x0
        self.rgb[k, Y, X] = rgb[ys, xs]
        self.wt[k, Y, X] = w[ys, xs]
        self.cam[k, Y, X] = c
        cnt[ys, xs] += 1


def border_ramp(mx, my, iw, ih, ramp):
    """1 inside the photo, falling linearly to 0 over `ramp` px towards its border."""
    d = np.minimum(np.minimum(mx, iw - 1 - mx), np.minimum(my, ih - 1 - my))
    return np.clip(d / max(ramp, 1e-6), 0, 1)


def _lab(rgb_u8, blur):
    """(K, h, w, 3) BGR uint8 -> Lab float32, optionally Gaussian-blurred per slot."""
    K, h, w, _ = rgb_u8.shape
    lab = cv2.cvtColor((rgb_u8.reshape(K * h, w, 3).astype(np.float32) / 255.0), cv2.COLOR_BGR2Lab)
    lab = lab.reshape(K, h, w, 3)
    if blur > 0:
        for k in range(K):
            lab[k] = cv2.GaussianBlur(lab[k], (0, 0), blur)
    return lab


def robust_combine(rgb, wt, delta_e, blur, smooth=0):
    """rgb (K, h, w, 3) uint8, wt (K, h, w) float -> (image (h, w, 3) float32, survivors (K, h, w) bool).
    Weighted-medoid outlier rejection in Lab, then the weighted mean of the survivors."""
    valid = wt > 0
    lab = _lab(rgb, blur)
    K = rgb.shape[0]
    d = np.zeros((K, K) + wt.shape[1:], np.float32)
    for i in range(K):
        for j in range(i + 1, K):
            dij = np.sqrt(((lab[i] - lab[j]) ** 2).sum(-1))
            d[i, j] = d[j, i] = dij
    wv = np.where(valid, wt, 0).astype(np.float32)
    # unweighted (every candidate one vote): an occluder in the single sharpest view must not outvote
    # the others that agree on the wall behind it
    cost = (d * valid[None]).sum(1)  # (K, h, w): summed distance of candidate i to all others
    cost[~valid] = np.inf
    med = cost.argmin(0)
    dmed = np.take_along_axis(d, med[None, None], 0)[0]  # (K, h, w) distance to the medoid
    keep = valid & (dmed <= delta_e)
    keep |= valid & (np.arange(K)[:, None, None] == med[None])
    keepf = keep.astype(np.float32)
    if smooth > 1:  # decide per neighbourhood, not per pixel: no speckle where views barely disagree
        for k in range(K):
            keepf[k] = cv2.boxFilter(keepf[k], -1, (smooth, smooth), borderType=cv2.BORDER_REPLICATE)
        keepf *= keepf
    w = np.where(keep, wv * keepf, 0)
    tot = w.sum(0)
    out = (rgb.astype(np.float32) * w[..., None]).sum(0) / np.where(tot > 0, tot, 1)[..., None]
    return out, keep


def select_combine(rgb, wt, cam, label, feather):
    """Single-photo choice per pixel (`label`, full-res photo index; -1 = none) from the slots, with the
    seam softened over `feather` px; where the chosen photo has no slot, the best-weighted slot."""
    K = rgb.shape[0]
    pick = ((cam == label[None]) & (wt > 0)).astype(np.float32)
    if feather > 1:
        for k in range(K):
            pick[k] = cv2.boxFilter(pick[k], -1, (feather, feather), borderType=cv2.BORDER_REPLICATE)
        pick[wt <= 0] = 0
    none = pick.sum(0) <= 0
    best = (np.arange(K)[:, None, None] == wt.argmax(0)[None]) & (wt > 0)
    w = np.where(none[None], best, pick).astype(np.float32)
    tot = w.sum(0)
    out = (rgb.astype(np.float32) * w[..., None]).sum(0) / np.where(tot > 0, tot, 1)[..., None]
    return out, w > 0


def finish(acc, gains, p, label=None):
    """Apply per-photo gains and combine all slots -> (image uint8, filled bool, per-photo use).
    With a `label` map: consensus single-photo choice; else the robust multi-view blend."""
    H, W = acc.g["H"], acc.g["W"]
    out = np.zeros((H, W, 3), np.uint8)
    lut = exposure.gain_luts(gains)  # (C, 3, 256) uint8, identity where no gain
    kept_by_cam = np.zeros(len(lut), np.float64)
    step = int(p["combineTileRows"])
    for r0 in range(0, H, step):
        sl = slice(r0, min(r0 + step, H))
        cam, wt = acc.cam[:, sl], acc.wt[:, sl].astype(np.float32)
        rgb = exposure.apply_luts(acc.rgb[:, sl], cam, lut)
        if label is not None:
            img, keep = select_combine(rgb, wt, cam, label[sl], int(p["seamFeatherPx"]))
        else:
            img, keep = robust_combine(rgb, wt, p["outlierDeltaE"], p["outlierBlurPx"],
                                       int(p["outlierSmoothPx"]))
        out[sl] = np.clip(img + 0.5, 0, 255).astype(np.uint8)
        kc = cam[keep]
        kept_by_cam += np.bincount(kc[kc >= 0], weights=wt[keep][kc >= 0], minlength=len(lut))
    filled = acc.count > 0
    return out, filled, kept_by_cam


def shrink(img, down=8):
    return cv2.resize(img, (img.shape[1] // down, img.shape[0] // down), interpolation=cv2.INTER_AREA)


def sample_cells(small, down, cam, X, project):
    """Low-resolution colours (ch, cw, 3) float32 of the shrunk photo at world points X; NaN outside."""
    px, z, _ = project(cam, X)
    px = np.clip(px, -1e6, 1e6)
    sx = ((px[..., 0] + 0.5) / down - 0.5).astype(np.float32)
    sy = ((px[..., 1] + 0.5) / down - 0.5).astype(np.float32)
    col = cv2.remap(small, sx, sy, cv2.INTER_LINEAR, borderMode=cv2.BORDER_CONSTANT).astype(np.float32)
    ok = (sx >= 0) & (sx <= small.shape[1] - 1) & (sy >= 0) & (sy <= small.shape[0] - 1) & (z > 0)
    col[~ok] = np.nan
    return col


def cell_points(f, g, cell, plane_points):
    cw, ch = int(math.ceil(g["W"] / cell)), int(math.ceil(g["H"] / cell))
    cols = np.broadcast_to((np.arange(cw) * cell + cell / 2.0)[None, :], (ch, cw)) - 0.5
    rows = np.broadcast_to((np.arange(ch) * cell + cell / 2.0)[:, None], (ch, cw)) - 0.5
    return plane_points(f, g, cols, rows)
