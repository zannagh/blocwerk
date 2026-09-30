"""Which facets block a camera's view of a point on another facet (texture photo choice).

A facet occludes a plane point X for a camera only where the facet really is: the straight segment
from X to the camera centre must cross the occluder's plane (X more than `tol` off it) at a point
inside the occluder's REGION. Facets are never treated as infinite planes, and "behind another facet's
plane" alone never hides anything (a far perpendicular panel whose plane merely passes behind the
wall must not blank it).

The region is the facet's extent rectangle (markers' bounding box + margin), cut along the seams with
its neighbours where the facet's own markers all lie on one side: a triangle segment (side wall,
closing piece) keeps only its real half, so its empty half-rectangle blocks nothing. A seam is only
used where the neighbour really is (the seam line runs, on average, within `MAX_SEAM_GAP_MM` of the
neighbour's extent), the same rule as the 3D viewer's triangle outlines (Wall3DFacetOutlines.cs).
"""
import numpy as np

MIN_PLANE_ANGLE_SIN = 0.17  # ~10 deg: closer to parallel gives no reliable seam
MAX_SEAM_GAP_MM = 800.0
MARKER_SIDE_TOL_MM = 20.0  # a marker corner this close to a seam still counts as on either side


class Occluder:
    """One facet as a blocker: plane frame, extent rectangle and half-planes (alpha, beta, gamma) in
    its (a, b) frame; a point is inside when alpha * a + beta * b >= gamma for every half-plane."""

    def __init__(self, f, halfplanes=()):
        self.id = f["id"]
        self.O, self.u, self.v, self.n = (np.array(f[k], float) for k in ("origin", "u", "v", "normal"))
        self.e = f["extentMm"]
        self.halfplanes = list(halfplanes)

    def contains(self, a, b, pad=0.0):
        e = self.e
        ok = (a >= e["aMin"] - pad) & (a <= e["aMax"] + pad) & (b >= e["bMin"] - pad) & (b <= e["bMax"] + pad)
        for al, be, ga in self.halfplanes:
            ok &= al * a + be * b >= ga - pad
        return ok


def _seam(g, h):
    """Signed distance (mm) in g's plane from the line where it meets h's plane, as (alpha, beta,
    gamma) with s = alpha * a + beta * b - gamma (> 0 in front of h); None when near-parallel."""
    nh = np.array(h["normal"], float)
    Og, ug, vg = (np.array(g[k], float) for k in ("origin", "u", "v"))
    al, be = float(nh @ ug), float(nh @ vg)
    norm = np.hypot(al, be)
    if norm < MIN_PLANE_ANGLE_SIN:
        return None
    ga = float(nh @ (np.array(h["origin"], float) - Og))
    return al / norm, be / norm, ga / norm


def _rect_distance(e, a, b):
    da = np.maximum(np.maximum(e["aMin"] - a, 0), a - e["aMax"])
    db = np.maximum(np.maximum(e["bMin"] - b, 0), b - e["bMax"])
    return np.hypot(da, db)


def _seam_gap(g, h, seam):
    """Mean distance of the seam line's part inside g's extent from h's extent (in h's plane)."""
    al, be, ga = seam
    e = g["extentMm"]
    # parametrise the line: point p0 + t * dir, dir along the line in (a, b)
    p0 = np.array([al, be]) * ga
    dr = np.array([-be, al])
    ts = []
    for k, lo, hi in ((0, e["aMin"], e["aMax"]), (1, e["bMin"], e["bMax"])):
        if abs(dr[k]) > 1e-9:
            ts += [(lo - p0[k]) / dr[k], (hi - p0[k]) / dr[k]]
    ts = sorted(ts)
    if len(ts) < 2:
        return np.inf
    t = np.linspace(ts[len(ts) // 2 - 1], ts[len(ts) // 2], 11)  # the segment inside the rectangle
    ab = p0 + t[:, None] * dr
    inside = (ab[:, 0] >= e["aMin"] - 1) & (ab[:, 0] <= e["aMax"] + 1) & (ab[:, 1] >= e["bMin"] - 1) & (ab[:, 1] <= e["bMax"] + 1)
    if not inside.any():
        return np.inf
    Og, ug, vg = (np.array(g[k], float) for k in ("origin", "u", "v"))
    W = Og + ab[inside, :1] * ug + ab[inside, 1:] * vg
    Oh, uh, vh = (np.array(h[k], float) for k in ("origin", "u", "v"))
    return float(_rect_distance(h["extentMm"], (W - Oh) @ uh, (W - Oh) @ vh).mean())


def _halfplanes(g, facets, corners):
    """Seam cuts of g's rectangle that leave all of g's marker corners (a, b) on the kept side."""
    if len(corners) == 0:
        return []
    e = g["extentMm"]
    rect = np.array([[e["aMin"], e["bMin"]], [e["aMax"], e["bMin"]], [e["aMax"], e["bMax"]], [e["aMin"], e["bMax"]]])
    out = []
    for h in facets:
        if h["id"] == g["id"] or (seam := _seam(g, h)) is None:
            continue
        al, be, ga = seam
        s_mk = corners @ [al, be] - ga
        s_rect = rect @ [al, be] - ga
        for sign in (1.0, -1.0):
            if (sign * s_mk >= -MARKER_SIDE_TOL_MM).all() and (sign * s_rect < -MARKER_SIDE_TOL_MM).any():
                if _seam_gap(g, h, seam) <= MAX_SEAM_GAP_MM:
                    out.append((sign * al, sign * be, sign * ga))
                break
    return out


def occluders(facets, markers):
    """Occluder per facet (region = extent cut by its marker-confirmed seams)."""
    by_facet = {}
    for m in markers or []:
        if m.get("facet") is not None and m.get("cornersPlaneMm"):
            by_facet.setdefault(str(m["facet"]), []).extend(m["cornersPlaneMm"])
    return [Occluder(g, _halfplanes(g, facets, np.array(by_facet.get(str(g["id"]), []), float).reshape(-1, 2)))
            for g in facets]


def hidden(centre, occs, X, tol):
    """Points X (N, 3) whose segment to the camera centre passes through another facet's region."""
    out = np.zeros(len(X), bool)
    for o in occs:
        dX = (X - o.O) @ o.n
        dC = float((centre - o.O) @ o.n)
        cross = (np.abs(dX) > tol) & (np.sign(dX) != np.sign(dC)) & (abs(dC) > 1e-6)
        if not cross.any():
            continue
        idx = np.nonzero(cross & ~out)[0]
        Xi = X[idx]
        t = dX[idx] / (dX[idx] - dC)
        P = Xi + t[:, None] * (centre - Xi)
        out[idx] = o.contains((P - o.O) @ o.u, (P - o.O) @ o.v)
    return out
