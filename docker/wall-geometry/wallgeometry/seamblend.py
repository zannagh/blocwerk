"""Seam-free single-photo choice ("select" mode): smooth label boundaries and a two-band blend.

The per-pixel photo choice is made on the coarse label-cell grid (16 mm) and upsampled, so the raw
boundary between two photos is a staircase of cell-sized steps. Two things hide it:

* The boundary is softened with a Gaussian (`seamBlendPx`, about one cell) instead of a box over a few
  pixels, which turns the staircase into a smooth curve (detail band: sharp, little ghosting).
* Colour and brightness differences between photos (lighting that the global per-photo gain cannot
  correct) are blended over a much wider zone (`seamWidePx`) in a low-pass band only: a two-band
  Laplacian blend. With L_k the low-pass of slot k, w_n the narrow and w_w the wide weights,
      out = sum_k w_n * img_k + upsample(sum_k (w_w - w_n) * L_k)
  equals the detail of the narrow blend plus the low band of the wide one. The low band is computed at
  reduced resolution, so it costs little.

Everything is deterministic and works in row tiles with a halo (`halo_rows`) so tiling changes nothing.
"""
import math

import cv2
import numpy as np

MAX_LOW_STEP = 12.0
LOW_DOWN = 4  # the low band is computed at 1 / LOW_DOWN of the texture resolution


def halo_rows(p):
    """Rows of context a tile needs above and below (3 sigma of the widest blur, a multiple of LOW_DOWN)."""
    r = 3.0 * max(float(p["seamWidePx"]), float(p["seamBlendPx"]))
    return int(math.ceil(r / LOW_DOWN)) * LOW_DOWN


def _blur(a, sigma):
    return cv2.GaussianBlur(a, (0, 0), sigma, borderType=cv2.BORDER_REPLICATE) if sigma > 0.3 else a


def _normalise(w, valid, best):
    """Per-pixel weights (K, h, w) from raw slot weights: zero where a slot is empty; where no chosen
    photo has a slot, the best-weighted slot."""
    w = np.where(valid, w, 0).astype(np.float32)
    none = w.sum(0) <= 1e-6
    w = np.where(none[None], best, w)
    tot = w.sum(0)
    return w / np.where(tot > 0, tot, 1)


def _best(wt):
    return (np.arange(wt.shape[0])[:, None, None] == wt.argmax(0)[None]) & (wt > 0)


def _box(mask, reach):
    """Row / column ranges (r0, r1, c0, c1) of `mask` grown by `reach`, or None when empty."""
    rows, cols = np.flatnonzero(mask.any(1)), np.flatnonzero(mask.any(0))
    if rows.size == 0:
        return None
    h, w = mask.shape
    return (max(rows[0] - reach, 0), min(rows[-1] + 1 + reach, h),
            max(cols[0] - reach, 0), min(cols[-1] + 1 + reach, w))


def narrow_weights(cam, wt, label, sigma, inner):
    """Detail-band weights (K, rows of `inner`, w). A photo's weight at a pixel is the Gaussian-blurred
    indicator of the photo's own label region: the blur is per PHOTO, not per slot (the slot a photo
    occupies differs from pixel to pixel). cam, wt, label cover the tile plus its halo."""
    K, h, w = wt.shape
    reach = int(math.ceil(3 * sigma))
    raw = np.zeros((K,) + wt[:, inner].shape[1:], np.float32)
    for c in np.unique(label[label >= 0]):
        reg = label == c
        box = _box(reg, reach)
        r0, r1, c0, c1 = box
        blurred = _blur(reg[r0:r1, c0:c1].astype(np.float32), sigma)
        y0, y1 = max(r0, inner.start), min(r1, inner.stop)
        if y1 <= y0:
            continue
        b = blurred[y0 - r0:y1 - r0]
        sl = (slice(None), slice(y0 - inner.start, y1 - inner.start), slice(c0, c1))
        raw[sl] += np.where(cam[:, y0:y1, c0:c1] == c, b[None], 0)
    return _normalise(raw, wt[:, inner] > 0, _best(wt[:, inner]))


def _small(a, down):
    h, w = a.shape[:2]
    return cv2.resize(a, (-(-w // down), -(-h // down)), interpolation=cv2.INTER_AREA)


def low_band(rgb, cam, wt, label, p, inner):
    """The wide-zone colour correction (rows of `inner`, w, 3) float32, see the module docstring. Done
    per photo at reduced resolution: photo c's low-pass colour L_c, weighted by the blurred indicator
    of its label region (narrow and wide)."""
    K, h, w, _ = rgb.shape
    d = LOW_DOWN
    s_band, s_wide, s_narrow = (float(p[k]) / d for k in ("seamBandPx", "seamWidePx", "seamBlendPx"))
    valid = wt > 0
    cs = cam[:, d // 2::d, d // 2::d].astype(np.int32)  # slot identity at the block centres
    vs = valid[:, d // 2::d, d // 2::d]
    ls = label[d // 2::d, d // 2::d]
    hs, ws = cs.shape[1:]
    rs = np.stack([_small(rgb[k].astype(np.float32) * valid[k][..., None], d)[:hs, :ws] for k in range(K)])
    sums = {n: np.zeros((hs, ws), np.float32) for n in ("n", "w")}
    acc = {n: np.zeros((hs, ws, 3), np.float32) for n in ("n", "w")}
    for c in np.unique(ls[ls >= 0]):
        m = (cs == c) & vs
        have = m.any(0)
        if not have.any():
            continue
        img = (rs * m[..., None]).sum(0)
        low = _blur(img, s_band) / np.maximum(_blur(have.astype(np.float32), s_band), 1e-3)[..., None]
        reg = (ls == c).astype(np.float32)
        for n, sg in (("n", s_narrow), ("w", s_wide)):
            wc = _blur(reg, sg) * have
            sums[n] += wc
            acc[n] += wc[..., None] * low
    ok = (sums["n"] > 1e-6) & (sums["w"] > 1e-6)
    corr = np.where(ok[..., None], acc["w"] / np.maximum(sums["w"], 1e-6)[..., None]
                    - acc["n"] / np.maximum(sums["n"], 1e-6)[..., None], 0).astype(np.float32)
    # a photo that differs by far more than exposure does (an occluder only it sees) must not tint the
    # wall around it: the wide blend only evens out differences up to MAX_LOW_STEP grey levels
    corr = np.clip(corr, -MAX_LOW_STEP, MAX_LOW_STEP)
    up = cv2.resize(corr, (w, h), interpolation=cv2.INTER_LINEAR)
    return up[inner]


def combine(rgb, wt, cam, label, p, inner):
    """Slots of a tile plus halo -> (image float32 (rows of inner, w, 3), weights (K, rows, w)).
    rgb (K, h, w, 3) uint8 (gain-corrected), wt (K, h, w), cam (K, h, w) int, label (h, w) full-res
    photo index per pixel (-1 none)."""
    w_n = narrow_weights(cam, wt, label, float(p["seamBlendPx"]), inner)
    img = (rgb[:, inner].astype(np.float32) * w_n[..., None]).sum(0)
    if float(p["seamWidePx"]) > float(p["seamBlendPx"]) * 1.5:
        img = img + low_band(rgb, cam, wt, label, p, inner)
    return img, w_n
