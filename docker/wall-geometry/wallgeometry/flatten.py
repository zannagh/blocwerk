"""Even shading per overhang group: facets at the same angle get one flat illumination level.

The room's lamps light a big overhang unevenly (a bright patch near a lamp, a darker far corner), and
two facets at the same angle next to each other (a main wall and its cornered extension) should read
as one evenly lit surface. Facets are grouped by overhang angle (within `flattenGroupDeg`); within a
group every facet's very smooth luminance field is estimated and divided out towards ONE common
target level for the group. Groups keep their own level: a vertical kickboard stays brighter than
the overhang above it, as it is in the room.

The field is the log luminance of the wall itself: a quadratic surface plus its residual blurred at
`flattenSigmaMm` (normalised convolution), both over plywood pixels only, and never extrapolated beyond
the range it takes on them. Holds (saturated colours), markers and anything that is not plywood (much
darker than the group, e.g. the space beyond a facet's real edge) are rejected up front, then pixels
far from the current field are dropped over a few passes. The correction is a
grey gain (all channels alike, so no hold changes colour) of only the lowest frequencies, bounded to
`flattenMaxGain` and faded out away from the plywood it was fitted on; local detail is untouched.
"""
import math

import cv2
import numpy as np

from .seams import _Tex, _vec

FLATTEN_DEFAULTS = {"flattenShading": True, "flattenGroupDeg": 3.0, "flattenSigmaMm": 500.0,
                    "flattenDownscale": 8, "flattenMaxChroma": 40.0, "flattenDarkLog": 0.6,
                    "flattenOutlierLog": 0.2, "flattenPasses": 3, "flattenMaxGain": 1.8,
                    "flattenOffWallMm": 250.0}


def overhang_deg(f):
    """Overhang of a facet in degrees: 0 vertical, > 0 leaning over the climber (world z is up)."""
    return math.degrees(math.asin(float(np.clip(-_vec(f, "normal")[2], -1, 1))))


def groups(facets, tol):
    """Facets grouped by overhang angle: each group spans at most `tol` degrees."""
    order = sorted(facets, key=overhang_deg)
    out = []
    for f in order:
        if out and overhang_deg(f) - overhang_deg(out[-1][0]) <= tol:
            out[-1].append(f)
        else:
            out.append([f])
    return out


def _wall_pixels(T, p):
    """Plywood candidates of one facet: covered, not a saturated hold colour."""
    small = np.exp(T.log).astype(np.float32) / 255.0
    lab = cv2.cvtColor(small, cv2.COLOR_BGR2Lab)
    chroma = np.hypot(lab[..., 1], lab[..., 2])
    return T.ok & (chroma < float(p["flattenMaxChroma"]))


def _luma(T):
    return T.log.mean(-1)  # log of the geometric channel mean


def _smooth(y, m, sigma):
    """Very smooth fit of y over mask m: a quadratic surface (follows gradients right up to the facet's
    border) plus its residual blurred at `sigma` (normalised convolution) for broader bumps."""
    h, w = y.shape
    r, c = np.mgrid[0:h, 0:w].astype(np.float64)
    r, c = r / max(h, w), c / max(h, w)
    basis = np.stack([np.ones_like(r), r, c, r * r, r * c, c * c], -1)
    coef = np.linalg.lstsq(basis[m], y[m].astype(np.float64), rcond=None)[0]
    poly = (basis @ coef).astype(np.float32)
    res = np.where(m, y - poly, 0).astype(np.float32)
    num = cv2.GaussianBlur(res, (0, 0), sigma)
    den = cv2.GaussianBlur(m.astype(np.float32), (0, 0), sigma)
    # where the mask is thin the residual is unreliable: fall back to the surface alone
    fit = poly + num / np.maximum(den, 1e-6) * np.clip(den / 0.3, 0, 1)
    return np.clip(fit, fit[m].min(), fit[m].max())


def _fields(texs, p):
    """Per facet: smooth log-luminance field (h, w) and the wall mask used; plus the group target."""
    ys = [_luma(T) for T in texs]
    ms = [_wall_pixels(T, p) for T in texs]
    pooled = np.concatenate([y[m] for y, m in zip(ys, ms)]) if any(m.any() for m in ms) else np.zeros(0)
    if pooled.size == 0:
        return None, None
    level = float(np.median(pooled))
    ms = [m & (y > level - float(p["flattenDarkLog"])) for y, m in zip(ys, ms)]
    fields = [None] * len(texs)
    for _ in range(int(p["flattenPasses"])):
        for k, T in enumerate(texs):
            if ms[k].sum() < 50:
                fields[k] = None
                continue
            sigma = float(p["flattenSigmaMm"]) / T.g["res"]
            fields[k] = _smooth(ys[k], ms[k], sigma)
            ms[k] &= np.abs(ys[k] - fields[k]) < float(p["flattenOutlierLog"])
    pooled = np.concatenate([fields[k][ms[k]] for k in range(len(texs)) if fields[k] is not None])
    return fields, float(np.median(pooled)), ms


def flatten(results, facets, params=None):
    """results: render_textures' list; facets: id -> facet. Evens out the shading of each overhang
    group in place; returns {facet id: {"group": [...], "overhangDeg", "gainMin", "gainMax"}}."""
    p = {**FLATTEN_DEFAULTS, **(params or {})}
    by_id = {r["facet"]: r for r in results}
    report = {}
    for grp in groups([facets[k] for k in by_id], float(p["flattenGroupDeg"])):
        texs = [_Tex(f, by_id[f["id"]], int(p["flattenDownscale"])) for f in grp]
        fields, target, masks = _fields(texs, p)
        if fields is None:
            continue
        cap = math.log(float(p["flattenMaxGain"]))
        for T, F, m in zip(texs, fields, masks):
            if F is None:
                continue
            gain = np.clip(target - F, -cap, cap).astype(np.float32) * _near_wall(T, m, p)
            _apply_gain(T.r, gain)
            report[T.f["id"]] = {"group": [f["id"] for f in grp], "overhangDeg": round(overhang_deg(T.f), 1),
                                 "gainMin": round(float(np.exp(gain.min())), 3),
                                 "gainMax": round(float(np.exp(gain.max())), 3)}
    return report


def _near_wall(T, m, p):
    """1 on and near the plywood the field was fitted on, fading to 0 (no change) over
    `flattenOffWallMm` beyond it: what is not wall (the space past a facet's real edge) is left alone."""
    closed = cv2.morphologyEx(m.astype(np.uint8), cv2.MORPH_CLOSE, np.ones((9, 9), np.uint8))
    d = cv2.distanceTransform((1 - closed).astype(np.uint8), cv2.DIST_L2, 5) * T.g["res"]
    return np.exp(-0.5 * (d / float(p["flattenOffWallMm"])) ** 2).astype(np.float32)


def _apply_gain(r, log_gain):
    img = r["image"]
    H, W = img.shape[:2]
    g = np.exp(cv2.resize(log_gain, (W, H), interpolation=cv2.INTER_CUBIC))
    r["image"] = np.clip(img.astype(np.float32) * g[..., None] + 0.5, 0, 255).astype(np.uint8)
