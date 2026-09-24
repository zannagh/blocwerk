"""The splat job: stages, progress bands, failure reasons.

all-in-one (kind `splat`): ingest -> sfm-features -> sfm-matching -> sfm-mapping -> undistort -> train
-> [align + refine] -> crop -> cleanup -> export.
split: `splat-prepare` runs ingest .. undistort and packs the training bundle (bundle.py) plus the
finish state (prepared.json); a 3D runner trains (gpurunner); `splat-finish` does the rest (finish.py).
"""
import json
import math
import os
import shutil

from computejobs.child import JobError

from . import colour
from .bundle import build_bundle, train_doc
from .finish import finish
from .frames import build_pairs, is_frame, split
from .groups import camera_group, video_group  # noqa: F401 - re-exported (tests, callers)
from .ingest import clean_jpeg, downscale
from .options import SplatOptions, resolve_matcher
from .settings import settings
from .sfm import Sfm
from .stages import Stages
from .training import image_sizes, train_fitted  # noqa: F401 - image_sizes re-exported

# overall progress band per stage (train dominates; bands only need to be monotone)
BANDS = {"ingest": (0.0, 0.02), "sfm-features": (0.02, 0.08), "sfm-matching": (0.08, 0.22),
         "sfm-mapping": (0.22, 0.26), "undistort": (0.26, 0.28), "train": (0.28, 0.95),
         "align": (0.95, 0.955), "crop": (0.955, 0.965), "export": (0.965, 1.0)}
# kind `splat-prepare`: the same stages without training, over the job's own 0..1
PREPARE_BANDS = {"ingest": (0.0, 0.05), "sfm-features": (0.05, 0.25), "sfm-matching": (0.25, 0.7),
                 "sfm-mapping": (0.7, 0.8), "undistort": (0.8, 0.9), "bundle": (0.9, 1.0)}
MIN_REGISTERED_FRACTION = 0.5
PREPARED_VERSION = 1


