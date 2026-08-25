#!/usr/bin/env python
"""Transfer stored hold coordinates from an old wall photo onto a new orthophoto.

One hold at a time, never one global overlay warp: a coarse map seeds a bounded
local search per hold, a robust thin-plate spline covers the holds the local search
could not resolve, and every transferred hold is finally snapped onto a blob the
YOLO detector found in the NEW image.

Reproduce:
    /tmp/wallenv/bin/python remap_holds.py            # see README.md
"""
import argparse
import json
import os
import pickle
import sys
import time

import cv2
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import hm_detect
import hm_planes
import hm_render
from hm_common import (NEW_IMG, OLD_IMG, cache_path, hold_px, load_holds, load_images,
                       seed_everything)
from hm_field import fit_field
from hm_global import build_global_map, side_panel_mask
from hm_planes import KICK, LEFT, MAIN
from hm_local import refine_all

NCC_GOOD = 0.60          # per-hold correlation above this = trusted control point
NCC_OK = 0.38            # with field + detector agreement this is enough to confirm
MOVE_PX = 60.0           # displacement from the smooth field that counts as "moved"
FIELD_TOL = 18.0         # agreement between the local match and the smooth field, px
REGIONS = (("top", 0.0, 1 / 3), ("middle", 1 / 3, 2 / 3), ("bottom", 2 / 3, 1.0))


def _cached(work, name, fn, force=False):
    path = cache_path(work, name)
    if os.path.exists(path) and not force:
        with open(path, "rb") as fh:
            return pickle.load(fh)
    val = fn()
    with open(path, "wb") as fh:
        pickle.dump(val, fh)
    return val


def region_of(y_norm):
    for name, lo, hi in REGIONS:
        if lo <= y_norm < hi or (hi == 1.0 and y_norm >= hi):
            return name
    return "bottom"


