"""Scale (mm per model unit) of a feature model, first source that works: the anchors (anchors.py) -> one
measured distance (two taps in one photo + mm, tap-to-plane) -> an estimate from the camera height above the
floor (1.45 m, Phase 0: -0.1 .. +9.4 %; `scaleSource: "estimate"`; the floor is the RANSAC floor plane, else
the height histogram of horizontal points below the cameras), or without a floor from the typical distance of a
point to its nearest photo (1.0 m; +-15-20 % on The Attic, rougher still).
"""
import numpy as np
from scipy.spatial import cKDTree

from .cameras import world_rays
from .planes import plane_ab

MIN_FLOOR_POINTS = 30  # The Attic, 53 photos: 52 floor points
PLAUSIBLE_NEAREST_MM = (400.0, 3000.0)  # a camera-height scale must put the photos this far from the wall
NEAREST_CAMERA_MM = 1000.0  # The Attic: 0.91 m (53 photos + 300 frames), 1.14 m (53 photos)


def camera_height(model, floor, prior_mm, spread):
    """(mm per unit or None, info): the median photo height above the floor plane = prior_mm; refused when it
    puts the median point-to-nearest-photo distance (spread, model units) outside PLAUSIBLE_NEAREST_MM."""
    h = np.array([(im["C"] - floor["c"]) @ floor["n"] for im in model["images"] if im["role"] == "photo"])
    h = h[h > 0]
    if len(h) < 3:
        return None, {"reason": "fewer than 3 photos above the floor plane"}
    med = float(np.median(h))
    lo, hi = PLAUSIBLE_NEAREST_MM
    if not lo <= prior_mm / med * spread <= hi:
        return None, {"reason": f"the floor puts the photos {prior_mm / med * spread / 1000:.1f} m from the wall"}
    return prior_mm / med, {"method": "camera height", "cameraHeightMm": prior_mm, "photos": int(len(h)),
                            "photoHeightP10ToMedian": round(float(np.percentile(h, 10) / med), 3)}


def nearest_camera(spread_units):
    return NEAREST_CAMERA_MM / spread_units, {"method": "nearest camera distance",
                                             "nearestCameraMm": NEAREST_CAMERA_MM}


def ray_hit(centre, d, planes, pts, support):
    """(point, plane index) where the ray first meets a plane within `support` of that plane's points."""
    best = (np.inf, None, None)
    for k, pl in enumerate(planes):
        den = d @ pl["n"]
        if abs(den) < 1e-9:
            continue
        t = ((pl["c"] - centre) @ pl["n"]) / den
        if t <= 0 or t >= best[0]:
            continue
        x = centre + t * d
        if "_tree" not in pl:
            pl["_tree"] = cKDTree(plane_ab(pts[pl["inl"]], pl["c"], pl["n"]))
        if pl["_tree"].query(plane_ab(x[None], pl["c"], pl["n"])[0], distance_upper_bound=support)[0] < support:
            best = (t, x, k)
    return best[1], best[2]


def measured(model, m, planes, pts, support):
    """(mm per unit or None, info) from two taps in one photo whose rays meet the planes."""
    im = next((i for i in model["images"] if i["stem"] == m["photo"] and i["role"] != "frame"), None)
    if im is None:
        return None, {"reason": f"photo {m['photo']} is not in the model"}
    centre, dirs = world_rays(im, model["cams"][im["cam"]], [m["a"], m["b"]])
    (xa, ka), (xb, kb) = (ray_hit(centre, d, planes, pts, support) for d in dirs)
    if xa is None or xb is None:
        return None, {"reason": "a tapped point is on no surface of the model"}
    dist = float(np.linalg.norm(xa - xb))
    if dist <= 0:
        return None, {"reason": "the tapped points coincide"}
    return m["mm"] / dist, {"method": "measured distance", "photo": m["photo"], "mm": m["mm"],
                            "planes": [int(ka), int(kb)]}


def floor_from_points(pts, normals, up, cam_centres):
    """Phase 0 c_scale.py: points with a near-vertical normal below every camera; the floor is the biggest
    peak of their height histogram. A plane-like {"c", "n"} (n = up) or None."""
    h = pts @ up
    below = (np.abs(normals @ up) > np.cos(np.radians(15))) & (h < (cam_centres @ up).min())
    if below.sum() < 50:
        return None
    span = np.percentile(h, 99) - np.percentile(h, 1)
    edges = np.arange(h[below].min(), h[below].max() + span / 400, span / 400)
    if len(edges) < 3:
        return None
    hist, edges = np.histogram(h[below], edges)
    k = int(np.argmax(hist))
    sel = below & (h > edges[max(k - 1, 0)]) & (h < edges[min(k + 2, len(edges) - 1)])
    if sel.sum() < MIN_FLOOR_POINTS or sel.sum() < 0.25 * below.sum():  # a real floor is one substantial layer
        return None
    return {"c": float(np.median(h[sel])) * up, "n": up, "points": int(sel.sum())}
