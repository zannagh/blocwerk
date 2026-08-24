"""Running the per-hold transfer against a plane other than the main span.

Identical method to the main span - seed map, bounded per-hold NCC search,
smooth residual field, snap to a detected blob, classify. What changes is only
what the geometry forces: these planes carry 30 and 22 holds rather than 411,
so a thin-plate spline over them would interpolate noise (and on a 113 px tall
strip it is degenerate in y). A degree-1 residual is fitted instead, which is
all the seed map's ~1-2 px residual leaves to correct.
"""
import numpy as np

import hm_detect
from hm_common import PolyMap
from hm_local import refine_all
from hm_planes import KICK, LEFT

NCC_GOOD = 0.60
NCC_OK = 0.38
FIELD_TOL = 18.0
MOVE_PX = 60.0

OUT_OF_PLANE = {
    KICK: "outside the kickboard strip - the stored position extrapolates off the plane",
    LEFT: "outside the left return panel image",
}


def _classify(ref, dist_from_field, snap_dist, snap_rel, plane_key, field_tol):
    if not ref["in_frame"]:
        return "missing", OUT_OF_PLANE.get(plane_key, "outside this plane's image")
    ncc = ref["ncc"]
    agrees = dist_from_field <= field_tol and not ref["at_limit"]
    snapped = np.isfinite(snap_dist)
    tight = snapped and snap_rel <= 0.6
    if ncc >= NCC_GOOD and agrees:
        return "matched", ""
    if ncc >= NCC_OK and agrees and tight:
        return "matched", "moderate correlation, confirmed by field and detector"
    if ncc >= NCC_GOOD and dist_from_field > MOVE_PX:
        return "moved", f"{dist_from_field:.0f} px off the smooth field prediction"
    if not snapped and ncc < NCC_OK:
        return "missing", "no correlation and no detected hold nearby"
    if tight:
        return "uncertain", "placed by the residual field, snapped to a detection"
    return "uncertain", "weak evidence"


def _fit_residual(po, delta, good, log=print):
    """Degree-1 robust residual over the confident holds; identity if too few."""
    n = int(good.sum())
    if n < 6:
        log(f"  residual field: only {n} confident holds - using the seed map as is")
        return (lambda pts: np.zeros((len(np.atleast_2d(pts)), 2))), 0
    poly = PolyMap.fit(po[good], delta[good], degree=1, huber=4.0)
    r = np.linalg.norm(poly(po[good]) - delta[good], axis=1)
    log(f"  residual field: {n} control points, median {np.median(r):.1f} px")
    return poly, n


