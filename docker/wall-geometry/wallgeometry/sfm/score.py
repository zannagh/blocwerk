"""Which planes are climbing-wall facets (Phase 0: exactly main wall, side panel and kickboard on The Attic; floor,
mats, room walls, rafters and hold / volume slabs rejected every time).

Per plane (metric frame, mm): hold hits (the photos' hold detections cast as rays; the biggest supported plane
within 150 mm behind the first one along each ray gets the hit; a hit on a feature lying on a facet counts for
that facet), camera facing (share of the photos in front of it looking at it), area (occupied
100 mm cells). Hard rules: area >= minFacetAreaM2 (0.4 m2), filling >= 40 % of its 1-99 % box (a plane
through scattered hold clutter is a sparse band), not horizontal when gravity is known, and a smaller plane
lying in front of a bigger accepted one (its points over the big one's footprint, within 300 mm) is a feature
ON that facet (volume face, big hold, a hold layer), not a facet.
"""
import numpy as np
from scipy.spatial import ConvexHull, QhullError, cKDTree

from .cameras import world_rays
from .gravity import is_horizontal
from .planes import plane_ab

CELL_MM = 100.0
HOLD_SUPPORT_MM = 80.0
HIT_DEPTH_MM = 150.0
FEATURE_ON_MM = 300.0
HULL_MARGIN_MM = 50.0
ON_COS = np.cos(np.radians(40.0))  # "in front of" means roughly parallel: a fold neighbour (kickboard) is not
MIN_HOLD_RAYS = 20
MIN_FILL = 0.4  # occupied area / its 1-99 % box (PCA-aligned): a plane through scattered clutter is a sparse band


def _hull(ab):
    """Half-plane equations (k, 3) of the convex outline of the DENSE 100 mm cells of in-plane points (>= 1/5 of
    the median cell count: stray inliers far off the surface must not stretch it), or None."""
    keys, counts = np.unique(np.floor(ab / CELL_MM).astype(np.int64), axis=0, return_counts=True)
    dense = keys[counts >= max(2, 0.2 * np.median(counts))]
    corners = (dense[:, None, :] + np.array([[0, 0], [1, 0], [0, 1], [1, 1]])[None]).reshape(-1, 2) * CELL_MM
    try:
        return ConvexHull(corners).equations
    except (QhullError, ValueError):
        return None


def _box_m2(ab):
    """Area (m2) of the 1-99 % box of in-plane points along their principal axes."""
    q = ab - ab.mean(0)
    rot = np.linalg.svd(q, full_matrices=False)[2] if len(q) >= 2 else np.eye(2)
    r = q @ rot.T
    span = np.percentile(r, 99, axis=0) - np.percentile(r, 1, axis=0)
    return max(float(span[0] * span[1]) / 1e6, (CELL_MM / 1000) ** 2)


def describe(planes, pts, photos):
    """Adds n (toward the photos), npts, areaM2, rmsMm, facing, ab tree to every plane."""
    C = np.array([im["C"] for im in photos])
    fwd = np.array([im["R"][2] for im in photos])
    for pl in planes:
        if ((C - pl["c"]) @ pl["n"]).mean() < 0:
            pl["n"] = -pl["n"]
        q = pts[pl["inl"]]
        ab = plane_ab(q, pl["c"], pl["n"])
        cells = np.unique(np.floor(ab / CELL_MM).astype(np.int64), axis=0)  # occupied 100 mm cells
        front = (C - pl["c"]) @ pl["n"] > 0
        area = len(cells) * (CELL_MM / 1000) ** 2
        pl.update(npts=int(len(q)), areaM2=area, fill=area / _box_m2(ab), hull=_hull(ab),
                  rmsMm=float(np.sqrt((((q - pl["c"]) @ pl["n"]) ** 2).mean())), tree=cKDTree(ab),
                  facing=float((front & (fwd @ -pl["n"] > 0.5)).mean()))
    return planes


