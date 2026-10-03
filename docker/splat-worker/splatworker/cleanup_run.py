"""Runs the floater clean-up (cleanup.py) on a job's splats, or standalone on an existing `.spz`.

In the pipeline it runs right after align + refine + crop, before export: the kept splats become
`wall.spz` / `wall.splat`, and the uncleaned (cropped) scene is exported too as `wall.raw.spz`, so the
app can keep it and revert. frame.json gets a `cleanup` block with the per-rule counts.

Standalone (an existing splat, e.g. one installed before the clean-up existed):

    python -m splatworker.cleanup_run --spz wall.spz --frame frame.json --geometry geometry.json \\
        --out wall.clean.spz [--report cleanup.json] [--set carve_min_cameras=4 ...]

It filters the .spz byte for byte (splatio.spz_subset: no re-quantisation) and carves with the
geometry document's photo cameras (the video frames' poses only exist inside a job).
"""
import argparse
import json
import sys

import numpy as np

from .cleanup import KEEP, CleanupParams, classify, longest_axis
from .splatio import read_spz, spz_subset

RAW_FILE = "wall.raw.spz"


def world_matrix(frame):
    """splat -> geometry world (mm): the fine alignment's when it was applied, else the camera fit's."""
    ref = frame.get("refinement") or {}
    return np.array(ref["toWorldMm"] if ref.get("applied") and ref.get("toWorldMm") else frame["toWorldMm"], float)


def geometry_cameras(doc):
    """Camera centres (world mm) of the geometry document's solved photos."""
    out = []
    for c in doc.get("cameras", []):
        R = np.array(c["R"], float).reshape(3, 3)
        out.append(-R.T @ np.array(c["t"], float))
    return np.array(out) if out else None


def run(xyz, log_scale, quat_wxyz, alpha, M, doc, cameras_world, params=None):
    """(keep mask, report) for splats in their own (COLMAP) frame, mapped into the world by M."""
    lin = M[:3, :3]
    world = xyz @ lin.T + M[:3, 3]
    scale = np.exp(log_scale) * np.cbrt(abs(np.linalg.det(lin)))
    axis = longest_axis(quat_wxyz, scale, lin)
    verdict, report = classify(world, scale, alpha, doc, cameras_world, params, axis)
    return verdict == KEEP, report


def clean_job(splats, frame, doc, centres, params=None):
    """Pipeline hook: (keep mask over `splats` (splatio.Splats), frame["cleanup"] block). Never raises:
    a failed clean-up keeps every splat (the scene is only less tidy, never broken)."""
    try:
        if not frame.get("aligned") or not frame.get("toWorldMm"):
            return np.ones(len(splats), bool), {"applied": False, "reason": "not aligned to the wall"}
        M = world_matrix(frame)
        cams = np.array(list(centres), float) if len(centres) else None
        cams = None if cams is None else cams @ M[:3, :3].T + M[:3, 3]
        keep, report = run(splats.xyz, splats.log_scale, splats.rot, splats.alpha, M, doc, cams, params)
        return keep, {"applied": True, "rawFile": RAW_FILE, **report}
    except Exception as e:  # noqa: BLE001 - the clean-up is an extra; the uncleaned scene is still good
        return np.ones(len(splats), bool), {"applied": False, "reason": str(e)[:200]}


def clean_spz(data, frame, doc, params=None):
    """Standalone: (cleaned .spz bytes, report) of an existing scene."""
    d = read_spz(data)
    q = d["quat_xyz"]
    quat = np.c_[np.sqrt(np.clip(1 - (q ** 2).sum(1), 0, 1)), q]
    M = world_matrix(frame)
    keep, report = run(d["xyz"], d["log_scale"], quat, d["alpha"] / 255.0, M, doc, geometry_cameras(doc), params)
    return spz_subset(data, keep), report


def _params(pairs):
    p = CleanupParams()
    for kv in pairs or []:
        k, v = kv.split("=", 1)
        if k not in p.to_dict():
            raise SystemExit(f"unknown parameter {k}; known: {sorted(p.to_dict())}")
        setattr(p, k, type(getattr(p, k))(v))
    return p


def main(argv=None):
    ap = argparse.ArgumentParser(description="Remove floaters from a wall splat using the wall geometry.")
    ap.add_argument("--spz", required=True)
    ap.add_argument("--frame", required=True, help="the job's frame.json (toWorldMm, refinement)")
    ap.add_argument("--geometry", required=True, help="the solved wall-geometry document")
    ap.add_argument("--out", required=True)
    ap.add_argument("--report")
    ap.add_argument("--set", nargs="*", metavar="NAME=VALUE", help="override a CleanupParams threshold")
    a = ap.parse_args(argv)
    with open(a.spz, "rb") as fh:
        data = fh.read()
    with open(a.frame) as fh:
        frame = json.load(fh)
    with open(a.geometry) as fh:
        doc = json.load(fh)
    out, report = clean_spz(data, frame, doc, _params(a.set))
    with open(a.out, "wb") as fh:
        fh.write(out)
    report = {"applied": True, "standalone": True, **report, "bytesBefore": len(data), "bytesAfter": len(out)}
    if a.report:
        with open(a.report, "w") as fh:
            json.dump(report, fh, indent=1)
    json.dump(report, sys.stdout)
    print()


if __name__ == "__main__":
    main()
