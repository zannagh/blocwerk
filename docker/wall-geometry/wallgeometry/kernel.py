"""The wall geometry kernel's rules and constants, shared with the C# side (docs/geometry-kernel.md).

Where a facet really is (its shape), which side of a seam it keeps, which markers say so, and when a line of sight
counts as blocked are ONE rule in both languages: the C# twin is src/Blocwerk.Core/Geometry/GeometryKernel.cs, and
test/geometry-golden/*.json holds the cases both must answer the same way. Change a value here only together with
the C# twin and the doc.
"""
import math

import numpy as np

# Planes closer to parallel than this (sine of their angle, ~9.8 deg) meet in no reliable seam line.
MIN_PLANE_ANGLE_SIN = 0.17
# ... the same as an angle: nearly coplanar facets below it are clipped apart at their midline instead (overlaps.py).
SEAM_MIN_ANGLE_DEG = math.degrees(math.asin(MIN_PLANE_ANGLE_SIN))
# A seam cuts a facet only where the neighbour really is: the seam runs, on average, within this of its region (mm).
MAX_SEAM_GAP_MM = 800.0
# A marker corner this close to a seam still counts as on either side of it (mm).
MARKER_SIDE_TOL_MM = 20.0
# A target this close to an occluder's plane is on that occluder's seam: the occluder does not block it (mm).
NEAR_PLANE_MM = 30.0
# A camera this close to an occluder's plane does not cross it (mm).
ON_PLANE_MM = 1e-6
# A view counts only from the front of the surface: cos(view ray, outward normal) above this (~87 deg).
MIN_FACING_COS = 0.05
# Outline edges this close to the extent rectangle's sides are the rectangle itself, not a fold clip (mm).
OUTLINE_EDGE_TOL_MM = 1.0


def extent_excluded(doc):
    """Ids of the markers the solver left out of their facet's extent (strays on a coplanar neighbour, extents.py):
    flagged `extentExcluded`, or listed by an `extentExcluded` facet decision (documents older than the flag)."""
    out = {m["id"] for m in doc.get("markers") or [] if m.get("extentExcluded")}
    for d in (doc.get("quality") or {}).get("facetDecisions") or []:
        if isinstance(d, dict) and d.get("kind") == "extentExcluded":
            out.update(m for m in d.get("markers") or [] if isinstance(m, int))
    return out


def voting_corners(facet, markers, excluded=()):
    """(n, 2) plane corners of the markers that vote on facet's seam sides: all of its own markers except those the
    solver left out of its extent (`excluded`, see extent_excluded)."""
    fid = str(facet["id"])
    out = [np.array(m["cornersPlaneMm"], float)[:, :2] for m in markers or []
           if m.get("facet") is not None and str(m["facet"]) == fid and m.get("cornersPlaneMm")
           and m.get("id") not in excluded and not m.get("extentExcluded")]
    return np.vstack(out) if out else np.zeros((0, 2))


def usable_extent(facet):
    """The facet's extentMm, or None when it has none or it has no area (C#: PlaneRectMm { Area: > 0 })."""
    e = facet.get("extentMm")
    return e if e and e["aMax"] > e["aMin"] and e["bMax"] > e["bMin"] else None


def outline_halfplanes(facet):
    """The fold clips of a facet's `outlineMm` (a convex polygon in its (a, b); SfM models) as unit half-planes
    (alpha, beta, gamma), kept where alpha * a + beta * b >= gamma. Edges along the extent rectangle's sides are
    the rectangle itself and are skipped."""
    poly = facet.get("outlineMm")
    if not poly or len(poly) < 3:
        return []
    P = np.array(poly, float)[:, :2]
    area = float(np.sum(P[:, 0] * np.roll(P[:, 1], -1) - np.roll(P[:, 0], -1) * P[:, 1])) / 2
    if abs(area) < 1e-9:
        return []
    e, tol, out = usable_extent(facet), OUTLINE_EDGE_TOL_MM, []
    for p, q in zip(P, np.roll(P, -1, 0)):
        d = q - p
        length = float(np.hypot(*d))
        if length < tol or (e and _on_rect_side(p, q, e, tol)):
            continue
        al, be = (-d[1], d[0]) if area > 0 else (d[1], -d[0])  # inward normal
        al, be = al / length, be / length
        out.append((al, be, al * p[0] + be * p[1]))
    return out


def _on_rect_side(p, q, e, tol):
    for k, lo, hi in ((0, e["aMin"], e["aMax"]), (1, e["bMin"], e["bMax"])):
        for x in (lo, hi):
            if abs(p[k] - x) <= tol and abs(q[k] - x) <= tol:
                return True
    return False


def facing(normal, X, centre):
    """Whether the camera at `centre` sees points X (N, 3) from the front of a surface with unit `normal`."""
    ray = np.asarray(centre, float) - X
    dist = np.linalg.norm(ray, axis=-1)
    return (ray @ np.asarray(normal, float)) / np.maximum(dist, 1e-9) > MIN_FACING_COS
