"""kind `splat-prepare`: the CPU half before a 3D runner trains.

The all-in-one job's own stages (pipeline.Run: ingest -> sfm-* -> undistort) on this machine, then the
training bundle (bundle.py: the undistorted images + sparse model, train.json, and the wall zones the
all-in-one job would train with) and prepared.json (what splat-finish needs to export exactly like the
all-in-one job; stays on the server), and sparse.zip (sparse_export.py: the distorted COLMAP model for
wall-geometry's solve-sfm; anchor photos, anchors.py, are in it but never in the bundle). No trainer and
no GPU needed: the profile is the REQUEST's (ultra included: the runner's GPU decides what it trains, not
this machine's).
"""
import json
import os
import shutil
import time

from computejobs.child import JobError

from . import trainers
from .bundle import build_bundle, train_doc
from .frames import is_frame
from .pipeline import Run
from .profiles import resolve
from .settings import settings
from .sparse_export import export_sparse

PREPARE_BANDS = {"ingest": (0.0, 0.05), "sfm-features": (0.05, 0.25), "sfm-matching": (0.25, 0.7),
                 "sfm-mapping": (0.7, 0.8), "undistort": (0.8, 0.9), "bundle": (0.9, 1.0)}
# prepared.json: version 2 adds frameCentres (the finish's clean-up looks through every camera) and zones;
# anchorCentres / anchorStems (anchors.py) are additive (absent from older prepare jobs).
PREPARED_VERSION = 2
PREPARED_KEYS = ("version", "quality", "options", "photoCentres", "frameCentres", "photoStems", "frameStems",
                 "points", "meanReprojErrorPx", "matcher", "cameraGroups", "sfm")
FILES = ["bundle.zip", "prepared.json", "sparse.zip"]


def _stem(name):
    return os.path.splitext(os.path.basename(name))[0]


def banded(bands):
    """begin/report of pipeline.Run over another band table (the split kinds' own 0..1)."""

    def begin(self, stage):
        self.end()
        self.stage, self.t0, self.note = stage, time.time(), None
        self.progress(bands[stage][0], stage)

    def report(self, fraction, detail=None):
        lo, hi = bands[self.stage]
        detail = "; ".join(p for p in (detail, self.note, self.suffix) if p) or None
        self.progress(lo + (hi - lo) * min(1.0, max(0.0, fraction)), self.stage, detail)

    return begin, report


class PrepareRun(Run):
    begin, report = banded(PREPARE_BANDS)

    def __init__(self, job_dir, progress):
        super().__init__(job_dir, progress)
        quality = settings.profile_override or self.opts.quality
        self.profile = resolve(quality, self.opts.maxSteps, self.opts.maxImageEdge)
        self.profile_note = None
        self.zones = None
        self.anchor_centres, self.sparse = {}, None

    def role_of(self, stem):
        return "anchor" if stem in self.anchor_stems else "frame" if is_frame(stem) else "photo"

    def after_mapping(self, img_dir, model_dir, model):
        """sparse.zip from the mapper's model (anchors included), the anchors' centres, then the model
        without the anchors (image_deleter) for undistortion and the bundle."""
        photos = self.inputs["photos"]
        sizes = {s: (f["storedWidth"], f["storedHeight"]) for s, f in photos.items()
                 if isinstance(f, dict) and f.get("storedWidth") and f.get("storedHeight")}
        self.sparse = export_sparse(model_dir, os.path.join(self.dir, "sparse.zip"), sizes, self.role_of, self.dir)
        anchors = {n: c for n, c in model["images"].items() if _stem(n) in self.anchor_stems}
        self.anchor_centres = {_stem(n): [float(v) for v in c] for n, c in anchors.items()}
        if not anchors:
            return model_dir, model
        out = os.path.join(self.dir, "sparse-noanchors")
        self.sfm_run.cm.delete_images(model_dir, out, sorted(anchors))
        model = self.sfm_run.cm.to_text(out, os.path.join(self.dir, "sparse-txt", "noanchors"))
        if any(_stem(n) in self.anchor_centres for n in model["images"]):
            raise JobError("sfm-mapping", "image_deleter left anchor images in the model")
        return out, model

    def prepared_state(self):
        """What the finish needs of this run: camera centres (COLMAP frame), counts and settings. No pixels,
        no photo metadata."""
        photos, frames = {}, {}
        for name, centre in self.model["images"].items():
            stem = os.path.splitext(os.path.basename(name))[0]
            (frames if is_frame(stem) else photos)[stem] = [float(v) for v in centre]
        return {"version": PREPARED_VERSION, "quality": self.profile.name, "options": self.opts.to_dict(),
                "geometry": self.geometry, "photoCentres": photos, "frameCentres": frames,
                "photoStems": self.photo_stems, "frameStems": self.frame_stems, "points": self.model["points"],
                "meanReprojErrorPx": self.model["meanReprojErrorPx"],
                "meanTrackLength": self.model.get("meanTrackLength"), "matcher": self.matcher,
                "cameraGroups": len(self.groups), "sfm": self.sfm_run.stats(), "stageSeconds": dict(self.timings),
                "zones": self.zones, "anchorStems": self.anchor_stems, "anchorCentres": self.anchor_centres,
                "sparse": self.sparse}


def keep_only(job_dir, files):
    """Only results (+ tool logs, never served) stay until the TTL."""
    for name in os.listdir(job_dir):
        if name not in files and not name.endswith(".log"):
            path = os.path.join(job_dir, name)
            shutil.rmtree(path, ignore_errors=True) if os.path.isdir(path) else os.remove(path)


def run_prepare(job_dir, progress):
    """photos -> bundle.zip (for a 3D runner) + prepared.json (for splat-finish) + sparse.zip (for solve-sfm)."""
    r = PrepareRun(job_dir, progress)
    img_dir = r.ingest()
    dataset = r.sfm(img_dir)
    r.begin("bundle")
    trainers.write_zones(r)  # as the all-in-one job: None unless the job opted in (wall zones) and has facets
    info = build_bundle(dataset, os.path.join(job_dir, "bundle.zip"), train_doc(r.profile), r.report, r.zones)
    r.end()
    state = r.prepared_state()
    with open(os.path.join(job_dir, "prepared.json"), "w") as fh:
        json.dump(state, fh)
    keep_only(job_dir, FILES)
    return {"bundle": info, "quality": r.profile.name, "files": list(FILES), "stats": {
        "photos": len(r.photo_stems), "registeredImages": len(state["photoCentres"]) + len(state["frameCentres"]),
        "videoFrames": len(r.frame_stems), "videoFramesRegistered": len(state["frameCentres"]),
        "sparsePoints": state["points"], "matcher": r.matcher, **state["sfm"],
        "anchors": len(r.anchor_stems), "anchorsRegistered": len(r.anchor_centres), "sparseZip": r.sparse,
        "stageSeconds": state["stageSeconds"]}}
