"""solve-sfm: a feature reconstruction (splat-prepare's sparse.zip) -> the wall-geometry document v1.

points (track >= 3, error < 2 px, PCA normals) -> planes in model units (scale-free tolerances: 1.2 % of the
median point-to-nearest-camera distance) -> anchors (similarity + plane-ICP) -> scale -> metric frame (mm) ->
merged planes, gravity, wall-facet decisions -> world frame, facets -> document (export.py).
"""
import numpy as np
from scipy.spatial import cKDTree

from ..frame import pseudo_up, world_transform
from . import anchors as anchoring
from .gravity import camera_up, declared_up, device_up, floor_plane, is_horizontal, unit
from .planes import fit_planes, merge_parallel, point_normals, refine
from .scale import camera_height, floor_from_points, measured, nearest_camera
from .score import credit_hosts, describe, hold_hits, hosts, judge, reject_features_on
from .world import build_facets, order_facets, shift_origin

# x the median point-to-nearest-PHOTO distance D (The Attic, 53 photos + 300 frames: D = 0.91 m -> 16 mm tolerance,
# 350 mm sampling radius, 300 mm connectivity cells: Phase 0's validated values)
TOL, RADIUS, CELL, SUPPORT = 0.0175, 0.385, 0.33, 0.09
MIN_POINTS, MIN_PHOTOS = 500, 3
MERGE_MM, TOUCH_MM = 30.0, 300.0


class SfmError(ValueError):
    """The model cannot give a wall geometry; the message is safe to return to the client."""


