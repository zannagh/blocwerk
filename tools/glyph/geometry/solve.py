"""Thin CLI over the wall-geometry solver, which lives in docker/wall-geometry/wallgeometry (single
source of truth, also what the service container runs).

    python solve.py                         # capture 1 -> out/wall-geometry.json (+ out/request.json)
    python solve.py --level-pairs 14,15     # extra gravity constraint: those markers are level
    python solve.py --validate              # + leave-one-photo-out (slow)
    python solve.py --figures               # + out/fig_*.png (needs matplotlib)
    python solve.py --request FILE          # solve an arbitrary request document instead of capture 1
"""
import argparse
import json
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "..", "..", "docker", "wall-geometry"))

from data import OUT, build_request  # noqa: E402
from wallgeometry import solve_document  # noqa: E402


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--request", help="request JSON to solve (default: build capture 1's)")
    ap.add_argument("--level-pairs", nargs="*", default=[], help="marker pairs at equal height, e.g. 14,15")
    ap.add_argument("--validate", action="store_true")
    ap.add_argument("--figures", action="store_true")
    ap.add_argument("--out", default=os.path.join(OUT, "wall-geometry.json"))
    a = ap.parse_args()
    pairs = [tuple(int(v) for v in p.split(",")) for p in a.level_pairs]
    if a.request:
        with open(a.request) as fh:
            req = json.load(fh)
        if pairs:
            req["levelPairs"] = [list(p) for p in pairs]
        req.setdefault("options", {})["validate"] = a.validate
    else:
        req = build_request(level_pairs=pairs, validate=a.validate)
        os.makedirs(OUT, exist_ok=True)
        with open(os.path.join(OUT, "request.json"), "w") as fh:
            json.dump(req, fh)
    t0 = time.time()
    doc, sol = solve_document(req, progress=lambda f, s: print(f"  {time.time() - t0:5.1f}s {s}"))
    with open(a.out, "w") as fh:
        json.dump(doc, fh, indent=1)
    q = doc["quality"]
    print("wrote", a.out)
    for s in doc["segments"]:
        for f in s["facets"]:
            print(f"  facet {f['id']:>3} ({s['name']}): {f['measuredAngleDeg']} deg, markers {f['markerIds']}")
    print("  gravity:", q["gravity"], "| reproj", q["reprojRmsPx"], "px | checks", json.dumps(q["checks"]))
    if a.figures:
        from figures import fig_3d, fig_plan, fig_side
        for p in (fig_3d(sol), fig_side(sol), fig_plan(sol)):
            print("wrote", p)


if __name__ == "__main__":
    main()
