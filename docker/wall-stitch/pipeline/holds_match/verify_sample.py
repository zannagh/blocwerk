#!/usr/bin/env python
"""Build side-by-side crop sheets for hand-verifying a random sample of transfers.

Left cell = the hold in the OLD photo (yellow), right cell = the same hold's
transferred position in the NEW orthophoto (classification colour). Both cells are
at the same physical scale, so a correct transfer is obvious by eye.
"""
import argparse
import json
import os
import sys

import cv2
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hm_planes
from hm_common import load_images, seed_everything
from hm_render import COLORS

CELL = 300
PER_ROW = 3


def build(work, per_region=15, only=None, plane=hm_planes.MAIN):
    """One old/new crop pair per sampled hold, both cells at the same physical scale.

    `plane` selects which target image the right-hand cell is taken from; the
    three planes are three separate coordinate spaces.
    """
    rng = seed_everything()
    old, new = load_images()
    if plane != hm_planes.MAIN:
        new = hm_planes.BY_KEY[plane].read()
    oh, ow = old.shape[:2]
    nh, nw = new.shape[:2]
    with open(os.path.join(work, "holds-remapped.json")) as fh:
        recs = json.load(fh)["holds"]
    recs = [r for r in recs if r.get("plane", hm_planes.MAIN) == plane]
    pool = [r for r in recs if r["new"] and (only is None or r["classification"] in only)]
    by_region = {}
    for r in pool:
        by_region.setdefault(r["region"], []).append(r)
    picked = []
    for region in sorted(by_region):
        items = sorted(by_region.get(region, []), key=lambda r: r["Id"])
        if not items:
            continue
        idx = rng.choice(len(items), min(per_region, len(items)), replace=False)
        picked += [items[i] for i in sorted(idx)]

    rows, cells = [], []
    for n, r in enumerate(picked):
        ro = float(r["old"]["Radius"] or 0.01) * max(ow, oh)
        half_old = max(3.0 * ro, 45.0)
        a = cv2.resize(cv2.getRectSubPix(old, (int(2 * half_old), int(2 * half_old)),
                                         (r["old"]["X"] * ow, r["old"]["Y"] * oh)),
                       (CELL, CELL))
        cv2.circle(a, (CELL // 2, CELL // 2), int(ro / half_old * CELL / 2), (0, 255, 255), 2)
        rn = float(r["new"]["Radius"]) * max(nw, nh)
        half_new = half_old * (rn / max(ro, 1e-6)) if ro > 0 else half_old * 3
        half_new = max(half_new, 45.0)
        b = cv2.resize(cv2.getRectSubPix(new, (int(2 * half_new), int(2 * half_new)),
                                         (r["new"]["X"] * nw, r["new"]["Y"] * nh)),
                       (CELL, CELL))
        col = COLORS[r["classification"]]
        cv2.circle(b, (CELL // 2, CELL // 2), int(rn / half_new * CELL / 2), col, 2)
        cv2.putText(b, f"{n + 1}", (6, 22), 0, 0.7, (0, 0, 0), 4, cv2.LINE_AA)
        cv2.putText(b, f"{n + 1}", (6, 22), 0, 0.7, col, 2, cv2.LINE_AA)
        cells.append(np.hstack([a, np.full((CELL, 3, 3), 255, np.uint8), b]))
        if len(cells) == PER_ROW:
            rows.append(np.hstack(cells))
            cells = []
    if cells:
        while len(cells) < PER_ROW:
            cells.append(np.full_like(cells[0], 40))
        rows.append(np.hstack(cells))
    sheet = np.vstack(rows)
    stem = "verify-sample" if plane == hm_planes.MAIN else "verify-%s" % plane
    name = stem + (".jpg" if only is None else "-%s.jpg" % "-".join(only))
    cv2.imwrite(os.path.join(work, name), sheet, [cv2.IMWRITE_JPEG_QUALITY, 92])
    return picked, name


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--work", default=os.path.dirname(os.path.abspath(__file__)))
    ap.add_argument("--per-region", type=int, default=15)
    ap.add_argument("--only", nargs="*", default=None)
    ap.add_argument("--plane", default=hm_planes.MAIN,
                    choices=[p.key for p in hm_planes.PLANES])
    a = ap.parse_args()
    p, n = build(a.work, a.per_region, a.only, a.plane)
    print(n, len(p))
    for i, r in enumerate(p):
        print(i + 1, r["Id"][:8], r["region"], r["classification"],
              "ncc %.2f" % r["ncc"], "snap", r["snap_distance_px"])
