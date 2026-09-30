"""Nearly coplanar facets never overlap: a wall has no two surfaces in one place.

Two facets are nearly coplanar when their normals are < mergeDeg apart and each one's markers lie < mergeMm off the
other's plane (facets.py). When their extents (markers' box + margin) still overlap in-plane, e.g. two panels side by
side whose margins reach over each other, both are clipped at the midline between their marker clusters, along the
axis on which the clusters are separated most. Clusters may reach up to MAX_INTERLEAVE_MM into each other (an
L-shaped seam: The Attic's main wall reaches 15 mm past the left edge of the lower "leftover bit" beside it);
clusters that interleave further on both axes are left alone (nothing to cut along). Every clip is recorded
(`quality.checks.overlapClipped`).
"""
import itertools

import numpy as np

from .facets import DEFAULTS, angle

_TOL_MM = 1.0
MAX_INTERLEAVE_MM = 100.0  # clusters may reach this far into each other (an L-shaped seam) and still be cut apart


def _axis(f, k):
    return f["u"] if k == 0 else f["v"]


def _to_plane(f, pts):
    d = pts - f["origin"]
    return np.stack([d @ f["u"], d @ f["v"]], 1)


def _to_world(f, ab):
    return f["origin"] + ab[:, :1] * f["u"] + ab[:, 1:] * f["v"]


def _lohi(e):
    return np.array([e["aMin"], e["bMin"]]), np.array([e["aMax"], e["bMax"]])


def _rect(e):
    lo, hi = _lohi(e)
    return np.array([lo, [hi[0], lo[1]], hi, [lo[0], hi[1]]])


def _cluster_world(f, ms):
    return _to_world(f, np.vstack([f["ab"][m] for m in ms]))


def _coplanar(f, g, pf, pg, p):
    if angle(f["normal"], g["normal"]) >= p["mergeDeg"]:
        return False
    off = max(np.abs((pg - f["origin"]) @ f["normal"]).max(), np.abs((pf - g["origin"]) @ g["normal"]).max())
    return off < p["mergeMm"]


def _set(e, k, hi, value):
    key = ("aMax", "bMax")[k] if hi else ("aMin", "bMin")[k]
    old = e[key]
    e[key] = float(min(old, value) if hi else max(old, value))
    return e[key] != old


def _cut(cf, cg):
    """(axis, side g lies on, midline) between two clusters in one plane frame; None when they interleave too far."""
    gaps = [(max(cg[:, k].min() - cf[:, k].max(), cf[:, k].min() - cg[:, k].max()), k) for k in (0, 1)]
    gap, k = max(gaps)
    if gap <= -MAX_INTERLEAVE_MM:
        return None
    if cg[:, k].min() - cf[:, k].max() >= cf[:, k].min() - cg[:, k].max():
        return k, 1, (cf[:, k].max() + cg[:, k].min()) / 2
    return k, -1, (cf[:, k].min() + cg[:, k].max()) / 2


def _clip_pair(fid, gid, f, g, core, p):
    pf, pg = _cluster_world(f, core[fid]), _cluster_world(g, core[gid])
    if not _coplanar(f, g, pf, pg, p):
        return None
    flo, fhi = _lohi(f["extent"])
    gr = _to_plane(f, _to_world(g, _rect(g["extent"])))
    ov = np.minimum(fhi, gr.max(0)) - np.maximum(flo, gr.min(0))
    cut = None if np.any(ov <= _TOL_MM) else _cut(_to_plane(f, pf), _to_plane(f, pg))
    if cut is None:
        return None
    k, side, mid = cut
    span = (max(flo[1 - k], gr[:, 1 - k].min()), min(fhi[1 - k], gr[:, 1 - k].max()))
    ends = _to_world(f, np.array([[mid, o] if k == 0 else [o, mid] for o in span]))
    dots = [float(g["u"] @ _axis(f, k)), float(g["v"] @ _axis(f, k))]
    kg = 0 if abs(dots[0]) >= abs(dots[1]) else 1
    g_hi = side * np.sign(dots[kg]) < 0
    cs = (ends - g["origin"]) @ _axis(g, kg)  # both ends of the cut: g stays clear of it however slightly it turns
    changed = _set(f["extent"], k, side > 0, mid)
    changed |= _set(g["extent"], kg, g_hi, float(cs.min() if g_hi else cs.max()))
    if not changed:
        return None
    return {"facets": [fid, gid], "axis": "ab"[k], "overlapMm": [round(float(v), 1) for v in ov],
            "clippedAtWorldMm": ends.mean(0), "reason": "nearly coplanar facets overlapped in-plane: both clipped at "
                                                        "the midline between their marker clusters"}


def clip_overlaps(facets, core, params=None):
    """Clips the `extent` of nearly coplanar facets that overlap in-plane (in place); returns the clip records.

    facets: {fid: {"origin", "u", "v", "normal", "ab": {m: (4, 2)}, "extent"}} in one frame (export._facet);
    core: {fid: [marker ids that span its extent]}."""
    p = {**DEFAULTS, **(params or {})}
    out = []
    for fid, gid in itertools.combinations(sorted(facets), 2):
        if core.get(fid) and core.get(gid):
            rec = _clip_pair(fid, gid, facets[fid], facets[gid], core, p)
            if rec:
                out.append(rec)
    return out