def hold_hits(planes, model, holds, to_metric):
    """Hits per plane of the hold rays and the number of rays. A ray's hit goes to the BIGGEST supported plane
    within HIT_DEPTH_MM behind the first one along it: a hold stands <= 80 mm proud of its panel, so a small
    plane just in front (a hold layer, a tilted piece of it) must not take the panel's holds."""
    hits, rays = np.zeros(len(planes), int), 0
    size = np.array([pl["npts"] for pl in planes], float)
    for im in model["images"]:
        xy = holds.get(im["stem"]) if im["role"] == "photo" else None
        if not xy or not planes:
            continue
        centre, dirs = world_rays(im, model["cams"][im["cam"]], xy)
        centre, dirs = to_metric(centre[None])[0], to_metric(dirs, direction=True)
        rays += len(dirs)
        T = np.full((len(planes), len(dirs)), np.inf)
        for k, pl in enumerate(planes):
            den = dirs @ pl["n"]
            with np.errstate(divide="ignore", invalid="ignore"):
                t = ((pl["c"] - centre) @ pl["n"]) / den
            ok = (np.abs(den) > 1e-9) & (t > 0)
            if not ok.any():
                continue
            x = centre + t[ok, None] * dirs[ok]
            d = pl["tree"].query(plane_ab(x, pl["c"], pl["n"]), distance_upper_bound=HOLD_SUPPORT_MM)[0]
            sel = np.flatnonzero(ok)[d < HOLD_SUPPORT_MM]
            T[k, sel] = t[sel]
        first = T.min(0)
        near = T <= first + HIT_DEPTH_MM
        best = np.where(near, size[:, None], -1.0).argmax(0)
        np.add.at(hits, best[np.isfinite(first)], 1)
    return hits, rays


def judge(planes, up, gravity_known, hits, rays, min_area):
    """Sets score / accepted / reason on every plane."""
    use_holds = rays >= MIN_HOLD_RAYS
    total_hits, total_pts = max(int(hits.sum()), 1), max(sum(p["npts"] for p in planes), 1)
    for k, pl in enumerate(planes):
        share = hits[k] / total_hits
        pl["holdHitShare"] = float(share) if use_holds else None
        pl["holdHits"] = int(hits[k])
        if use_holds:
            pl["score"] = 0.6 * min(share / 0.02, 1) + 0.25 * pl["facing"] + 0.15 * min(pl["areaM2"] / 2, 1)
        else:  # no detections: facing, area and point support (weaker; README)
            pl["score"] = 0.5 * min(pl["facing"] / 0.2, 1) + 0.25 * min(pl["areaM2"] / 2, 1) \
                + 0.25 * min(pl["npts"] / total_pts / 0.1, 1)
        reason = None
        if gravity_known and is_horizontal(pl["n"], up):
            reason = "horizontal"
        elif pl["areaM2"] < min_area:
            reason = "small"
        elif pl["fill"] < MIN_FILL:
            reason = "sparse"
        elif use_holds and share < 0.01:
            reason = "no holds"
        elif not use_holds and pl["facing"] < 0.05:
            reason = "not facing the cameras"
        elif pl["score"] < 0.5:
            reason = "low score"
        pl["accepted"], pl["reason"] = reason is None, reason
    return planes


def _lies_on(pl, big, pts):
    """Is plane pl a feature ON plane big: smaller (<= 1/3 of the points), within 40 deg of parallel, and its
    points mostly (> 60 %) over big's footprint (its convex outline: big's own points have holes exactly where
    features took them) and in front of it within 300 mm? Hold layers and tilted volume faces are (The Attic:
    0-32 deg); a neighbour across a fold (the kickboard at 45 deg) is not."""
    if pl is big or pl["npts"] * 3 > big["npts"] or abs(pl["n"] @ big["n"]) < ON_COS or big["hull"] is None:
        return False
    q = pts[pl["inl"]]
    h = (q - big["c"]) @ big["n"]
    inside = (plane_ab(q, big["c"], big["n"]) @ big["hull"][:, :2].T + big["hull"][:, 2] <= HULL_MARGIN_MM).all(1)
    return (inside & (np.abs(h) < FEATURE_ON_MM)).mean() > 0.6 and np.median(h) > -2 * big["rmsMm"]


def hosts(planes, pts):
    """Per plane the index of the biggest plane it lies on (a feature on it), or None."""
    order = sorted(range(len(planes)), key=lambda k: -planes[k]["npts"])
    return [next((b for b in order if _lies_on(planes[k], planes[b], pts)), None) for k in range(len(planes))]


def credit_hosts(hits, host):
    """A hold on a hold layer or a volume is a hold on the facet under it: hits move to the host plane."""
    out = hits.copy()
    for k, h in enumerate(host):
        if h is not None:
            out[h] += hits[k]
    return out


def reject_features_on(planes, host):
    """A plane lying on an accepted plane is not a facet itself."""
    for k, h in enumerate(host):
        if h is not None and planes[k]["accepted"] and planes[h]["accepted"]:
            planes[k]["accepted"], planes[k]["reason"] = False, "feature on a bigger facet"
    return planes
