"""What a `splat` / `splat-prepare` job finds in its directory once its upload is parsed: the validated
inputs.json (+ geometry.json). Shared by the HTTP job (main.py) and a 3D runner executing the same kind from a
compute envelope (gpurunner/compute_envelope.py), so both build one layout."""
import json
import os

from computejobs.service import bad
from computejobs.upload import json_field

from .anchors import split_anchors
from .frames import split
from .options import OptionsError, parse_options, validate_geometry
from .settings import settings


def write_photo_inputs(job_dir, fields, photos, kind):
    """fields {name: bytes} and photos {stem: facts} as read by upload.SplatUpload (the photos are already in
    job_dir/arrived). Refuses with computejobs.service.bad (422) what the HTTP API refuses."""
    stills, frames = split(photos)  # vf_* = auxiliary video frames (frames.py): never aligned, never counted
    stills, anchors = split_anchors(stills)  # a00, a01, ... = anchor photos (anchors.py): prepare only
    if anchors and kind != "splat-prepare":
        bad(f"anchor photos ({', '.join(anchors[:3])}, ...) are only accepted by splat-prepare", 422)
    try:
        options = parse_options(json_field(fields, "options"))
        geometry = json_field(fields, "geometry")
        if geometry is not None:
            validate_geometry(geometry)
            if not {c["image"] for c in geometry["cameras"]} & set(stills):
                raise OptionsError("no photo file name matches a camera 'image' of the geometry document")
    except OptionsError as e:
        bad(str(e), 422)
    if len(stills) < settings.min_photos:
        extra = f" and {len(frames)} video frames" if frames else ""
        bad(f"need at least {settings.min_photos} photos (got {len(stills)}{extra})", 422)
    if geometry is not None:
        with open(os.path.join(job_dir, "geometry.json"), "w") as fh:
            json.dump(geometry, fh)
    with open(os.path.join(job_dir, "inputs.json"), "w") as fh:
        json.dump({"photos": photos, "anchors": anchors, "options": options.to_dict()}, fh)