def select_points(model):
    ok = (model["tl"] >= 3) & (model["err"] < 2.0)
    Q = model["xyz"][ok]
    if len(Q) < MIN_POINTS:
        raise SfmError(f"only {len(Q)} well-measured 3D points (need {MIN_POINTS}): take more overlapping photos")
    C = np.array([im["C"] for im in model["images"] if im["role"] == "photo"])  # not the (closer) video frames
    sample = Q[:: max(1, len(Q) // 5000)]
    spread = float(np.median(cKDTree(C).query(sample)[0]))
    return {"Q": Q, "N": point_normals(Q), "spread": spread, "total": len(model["xyz"])}


def photos_of(model):
    photos = [im for im in model["images"] if im["role"] == "photo"]
    if len(photos) < MIN_PHOTOS:
        raise SfmError(f"only {len(photos)} photos registered (need {MIN_PHOTOS})")
    return photos


def anchor_frame(model, req, pts):
    """The anchors' world transform {"ok", "s", "A", "b", report} (metric frame Y = s X -> world); a refused fit
    within anchors.SCALE_RMS_MM comes back as {"ok": False, "fit": {"s", "R"}, report} for scale and gravity only."""
    fit = anchoring.fit_anchors(model, req.anchors, req.reference)
    report = {k: v for k, v in fit.items() if k not in ("s", "R", "t")}
    if not fit["ok"]:
        # Refused for the FRAME (activation), yet a similarity over >= 6 consistent anchors within 60 mm rms, spread
        # over metres of camera positions, pins the scale to ~1-2 % and "up" to a fraction of a degree: far better
        # than the camera-height estimate (+-10 %, +9.3 % on the first real re-capture) or a phone's accelerometer.
        # So the model is still measured with it, recorded as "anchor-fit"; it is just not placed in the old frame.
        if fit.get("scaleOk"):
            return {"ok": False, "fit": {"s": fit["s"], "R": fit["R"]},
                    "report": {**report, "usedFor": "scale and gravity"}}
        return {"ok": False, "report": report}
    s, R, t = fit["s"], fit["R"], fit["t"]
    P, N = pts["Q"] @ (s * R).T + t, pts["N"] @ R.T
    Ri, ti, icp = anchoring.plane_icp(P, N, anchoring.reference_facets(req.reference))
    report["icp"] = icp
    if Ri is None:
        return {"ok": False, "report": {**report, "ok": False, "reason": icp["reason"]}}
    return {"ok": True, "s": s, "A": Ri @ R, "b": Ri @ t + ti, "report": report}


def choose_scale(model, req, raw, pts, floor, anchor, up0):
    """(mm per unit, source, known, info) by the chain anchors -> measured -> anchor fit (frame refused) -> estimate."""
    tried = {}
    if anchor and anchor["ok"]:
        return anchor["s"], "anchors", True, {"method": "anchors"}
    if req.measured:
        s, info = measured(model, req.measured, raw, pts["Q"], SUPPORT * pts["spread"])
        if s:
            return s, "measured", True, info
        tried["measured"] = info["reason"]
    if anchor and anchor.get("fit"):
        rep = anchor["report"]
        info = {"method": "anchor fit (frame refused)", "rmsMm": rep["rmsMm"], "maxMm": rep["maxMm"], "notUsed": tried}
        return anchor["fit"]["s"], "anchor-fit", True, info
    photos = np.array([im["C"] for im in model["images"] if im["role"] == "photo"])
    ground, how = (floor, "plane") if floor else (floor_from_points(pts["Q"], pts["N"], up0, photos), "height histogram")
    s, info = (None, {"reason": "no floor"})
    if ground:
        s, info = camera_height(model, ground, req.options["cameraHeightMm"], pts["spread"])
        info = {**info, "floor": how}
    if not s:
        tried["cameraHeight"] = info["reason"]
        s, info = nearest_camera(pts["spread"])
    return s, "estimate", False, {**info, "notUsed": tried}


def choose_gravity(model, req, planes, floor, dev, cam_up, anchor):
    """(up in the metric frame, source, known, info) by the chain anchors -> anchor fit -> device -> declared -> floor
    -> cameras."""
    if anchor and anchor["ok"]:
        up_w = unit(req.reference["world"].get("up") or [0, 0, 1])
        return anchor["A"].T @ up_w, "anchors", bool(req.reference["world"].get("gravityKnown", True)), {}
    if anchor and anchor.get("fit") and req.reference["world"].get("gravityKnown", True):
        up_w = unit(req.reference["world"].get("up") or [0, 0, 1])
        return anchor["fit"]["R"].T @ up_w, "anchor-fit", True, {"rmsMm": anchor["report"]["rmsMm"]}
    up, info = dev
    if up is not None:
        return up, "device", True, {"device": info}
    tried = {"device": info.get("reason")}
    prior = floor["n"] if floor else cam_up
    if any(h.declared_angle_deg is not None for h in req.segments):
        cands = sorted((p for p in planes if p["areaM2"] >= req.options["minFacetAreaM2"]
                        and not is_horizontal(p["n"], prior)), key=lambda p: -p["npts"])[:8]
        up, dinfo = declared_up(cands, req.segments, prior)
        if up is not None:
            return up, "declared", True, {"declared": dinfo, "notUsed": tried}
        tried["declared"] = dinfo.get("reason")
    if floor:
        return floor["n"], "floor", True, {"notUsed": tried}
    return cam_up, "cameras", False, {"notUsed": {**tried, "floor": "no floor plane"}}


def metric_planes(raw, s, pts, photos, model, req):
    """The raw planes scaled to mm, merged, described and scored against the hold detections."""
    Y = pts["Q"] * s
    planes = merge_parallel([{**p, "c": p["c"] * s} for p in raw], Y, max_off=MERGE_MM, gap=TOUCH_MM)
    mphotos = [{"C": im["C"] * s, "R": im["R"]} for im in photos]
    describe(planes, Y, mphotos)
    hits, rays = hold_hits(planes, model, req.holds, lambda x, direction=False: x if direction else x * s)
    return Y, planes, hits, rays


def world_of(planes, Y, up, known, cam_up, anchor, req):
    """(A, b, up_world, facets) of the accepted planes: world = A @ Y + b."""
    if anchor and anchor["ok"]:
        up_w = unit(req.reference["world"].get("up") or [0, 0, 1])
        return anchor["A"], anchor["b"], up_w, build_facets(planes, Y, anchor["A"], anchor["b"], up_w)
    ref = order_facets(planes)[0]
    up = up if known else pseudo_up(ref["n"], cam_up)
    A, up_w = world_transform(ref["n"], up), np.array([0.0, 0, 1])
    facets = build_facets(planes, Y, A, np.zeros(3), up_w)
    shift = facets[0]["origin"].copy()
    return A, -shift, up_w, shift_origin(facets, shift)


def solve_sfm(req, model, progress=None):
    """-> the parts export.build_sfm_document needs."""
    progress = progress or (lambda *_: None)
    progress(0.05, "points")
    photos = photos_of(model)
    pts = select_points(model)
    rng = np.random.default_rng(int(req.options["seed"]))
    progress(0.1, "planes")
    D = pts["spread"]
    raw = fit_planes(pts["Q"], pts["N"], TOL * D, RADIUS * D, CELL * D, rng,
                     min_pts=int(np.clip(0.004 * len(pts["Q"]), 40, 150)))
    progress(0.6, "gravity and scale")
    dev, cam_up = device_up(model, req.gravity), camera_up(model)
    up0 = dev[0] if dev[0] is not None else cam_up
    floor = floor_plane(raw, pts["Q"], up0, np.array([im["C"] for im in photos]), D)
    anchor = anchor_frame(model, req, pts) if req.anchors else None
    s, s_source, s_known, s_info = choose_scale(model, req, raw, pts, floor, anchor, up0)
    Y, planes, hits, rays = metric_planes(raw, s, pts, photos, model, req)
    up, g_source, g_known, g_info = choose_gravity(model, req, planes, floor, dev, cam_up, anchor)
    progress(0.8, "wall facets")
    host = hosts(planes, Y)
    judge(planes, up, g_known, credit_hosts(hits, host), rays, req.options["minFacetAreaM2"])
    reject_features_on(planes, host)
    refine([p for p in planes if p["accepted"]], Y, pts["N"], TOL * D * s)
    if not any(p["accepted"] for p in planes):
        raise SfmError("no wall surface found in the reconstruction (no plane passed the wall-facet rules)")
    A, b, up_w, facets = world_of(planes, Y, up, g_known, cam_up, anchor, req)
    return {"req": req, "model": model, "pts": pts, "s": s, "A": A, "b": b, "up": up_w, "planes": planes,
            "facets": facets, "rays": rays, "gravity": {"source": g_source, "known": g_known, **g_info},
            "scale": {"source": s_source, "known": s_known, "mmPerUnit": s, **s_info},
            "anchors": anchor["report"] if anchor else None, "Y": Y}
