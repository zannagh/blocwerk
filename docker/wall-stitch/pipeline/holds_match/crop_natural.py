#!/usr/bin/env python
"""Hold-aware crop of the cylindrical natural master to a natural rectangle.

Runs AFTER remap_holds.py has transferred the live holds onto the (uncropped)
cylindrical natural master.  Using the recognised hold positions it crops the master
to the wall:

  * top     -> just above the topmost holds (where the plywood meets the ceiling),
  * bottom  -> just below the perspectivally lowest hold (the left-upright's lowest),
               with a mat margin so the near-border hold is not clipped and some mat
               stays in frame,
  * sides   -> the full curved wall including both end pieces; because the right attic
               clutter (drill / table / beams) carries no transferred holds, cutting a
               margin past the outermost holds trims it in a hold-free column.

The crop is applied to the angled-slot PNG/JPG in place, the recognised hold
coordinates in holds-remapped.json are re-normalised to the crop, and
report['natural'] dims are updated.  Idempotent: re-running detects an already-cropped
master (its size matches report['natural'].crop) and no-ops.
"""
import argparse
import json
import os

import cv2
import numpy as np

from hm_common import NEW_IMG, WALL_ROOT

# margins in px around the hold hull; bottom is larger so mat stays in frame
MARGIN_TOP = 45
MARGIN_SIDE = 45
MARGIN_BOTTOM = 90
FINAL = os.path.join(WALL_ROOT, "06-final")
ANGLED = os.path.join(FINAL, "wall-orthophoto-angled")
REMAP = os.path.join(WALL_ROOT, "holds-match", "holds-remapped.json")
REPORT = os.path.join(FINAL, "report.json")


def _hull_px(records, nw, nh):
    """Bounding box (in master px) of the placed real holds, radius-inflated."""
    xs0, xs1, ys0, ys1 = [], [], [], []
    for r in records:
        new = r.get("new")
        if not new or not r.get("new_in_frame", False):
            continue
        if r["classification"] == "missing":
            continue
        x = new["X"] * nw
        y = new["Y"] * nh
        rad = float(new.get("Radius") or 0.0) * max(nw, nh)
        xs0.append(x - rad); xs1.append(x + rad)
        ys0.append(y - rad); ys1.append(y + rad)
    if not xs0:
        raise SystemExit("no placed holds to crop against")
    return min(xs0), max(xs1), min(ys0), max(ys1)


def _renorm(new, x0, y0, cw, ch, nw, nh):
    out = {
        "X": round((new["X"] * nw - x0) / cw, 6),
        "Y": round((new["Y"] * nh - y0) / ch, 6),
        "Radius": round(float(new.get("Radius") or 0.0) * max(nw, nh) / max(cw, ch), 6),
    }
    if new.get("ShapePoints"):
        out["ShapePoints"] = [{"Dx": round(p["Dx"] * nw / cw, 6),
                               "Dy": round(p["Dy"] * nh / ch, 6)}
                              for p in new["ShapePoints"]]
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--jpeg-quality", type=int, default=95)
    ap.add_argument("--png-compression", type=int, default=3)
    args = ap.parse_args()

    master = cv2.imread(NEW_IMG, cv2.IMREAD_COLOR)
    if master is None:
        raise SystemExit(f"could not read master {NEW_IMG}")
    nh, nw = master.shape[:2]
    doc = json.load(open(REMAP))
    records = doc["holds"]

    minx, maxx, miny, maxy = _hull_px(records, nw, nh)
    x0 = int(max(0, np.floor(minx - MARGIN_SIDE)))
    x1 = int(min(nw, np.ceil(maxx + MARGIN_SIDE)))
    y0 = int(max(0, np.floor(miny - MARGIN_TOP)))
    y1 = int(min(nh, np.ceil(maxy + MARGIN_BOTTOM)))
    cw, ch = x1 - x0, y1 - y0
    print(f"master {nw}x{nh}  hold hull x[{minx:.0f},{maxx:.0f}] y[{miny:.0f},{maxy:.0f}]")
    print(f"crop -> x0={x0} y0={y0} x1={x1} y1={y1}  ({cw}x{ch})")

    crop = master[y0:y1, x0:x1]
    cv2.imwrite(ANGLED + ".png", crop, [cv2.IMWRITE_PNG_COMPRESSION, args.png_compression])
    cv2.imwrite(ANGLED + ".jpg", crop, [cv2.IMWRITE_JPEG_QUALITY, args.jpeg_quality])

    # re-normalise every placed hold + candidate to the crop
    for r in records:
        if r.get("new"):
            r["new"] = _renorm(r["new"], x0, y0, cw, ch, nw, nh)
    kept = []
    for c in doc.get("new_candidates", []):
        px, py = c["new"]["X"] * nw, c["new"]["Y"] * nh
        if x0 <= px < x1 and y0 <= py < y1:
            c["new"] = _renorm(c["new"], x0, y0, cw, ch, nw, nh)
            kept.append(c)
    doc["new_candidates"] = kept
    doc["_new_image"] = {"width": cw, "height": ch}
    doc["_crop"] = {"x0": x0, "y0": y0, "x1": x1, "y1": y1,
                    "from_master": {"width": nw, "height": nh}}
    json.dump(doc, open(REMAP, "w"), indent=1)

    report = json.load(open(REPORT))
    nat = report.get("natural", {})
    nat["master_width"] = cw
    nat["master_height"] = ch
    nat["crop"] = {"x0": x0, "y0": y0, "x1": x1, "y1": y1,
                   "uncropped_width": nw, "uncropped_height": nh,
                   "margins_px": {"top": MARGIN_TOP, "side": MARGIN_SIDE,
                                  "bottom": MARGIN_BOTTOM},
                   "rule": ("top just above topmost holds, bottom below the lowest "
                            "(left-upright) hold keeping some mat, sides past the "
                            "outermost holds so the hold-free right attic is trimmed")}
    report["natural"] = nat
    json.dump(report, open(REPORT, "w"), indent=1)
    print(f"cropped master -> {ANGLED}.png ({cw}x{ch}); holds re-normalised; report updated")


if __name__ == "__main__":
    main()
