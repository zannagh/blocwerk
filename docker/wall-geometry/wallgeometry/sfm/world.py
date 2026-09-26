"""The accepted planes as facets of the wall-geometry document: world frame, deterministic ids, extents.

World = A @ Y + b for metric-frame points Y (A a rotation). Without anchors: the marker solver's convention
(frame.py): z = up, x along the reference facet, origin = the reference facet's origin; the reference facet is
the biggest accepted plane (area, then points), the others are numbered by their centre's x, then z. With
anchors: the reference document's world (its up, its origin).

Extents: the 1-99 % box of a facet's inliers in its own (a, b), clipped at the fold line with every adjacent
facet (the half-plane on the facet's own side), origin at the box's (aMin, bMin), so extents start at 0.
"""
import numpy as np
from scipy.spatial import cKDTree

from ..frame import facet_axes

ADJACENT_MM = 200.0
PARALLEL_COS = np.cos(np.radians(10.0))


def order_facets(planes):
    """Accepted planes in id order: the reference first, then by metric-frame centre (set later in world)."""
    acc = [p for p in planes if p["accepted"]]
    return sorted(acc, key=lambda p: (-round(p["areaM2"], 2), -p["npts"]))


def assign_ids(facets, world_pts):
    """facets[0] is the reference ("0"); the rest get "1", "2", ... by world centre x, then z."""
    rest = sorted(facets[1:], key=lambda f: (round(float(world_pts(f)[0]), -1), round(float(world_pts(f)[2]), -1)))
    for i, f in enumerate([facets[0]] + rest):
        f["id"] = str(i)
    return [facets[0]] + rest


def _clip(poly, alpha, beta, gamma, keep_sign):
    """Sutherland-Hodgman: the part of polygon poly (k, 2) with sign(alpha a + beta b + gamma) == keep_sign."""
    out = []
    f = lambda p: keep_sign * (alpha * p[0] + beta * p[1] + gamma)
    for i in range(len(poly)):
        p, q = poly[i], poly[(i + 1) % len(poly)]
        fp, fq = f(p), f(q)
        if fp >= 0:
            out.append(p)
        if (fp >= 0) != (fq >= 0):
            out.append(p + (q - p) * fp / (fp - fq))
    return np.array(out)


def _box(F):
    ab = F["ab"]
    lo, hi = np.percentile(ab, 1, axis=0), np.percentile(ab, 99, axis=0)
    return np.array([[lo[0], lo[1]], [hi[0], lo[1]], [hi[0], hi[1]], [lo[0], hi[1]]])


def extents(facets):
    """Sets origin / extent on every facet (world frame): the clipped 1-99 % box."""
    trees = {id(F): cKDTree(F["P"][:: max(1, len(F["P"]) // 3000)]) for F in facets}
    for F in facets:
        poly = _box(F)
        for G in facets:
            if G is F or abs(F["n"] @ G["n"]) > PARALLEL_COS:
                continue
            sample = G["P"][:: max(1, len(G["P"]) // 3000)]
            if trees[id(F)].query(sample, distance_upper_bound=ADJACENT_MM)[0].min() >= ADJACENT_MM:
                continue
            alpha, beta, gamma = G["n"] @ F["u"], G["n"] @ F["v"], G["n"] @ (F["c"] - G["c"])
            side = np.sign(np.median(F["ab"] @ [alpha, beta] + gamma)) or 1.0
            clipped = _clip(poly, alpha, beta, gamma, side)
            if len(clipped) >= 3:
                poly = clipped
        lo, hi = poly.min(0), poly.max(0)
        F["origin"] = F["c"] + lo[0] * F["u"] + lo[1] * F["v"]
        F["extent"] = {"aMin": 0.0, "aMax": float(hi[0] - lo[0]), "bMin": 0.0, "bMax": float(hi[1] - lo[1])}
    return facets


def build_facets(planes, pts, A, b, up):
    """World facets of the accepted planes: {"id", "plane", "c", "n", "u", "v", "P", "ab", "origin", "extent"}."""
    facets = []
    for pl in order_facets(planes):
        n = A @ pl["n"]
        c = A @ pl["c"] + b
        P = pts[pl["inl"]] @ A.T + b
        u, v = facet_axes(n, up)
        facets.append({"plane": pl, "c": c, "n": n, "u": u, "v": v, "P": P,
                       "ab": np.stack([(P - c) @ u, (P - c) @ v], 1)})
    if not facets:
        return facets
    facets = assign_ids(facets, lambda F: F["P"].mean(0))
    return extents(facets)


def shift_origin(facets, shift):
    """Moves the world origin by -shift (the reference facet's origin becomes 0)."""
    for F in facets:
        F["origin"] = F["origin"] - shift
        F["c"] = F["c"] - shift
        F["P"] = F["P"] - shift
    return facets