class Run(Stages):
    def __init__(self, job_dir, progress, bands=None):
        super().__init__(job_dir, progress, bands or BANDS)
        with open(os.path.join(job_dir, "inputs.json")) as fh:
            self.inputs = json.load(fh)
        self.opts = SplatOptions(**self.inputs["options"])
        self.profile = self.opts.profile()
        gpath = os.path.join(job_dir, "geometry.json")
        self.geometry = json.load(open(gpath)) if os.path.exists(gpath) else None
        # photos (marker stills: align + registration minimum) vs auxiliary video frames (coverage only)
        self.photo_stems, self.frame_stems = split(self.inputs["photos"])
        self.sfm_run = None

    # ----- stages -----
    def ingest(self):
        """Downscale the (already metadata-free) arrivals and group them by camera for COLMAP."""
        self.begin("ingest")
        from PIL import Image
        photos, cams = self.inputs["photos"], {}
        if self.geometry:
            cams = {c["image"]: c for c in self.geometry.get("cameras", [])}
        img_dir = os.path.join(self.dir, "images")
        groups, fixes, after = {}, self.colour_plan(), {}
        for i, (stem, facts) in enumerate(sorted(photos.items())):
            src = os.path.join(self.dir, "arrived", f"{stem}.jpg")
            with Image.open(src) as im:
                edge = self.profile.frame_edge if is_frame(stem) else self.profile.edge
                small = downscale(im.convert("RGB"), edge)
            if stem in fixes:
                small = colour.apply(small, fixes[stem])
                after[stem] = colour.image_stats(small)
            key, params = video_group(small.size) if is_frame(stem) else camera_group(stem, facts, small.size, cams.get(stem))
            os.makedirs(os.path.join(img_dir, key), exist_ok=True)
            with open(os.path.join(img_dir, key, f"{stem}.jpg"), "wb") as fh:
                fh.write(clean_jpeg(small, 92))
            groups.setdefault(key, {"names": [], "params": params})["names"].append(f"{key}/{stem}.jpg")
            self.report((i + 1) / len(photos), f"{i + 1}/{len(photos)} photos")
        if after:
            self.log_colour("after", after)
        shutil.rmtree(os.path.join(self.dir, "arrived"), ignore_errors=True)
        self.groups = groups
        return img_dir

    def colour_plan(self):
        """colour.plan over the arrivals ({} when off, or when there are not both photos and frames)."""
        if not self.opts.colourMatch or not self.photo_stems or not self.frame_stems:
            return {}
        from PIL import Image
        stats = {}
        for stem in self.inputs["photos"]:
            with Image.open(os.path.join(self.dir, "arrived", f"{stem}.jpg")) as im:
                im.draft("RGB", (colour.STATS_EDGE, colour.STATS_EDGE))  # fast DCT downscale
                stats[stem] = colour.image_stats(im)
        fixes, ref = colour.plan(stats, is_frame)
        if ref:
            self.log_colour("before", stats)
            self._log_line(f"colour: reference (median photo) {ref.summary()}")
        return fixes

    def log_colour(self, when, stats):
        for name, kind in (("photos", False), ("frames", True)):
            group = colour.group_stats([s for k, s in stats.items() if is_frame(k) == kind])
            if group:
                self._log_line(f"colour: {name} {when}: {group.summary()}")

    def sfm(self, img_dir):
        n = len(self.inputs["photos"])
        self.sfm_run = sfm = Sfm(self)
        db = os.path.join(self.dir, "colmap.db")
        batches = [(g["names"], g["params"], not k.startswith("single")) for k, g in sorted(self.groups.items())]
        sfm.extract(db, img_dir, batches, 16384 if n <= 60 else 8192)
        self.matcher = resolve_matcher(self.opts.matcher, n, len(self.frame_stems))
        sfm.match(db, self.matcher, n, self.pair_list() if self.matcher == "pairs" else None)
        best = sfm.map(db, img_dir, n)
        self.check_registered(best[1] if best else {"images": {}})
        model_dir, model = best
        self.model = model
        self.begin("undistort")
        undist = os.path.join(self.dir, "dataset")
        sfm.cm.undistort(img_dir, model_dir, undist, self.profile.edge, self.report)
        return undist

    def check_registered(self, model):
        """The registration minimum counts the PHOTOS only; frames that did not register are dropped
        silently (they are not in the model, so undistort and Brush never see them)."""
        stems = {os.path.splitext(os.path.basename(k))[0] for k in model["images"]}
        n = len(self.photo_stems)
        registered = len(stems & set(self.photo_stems))
        need = max(3, math.ceil(MIN_REGISTERED_FRACTION * n))
        if registered < need:
            raise JobError("sfm-mapping", f"only {registered}/{n} images registered (need {need}): shoot "
                                          "more overlap between neighbouring photos (every spot of the wall "
                                          "in 3+ photos from different positions), avoid motion blur")
        if self.frame_stems:
            self.frames_registered = len(stems & set(self.frame_stems))
            self.suffix = f"{self.frames_registered}/{len(self.frame_stems)} video frames registered"
            self.report(1.0)

    def pair_list(self):
        """frames.build_pairs over the image names as COLMAP knows them (<group>/<stem>.jpg)."""
        names = {os.path.splitext(os.path.basename(nm))[0]: nm for g in self.groups.values() for nm in g["names"]}
        return build_pairs([names[s] for s in self.photo_stems if s in names],
                           [names[s] for s in self.frame_stems if s in names],
                           settings.frame_neighbours, settings.frame_photo_stride)

    def train(self, dataset):
        """Brush fitted to the budget (training.train_fitted)."""
        self.begin("train")
        budget = self.sfm_run.train_budget_mb()
        out = train_fitted(dataset, os.path.join(self.dir, "train"), os.path.join(self.dir, "train.log"),
                           self.profile, budget, self.report, self._log_line, self._set_note)
        self.sfm_run.retries.extend(out.retries)
        self.brush_stats = out.stats
        return out.ply

    def _set_note(self, note):
        self.note = note

    def prepared_state(self):
        """What the finish step needs of this run (prepared.json): no pixels, no photo metadata."""
        centres, registered = {}, []
        for name, centre in self.model["images"].items():
            stem = os.path.splitext(os.path.basename(name))[0]
            registered.append(stem)
            if not is_frame(stem):
                centres[stem] = [float(v) for v in centre]
        return {"version": PREPARED_VERSION, "options": self.opts.to_dict(), "geometry": self.geometry,
                "photoCentres": centres, "registered": sorted(registered), "photoStems": self.photo_stems,
                "frameStems": self.frame_stems, "points": self.model["points"],
                "meanReprojErrorPx": self.model["meanReprojErrorPx"], "matcher": self.matcher,
                "cameraGroups": len(self.groups), "sfm": self.sfm_run.stats(), "stageSeconds": dict(self.timings)}


def _keep_only(job_dir, files):
    """Only results (+ tool logs, never served) stay until the TTL."""
    for name in os.listdir(job_dir):
        if name not in files and not name.endswith(".log"):
            path = os.path.join(job_dir, name)
            shutil.rmtree(path, ignore_errors=True) if os.path.isdir(path) else os.remove(path)


def run(job_dir, progress):
    """kind `splat`: everything on this machine."""
    r = Run(job_dir, progress)
    img_dir = r.ingest()
    dataset = r.sfm(img_dir)
    ply = r.train(dataset)
    r.end()
    train_stats = {**r.brush_stats, "trainingSeconds": r.timings.get("train")}
    result = finish(job_dir, progress, r.prepared_state(), ply, train_stats, BANDS)
    _keep_only(job_dir, result["files"])
    return result


def run_prepare(job_dir, progress):
    """kind `splat-prepare`: the CPU half before training -> bundle.zip (for a 3D runner) + prepared.json."""
    r = Run(job_dir, progress, PREPARE_BANDS)
    img_dir = r.ingest()
    dataset = r.sfm(img_dir)
    r.begin("bundle")
    info = build_bundle(dataset, os.path.join(job_dir, "bundle.zip"), train_doc(r.profile), r.report)
    r.end()
    state = r.prepared_state()
    with open(os.path.join(job_dir, "prepared.json"), "w") as fh:
        json.dump(state, fh)
    files = ["bundle.zip", "prepared.json"]
    _keep_only(job_dir, files)
    return {"bundle": info, "quality": r.profile.name, "stats": {
        "photos": len(r.photo_stems), "registeredImages": len(state["registered"]),
        "videoFrames": len(r.frame_stems), "sparsePoints": state["points"], "matcher": r.matcher,
        **state["sfm"], "stageSeconds": state["stageSeconds"]}, "files": files}
