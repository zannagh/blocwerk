"""The zones in a job (zones.py): written for the trainer before training, applied at export.

write_zones: right after SfM the photos' COLMAP cameras are aligned to the wall geometry (align.py, the
same fit the frame uses later) and the zones go to <train dir>/zones.json with that COLMAP -> world
transform, for gsplat_train --zones. export_cut: after training and the plane-ICP refinement the trained
splats are mapped into the world by the refined transform and cut to the wall + surroundings (the box is
then frame["crop"], so the viewer's bounds match the file).
"""
import json
import os

import numpy as np

from .align import align, reference_centre
from .frames import is_frame
from .zones import ZoneParams, cut_mask, spec, viewer_box


def photo_centres(model):
    """{photo stem: COLMAP camera centre} of the registered photos (video frames never align)."""
    out = {}
    for name, centre in model["images"].items():
        stem = os.path.splitext(os.path.basename(name))[0]
        if not is_frame(stem):
            out[stem] = centre
    return out


def zone_params(opts):
    """The zones of a request: the box reaches options.cropMarginMm past the wall (its old crop box)."""
    return ZoneParams(box_margin_mm=float(opts.cropMarginMm))


def write_zones(path, model, geometry, params):
    """(camera frame (align.align), the zones spec) written to `path` with toWorldMm; None without facets."""
    frame = align(photo_centres(model), geometry, params.box_margin_mm)
    zs = spec(geometry, params)
    if zs is None:
        return frame, None
    zs["toWorldMm"] = frame["toWorldMm"]
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w") as fh:
        json.dump(zs, fh)
    return frame, zs


def world_matrix(frame):
    ref = frame.get("refinement") or {}
    return np.array(ref["toWorldMm"] if ref.get("applied") and ref.get("toWorldMm") else frame["toWorldMm"], float)


def export_cut(splats, frame, zs, geometry):
    """(keep mask over splatio.Splats, report); sets frame["crop"] to the surroundings box."""
    M = world_matrix(frame)
    lin = M[:3, :3]
    world = splats.xyz @ lin.T + M[:3, 3]
    scale_mm = np.exp(splats.log_scale) * np.cbrt(abs(np.linalg.det(lin)))
    keep, report = cut_mask(world, scale_mm, splats.alpha, splats.rot, lin, zs)
    frame["crop"] = viewer_box(zs, reference_centre(geometry))
    return keep, report
