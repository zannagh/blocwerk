"""Facet extents that one stray marker cannot stretch over a neighbouring facet.

A facet's extent is its markers' bounding box (+ margin, export.py). A marker assigned to a facet but stuck far from
its other markers, INSIDE the region of another coplanar facet, would stretch the extent over that facet (The Attic,
2026-09-30: marker 39 on the "leftover bit" stretched it 1.1 m over the main wall -> textures, coverage and volumes
saw two surfaces in one place). Such a marker stays a member of its facet (it is still solved and observed: the plane,
the cameras and the marker itself keep it); only the extent leaves it out. A marker is left out when
  * it lies outside the bounding box of its own facet's other markers (+ margin), AND
  * it lies on another facet's plane (normal < mergeDeg, every corner < mergeMm off it) inside that facet's
    markers' bounding box (+ margin).
A facet always keeps at least one marker in its extent. The facet origin (bottom-left of ALL its markers) is unchanged,
so the world frame and every marker's plane coordinates stay what they were; only aMin..bMax shrink.
"""
import numpy as np

from .facets import DEFAULTS, angle


def _box(ab, ms, margin):
    pts = np.vstack([ab[m] for m in ms])
    return pts.min(0) - margin, pts.max(0) + margin


def _inside(p, box):
    return bool(np.all(p >= box[0]) and np.all(p <= box[1]))


def _plane_ab(f, corners):
    """(a, b) of world corners in facet f's frame, and their max |distance| from its plane."""
    d = corners - f["origin_w"]
    return np.stack([d @ f["u"], d @ f["v"]], 1), float(np.abs(d @ f["normal"]).max())


def stray_markers(facets, corners, margin, params=None):
    """{facet id: [marker ids left out of its extent]}.

    facets: {fid: {"origin_w", "u", "v", "normal", "ab": {m: (4, 2) plane corners}}} in one frame with `corners`
    ({m: (4, 3) corners}). Regions are the markers' bounding boxes grown by `margin` (mm)."""
    p = {**DEFAULTS, **(params or {})}
    boxes = {fid: _box(f["ab"], list(f["ab"]), margin) for fid, f in facets.items()}
    out = {}
    for fid, f in facets.items():
        ms = sorted(f["ab"])
        for m in ms:
            rest = [k for k in ms if k != m and k not in out.get(fid, [])]
            if not rest or _inside(f["ab"][m].mean(0), _box(f["ab"], rest, margin)):
                continue
            for gid, g in facets.items():
                if gid == fid or angle(f["normal"], g["normal"]) >= p["mergeDeg"]:
                    continue
                ab, off = _plane_ab(g, corners[m])
                if off < p["mergeMm"] and _inside(ab.mean(0), boxes[gid]):
                    out.setdefault(fid, []).append(m)
                    break
    return out