def classify(ref, dist_from_field, snap_dist, snap_rel, in_panel):
    """matched / moved / uncertain / missing for one hold.

    Two independent signals confirm a hold: the local correlation peak, and whether
    that peak agrees with the smooth field the other holds define. A third, the
    detector snap, rescues holds whose correlation is mediocre because the old photo
    resolved them poorly.
    """
    if not ref["in_frame"]:
        return "missing", ("left return panel - outside the orthophoto"
                           if in_panel else "below the orthophoto crop")
    ncc = ref["ncc"]
    agrees = dist_from_field <= FIELD_TOL and not ref["at_limit"]
    snapped = np.isfinite(snap_dist)
    tight_snap = snapped and snap_rel <= 0.6
    if ncc >= NCC_GOOD and agrees:
        return "matched", ""
    if ncc >= NCC_OK and agrees and tight_snap:
        return "matched", "moderate correlation, confirmed by field and detector"
    if ncc >= NCC_GOOD and dist_from_field > MOVE_PX:
        return "moved", f"{dist_from_field:.0f} px off the smooth field prediction"
    if not snapped and ncc < NCC_OK:
        return "missing", "no correlation and no detected hold nearby"
    if tight_snap:
        return "uncertain", "placed by the deformation field, snapped to a detection"
    return "uncertain", "weak evidence"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--work", default="/Users/patrickweindl/Desktop/wall-photos/work/holds-match")
    ap.add_argument("--force", action="store_true", help="ignore the cache")
    args = ap.parse_args()
    os.makedirs(args.work, exist_ok=True)
    t0 = time.time()
    seed_everything()

    print("[1/7] loading")
    old, new = load_images()
    doc, live = load_holds(generation=1)
    oh, ow = old.shape[:2]
    nh, nw = new.shape[:2]
    print(f"  old {ow}x{oh}  new(natural master) {nw}x{nh}  live holds {len(live)}")

    # Plane assignment is kept for the per-REGION breakdown only. Every hold is now
    # transferred and emitted in ONE whole-wall coordinate space (normalised against the
    # single natural master), so there is no per-plane split of the coordinates or the run.
    plane_of = hm_planes.assign(live, old.shape)
    for key in (MAIN, KICK, LEFT):
        print(f"  region {key}: {sum(1 for k in plane_of if k == key)} holds")

    print("[2/7] coarse global initialisation (old photo -> natural master, whole wall)")
    packed = _cached(args.work, "seedmap.pkl",
                     lambda: build_global_map(old, new), args.force)
    seed, h_full, corr, gstats = packed
    for k, v in gstats.items():
        print(f"  {k}: {v}")

    print("[3/7] per-hold local refinement")
    refs = _cached(args.work, "refine.pkl",
                   lambda: refine_all(old, new, seed, live), args.force)

    print("[4/7] robust deformation field")
    po = np.array([[h["X"] * ow, h["Y"] * oh] for h in live])
    seed_pts = np.array([r["seed"] for r in refs])
    ref_pts = np.array([r["new"] for r in refs])
    ncc = np.array([r["ncc"] for r in refs])
    good = np.array([r["ncc"] >= NCC_GOOD and not r["at_limit"] and r["in_frame"]
                     for r in refs])
    print(f"  {int(good.sum())}/{len(live)} holds are trusted control points"
          f" (ncc >= {NCC_GOOD})")
    field, kept = fit_field(po[good], (ref_pts - seed_pts)[good])
    field_pts = seed_pts + field(po)
    resid = np.linalg.norm(ref_pts - field_pts, axis=1)

    final = np.where(good[:, None] & (resid[:, None] < 250), ref_pts, field_pts)

    print("[5/7] detecting holds in the natural master")
    # The natural master is far smaller than the ortho (holds ~20 px radius, not ~50), so
    # the ortho-scale detector defaults (tile 1280, min_side 45) would reject most real
    # holds. Tune the tile down for more zoom and lower the size floor to the natural
    # master's scale; filter at full resolution since the image is small enough.
    det = _cached(args.work, "detections.pkl",
                  lambda: hm_detect.detect(new, log=print, tile=640, stride=480,
                                           min_side=14.0, filter_scale=1.0), args.force)
    boxes, scores, classes = det
    print(f"  {len(boxes)} detections after NMS")

    r_old = np.array([float(h.get("Radius") or 0.0) * max(ow, oh) for h in live])
    scale = np.array([r["scale"] for r in refs])
    r_new_pred = r_old * scale
    snapped, r_snap, snap_dist, snap_idx = hm_detect.snap(final, r_new_pred, boxes)

    print("[6/7] classifying (single whole-wall surface)")
    panel = side_panel_mask(old.shape)
    records = []
    # region breakdown: main-span / kickboard / left-return from plane_of, and a
    # left/centre/right split of the main span so the right corner is visible on its own.
    region_stats = {k: {} for k in (MAIN, KICK, LEFT)}
    corner_stats = {"main-left": {}, "main-centre": {}, "main-right": {}}
    for i, h in enumerate(live):
        in_panel = bool(panel[int(min(oh - 1, h["Y"] * oh)), int(min(ow - 1, h["X"] * ow))])
        use_snap = np.isfinite(snap_dist[i])
        pos = snapped[i] if use_snap else final[i]
        rad_px = r_snap[i] if use_snap else r_new_pred[i]
        snap_rel = (snap_dist[i] / max(r_new_pred[i], 1.0)
                    if np.isfinite(snap_dist[i]) else np.inf)
        cls, why = classify(refs[i], resid[i], snap_dist[i], snap_rel, in_panel)
        if cls == "missing":
            pos = final[i]
            rad_px = r_new_pred[i]
        in_frame_pos = bool(0 <= pos[0] < nw and 0 <= pos[1] < nh)
        conf = float(np.clip(ncc[i], 0, 1))
        rreg = plane_of[i]
        rec = {
            "Id": h["Id"],
            "Category": h["Category"],
            # SINGLE whole-wall surface: `plane` is informational only ("wall"); the region
            # tag records which wall surface the stored (X, Y) came from, for diagnostics.
            "plane": "wall",
            "region": rreg,
            "Color": h.get("Color"),
            "BoulderLinkCount": h.get("BoulderLinkCount", 0),
            "old": {"X": h["X"], "Y": h["Y"], "Radius": h.get("Radius")},
            "new": None,
            "classification": cls,
            "reason": why,
            "confidence": round(conf, 4),
            "ncc": round(float(ncc[i]), 4),
            "snap_distance_px": (None if not np.isfinite(snap_dist[i])
                                 else round(float(snap_dist[i]), 1)),
            "field_residual_px": round(float(resid[i]), 1),
            "new_in_frame": in_frame_pos,
        }
        # Whole-wall normalisation: X = px/W, Y = px/H against the single natural master,
        # Radius against its longer side. This is exactly the app's DB convention
        # (SegmentId NULL, no per-plane split).
        jac = seed.jacobian((h["X"] * ow, h["Y"] * oh))
        nx, ny = float(pos[0] / nw), float(pos[1] / nh)
        rec["new"] = {
            "X": round(nx, 6), "Y": round(ny, 6),
            "Radius": round(float(rad_px) / max(nw, nh), 6),
        }
        if h.get("ShapePoints"):
            pts = []
            for p in h["ShapePoints"]:
                v = np.array([p["Dx"] * ow, p["Dy"] * oh])
                w = jac @ v
                pts.append({"Dx": round(float(w[0] / nw), 6),
                            "Dy": round(float(w[1] / nh), 6)})
            rec["new"]["ShapePoints"] = pts
        records.append(rec)
        rs = region_stats.setdefault(rreg, {})
        rs[cls] = rs.get(cls, 0) + 1
        if rreg == MAIN:
            bucket = ("main-left" if h["X"] < 0.2 else
                      "main-right" if h["X"] > 0.8 else "main-centre")
            cs = corner_stats[bucket]
            cs[cls] = cs.get(cls, 0) + 1

    counts_all = {}
    counts_all_linked = {}
    for r in records:
        cls = r["classification"]
        counts_all[cls] = counts_all.get(cls, 0) + 1
        if r["BoulderLinkCount"] > 0:
            counts_all_linked[cls] = counts_all_linked.get(cls, 0) + 1

    # Holds whose transferred position lands off the natural master. On the whole-wall
    # natural master (nothing cropped) this should be a small handful; on the old tight
    # ortho crop it was ~55 on the main span alone.
    off_frame = [live[i]["Id"] for i, r in enumerate(refs) if not r["in_frame"]]
    by_id = {r["Id"]: r for r in records}
    prev_lost = [by_id[i] for i in off_frame if by_id[i]["BoulderLinkCount"] > 0]
    recovered = [r for r in prev_lost if r["classification"] != "missing"]
    print(f"  off-frame holds: {len(off_frame)}, boulder-linked among them: "
          f"{len(prev_lost)}, now placed: {len(recovered)}")

    # New detections that no old hold claimed, inside the region the old photo covered.
    # These are CANDIDATE new holds. They are reported and drawn, but emitted in a
    # SEPARATE `new_candidates` list, never in `holds`: they carry no stored Id and the
    # detector produces false positives on the natural master's non-wall content (mats,
    # ceiling timbers, attic clutter), so injecting them as real hold records would risk
    # polluting the DB clone. Counted-only keeps the transfer honest.
    claimed = set(int(j) for j in snap_idx if j >= 0)
    cover = hm_render.old_coverage_mask(old, new, seed)
    cen = hm_detect.blob_centres(boxes)
    new_blobs = []
    new_candidates = []
    for j in range(len(boxes)):
        if j in claimed:
            continue
        cx, cy = cen[j]
        if 0 <= int(cy) < cover.shape[0] and 0 <= int(cx) < cover.shape[1] and cover[int(cy), int(cx)]:
            new_blobs.append(j)
            bw, bh = float(boxes[j][2]), float(boxes[j][3])
            new_candidates.append({
                "new": {"X": round(float(cx) / nw, 6), "Y": round(float(cy) / nh, 6),
                        "Radius": round(max(bw, bh) / 2.0 / max(nw, nh), 6)},
                "classification": "new", "confidence": round(float(scores[j]), 4),
            })
    print(f"  new candidate blobs (detected, no old hold): {len(new_blobs)}")

    out = {
        "_source_old": OLD_IMG, "_source_new": NEW_IMG,
        "_new_image": {"width": nw, "height": nh},
        "_convention": doc["_coordinateConvention"],
        "_surface": ("SINGLE whole-wall surface. Every hold's `new.X`/`new.Y` is normalised"
                     " 0..1 per axis against the ONE natural master (top-left origin), and"
                     " `new.Radius` against its longer side. This matches the app DB, which"
                     " stores holds whole-wall normalised with SegmentId NULL. `region` is"
                     " informational only (which wall surface the stored hold came from)."),
        "_thresholds": {"ncc_good": NCC_GOOD, "ncc_ok": NCC_OK,
                        "field_tol_px": FIELD_TOL, "moved_px": MOVE_PX},
        "holds": records,
        "new_candidates": new_candidates,
    }
    with open(os.path.join(args.work, "holds-remapped.json"), "w") as fh:
        json.dump(out, fh, indent=1)

    print("[7/7] overlays")
    hm_render.render_all(args.work, old, new, live, records, boxes, new_blobs, seed)

    report = {
        "generated": time.strftime("%Y-%m-%dT%H:%M:%S"),
        "runtime_seconds": round(time.time() - t0, 1),
        "surface": "single whole-wall natural master",
        "old_image": {"path": OLD_IMG, "width": ow, "height": oh},
        "new_image": {"path": NEW_IMG, "width": nw, "height": nh},
        "live_holds": len(live),
        "counts": counts_all,
        "counts_boulder_linked": counts_all_linked,
        "counts_by_region": region_stats,
        "counts_by_main_span_corner": corner_stats,
        "region_holds": {k: sum(1 for x in plane_of if x == k) for k in (MAIN, KICK, LEFT)},
        "boulder_linked_total": sum(1 for h in live if h.get("BoulderLinkCount", 0) > 0),
        "off_frame_holds": len(off_frame),
        "boulder_linked_off_frame": len(prev_lost),
        "boulder_linked_off_frame_recovered": len(recovered),
        "global_stage": gstats,
        "detections_in_new": int(len(boxes)),
        "new_candidates_unclaimed_inside_old_coverage": len(new_blobs),
        "snap": {"snapped": int(np.isfinite(snap_dist).sum()),
                 "median_snap_px": (float(np.median(snap_dist[np.isfinite(snap_dist)]))
                                    if np.isfinite(snap_dist).any() else None)},
        "field": {"control_points": int(good.sum()),
                  "kept_after_rejection": int(kept.sum()),
                  "median_control_residual_px": float(np.median(resid[good]))},
        "thresholds": {"ncc_good": NCC_GOOD, "ncc_ok": NCC_OK,
                       "field_tol_px": FIELD_TOL, "moved_px": MOVE_PX},
        "missing_reasons": {r: sum(1 for x in records
                                   if x["classification"] == "missing" and x["reason"] == r)
                            for r in sorted({x["reason"] for x in records
                                             if x["classification"] == "missing"})},
    }
    with open(os.path.join(args.work, "report.json"), "w") as fh:
        json.dump(report, fh, indent=1)
    print(json.dumps({"overall": counts_all, "by_region": region_stats}, indent=1))
    print("done in %.1fs" % (time.time() - t0))


if __name__ == "__main__":
    main()