def run(plane, old, new, holds, seed, boxes, log=print):
    """Transfer `holds` onto `plane`. Returns (records, diagnostics)."""
    oh, ow = old.shape[:2]
    nh, nw = new.shape[:2]
    field_tol = FIELD_TOL * (plane.search_px / 60.0) * 2.0

    refs = refine_all(old, new, seed, holds, log=None, search=plane.search_px)
    po = np.array([[h["X"] * ow, h["Y"] * oh] for h in holds], float)
    seed_pts = np.array([r["seed"] for r in refs])
    ref_pts = np.array([r["new"] for r in refs])
    ncc = np.array([r["ncc"] for r in refs])
    good = np.array([r["ncc"] >= NCC_GOOD and not r["at_limit"] and r["in_frame"]
                     for r in refs])
    log(f"  {int(good.sum())}/{len(holds)} holds are trusted control points")
    poly, nctrl = _fit_residual(po, ref_pts - seed_pts, good, log=log)
    field_pts = seed_pts + poly(po)
    resid = np.linalg.norm(ref_pts - field_pts, axis=1)
    final = np.where(good[:, None] & (resid[:, None] < 250), ref_pts, field_pts)

    r_old = np.array([float(h.get("Radius") or 0.0) * max(ow, oh) for h in holds])
    scale = np.array([r["scale"] for r in refs])
    r_new_pred = r_old * scale
    snapped, r_snap, snap_dist, _ = hm_detect.snap(
        final, r_new_pred, boxes, max_abs=plane.snap_abs, min_tol=plane.snap_min_tol)

    records = []
    for i, h in enumerate(holds):
        use = np.isfinite(snap_dist[i])
        pos = snapped[i] if use else final[i]
        rad = r_snap[i] if use else r_new_pred[i]
        rel = snap_dist[i] / max(r_new_pred[i], 1.0) if use else np.inf
        cls, why = _classify(refs[i], resid[i], snap_dist[i], rel, plane.key, field_tol)
        if cls == "missing":
            pos, rad = final[i], r_new_pred[i]
        in_frame = bool(0 <= pos[0] < nw and 0 <= pos[1] < nh)
        rec = {
            "Id": h["Id"], "Category": h["Category"], "Color": h.get("Color"),
            "BoulderLinkCount": h.get("BoulderLinkCount", 0),
            "plane": plane.key,
            "old": {"X": h["X"], "Y": h["Y"], "Radius": h.get("Radius")},
            "new": None,
            "classification": cls, "reason": why,
            "confidence": round(float(np.clip(ncc[i], 0, 1)), 4),
            "ncc": round(float(ncc[i]), 4),
            "snap_distance_px": (None if not use else round(float(snap_dist[i]), 1)),
            "field_residual_px": round(float(resid[i]), 1),
            "region": "kickboard" if plane.key == KICK else "left-return",
            "new_in_frame": in_frame,
        }
        jac = seed.jacobian((h["X"] * ow, h["Y"] * oh))
        rec["new"] = {"X": round(float(pos[0] / nw), 6),
                      "Y": round(float(pos[1] / nh), 6),
                      "Radius": round(float(rad) / max(nw, nh), 6)}
        if h.get("ShapePoints"):
            pts = []
            for p in h["ShapePoints"]:
                wv = jac @ np.array([p["Dx"] * ow, p["Dy"] * oh])
                pts.append({"Dx": round(float(wv[0] / nw), 6),
                            "Dy": round(float(wv[1] / nh), 6)})
            rec["new"]["ShapePoints"] = pts
        records.append(rec)

    diag = {
        "holds": len(holds),
        "image": {"width": nw, "height": nh, "path": plane.image},
        "detections": int(len(boxes)),
        "control_points": nctrl,
        "field_tol_px": round(field_tol, 1),
        "search_px": plane.search_px,
        "snapped": int(np.isfinite(snap_dist).sum()),
        "median_snap_px": (float(np.median(snap_dist[np.isfinite(snap_dist)]))
                           if np.isfinite(snap_dist).any() else None),
        "median_ncc": float(np.median(ncc)),
        "px_per_old_px": float(np.median(scale)),
        "note": plane.note,
    }
    return records, diag


def run_planes(old, live, plane_of, cache, log=print):
    """Seed map, detection and transfer for every plane except the main span."""
    import os

    import hm_detect as _det
    import hm_planes as _pl

    records, diags, images, boxes_by = {}, {}, {}, {}
    for key in (KICK, LEFT):
        plane = _pl.BY_KEY[key]
        subset = [h for h, k in zip(live, plane_of) if k == key]
        log(f"  --- {key}: {len(subset)} holds, {os.path.basename(plane.image)}")
        img = plane.read()
        images[key] = img
        seed = cache(f"seedmap-{key}.pkl",
                     lambda p=plane: _pl.build_plane_map(old, p, log=log))[0]
        boxes = cache(f"detections-{key}.pkl",
                      lambda p=plane, im=img: _det.detect(
                          im, log=None, tile=p.det_tile, stride=p.det_stride,
                          min_side=p.det_min_side, filter_scale=p.det_filter_scale))[0]
        log(f"    {len(boxes)} detections after NMS + appearance filter")
        recs, diag = run(plane, old, img, subset, seed, boxes,
                         log=lambda m: log("  " + m))
        diag["old_to_plane_anisotropy"] = _anisotropy(seed, subset, old.shape)
        records[key], diags[key], boxes_by[key] = recs, diag, boxes
        c = {}
        for r in recs:
            c[r["classification"]] = c.get(r["classification"], 0) + 1
        log(f"    {c}")
    return records, diags, images, boxes_by


def _anisotropy(seed, holds, shape):
    """How anisotropically the OLD photo sampled this plane, per hold.

    The ratio of the seed map's Jacobian singular values. This is the old
    camera's foreshortening of the plane, not the rectified plane's own
    sampling; it matters because a hold's stored size is a single scalar, so
    the transfer has to use the geometric mean (sqrt of the determinant) and
    that is off by roughly this ratio in the extreme directions.
    """
    oh, ow = shape[:2]
    rat = []
    for h in holds:
        sv = np.linalg.svd(seed.jacobian((h["X"] * ow, h["Y"] * oh)), compute_uv=False)
        rat.append(float(sv[0] / max(sv[1], 1e-9)))
    return {"median": float(np.median(rat)), "max": float(np.max(rat))}
