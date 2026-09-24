"""The splat job: stages, progress bands, failure reasons, frame.json.

ingest -> sfm-features -> sfm-matching -> sfm-mapping -> undistort -> train -> [align + refine] -> crop
[+ floater clean-up] -> export
"""
import json
import math
import os
import shutil
import time

from computejobs.child import JobError

from . import brush, colour, tuning
from .align import align, unaligned_frame
from .cleanup_run import RAW_FILE, clean_job
from .frames import build_pairs, is_frame, split
from .groups import camera_group, image_sizes, video_group
from .ingest import clean_jpeg, downscale
from .options import SplatOptions, resolve_matcher
from .procs import MemoryLimitError
from .refine import refine_frame
from .settings import settings
from .sfm import Sfm
from .splatio import Splats, crop_mask, read_ply, write_splat, write_spz

# overall progress band per stage (train dominates; bands only need to be monotone)
BANDS = {"ingest": (0.0, 0.02), "sfm-features": (0.02, 0.08), "sfm-matching": (0.08, 0.22),
         "sfm-mapping": (0.22, 0.26), "undistort": (0.26, 0.28), "train": (0.28, 0.95),
         "align": (0.95, 0.955), "crop": (0.955, 0.965), "export": (0.965, 1.0)}
MIN_REGISTERED_FRACTION = 0.5


class Run:
    def __init__(self, job_dir, progress):
        self.dir, self.progress = job_dir, progress
        self.timings, self.stage, self.t0 = {}, None, None
        with open(os.path.join(job_dir, "inputs.json")) as fh:
            self.inputs = json.load(fh)
        self.opts = SplatOptions(**self.inputs["options"])
        self.profile = self.opts.profile()
        gpath = os.path.join(job_dir, "geometry.json")
        self.geometry = json.load(open(gpath)) if os.path.exists(gpath) else None
        self.log = os.path.join(job_dir, "tools.log")
        # photos (marker stills: align + registration minimum) vs auxiliary video frames (coverage only)
        self.photo_stems, self.frame_stems = split(self.inputs["photos"])
        self.suffix = None  # appended to every stage detail once known ("87/120 video frames registered")
        self.note = None  # appended to the details of the current stage ("memory budget 5.2 GB (...)")
        self.sfm_run = None

    # ----- progress helpers -----
    def begin(self, stage):
        self.end()
        self.stage, self.t0, self.note = stage, time.time(), None
        self.progress(BANDS[stage][0], stage)

    def end(self):
        if self.stage:
            self.timings[self.stage] = round(time.time() - self.t0, 1)
            self.stage = None

    def report(self, fraction, detail=None):
        lo, hi = BANDS[self.stage]
        detail = "; ".join(p for p in (detail, self.note, self.suffix) if p) or None
        self.progress(lo + (hi - lo) * min(1.0, max(0.0, fraction)), self.stage, detail)

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

    def _log_line(self, text):
        with open(self.log, "a") as fh:
            fh.write(f"# {text}\n")

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
        """Brush on the first plan of tuning.train_plans that fits; a memory-guard kill steps down to
        the next one (the next lower quality profile), like the matching tiers."""
        self.begin("train")
        budget = self.sfm_run.train_budget_mb()
        sizes = image_sizes(os.path.join(dataset, "images"))
        plans = tuning.train_plans(budget, sizes, self.profile)
        retries = []
        for i, plan in enumerate(plans):
            self.note = f"{self.profile.name} -> {plan.name}" if plan.profile.name != self.profile.name or \
                plan.edge < self.profile.edge else plan.name
            self.note += f" (est. {plan.estimate_mb / 1024:.1f} of {budget / 1024:.1f} GB)"
            self._log_line(f"train plan: {self.note}")
            p = plan.profile
            try:
                ply, parser = brush.train(settings.brush_bin, dataset, os.path.join(self.dir, "train"),
                                          p.steps, plan.edge, settings.brush_cache_dir,
                                          os.path.join(self.dir, "train.log"), self.report,
                                          budget, settings.max_swap_growth_mb,
                                          p.brush_args(p.steps, plan.max_splats), p.checkpoints)
                break
            except MemoryLimitError as e:
                if i == len(plans) - 1:
                    raise
                retries.append({"stage": "train", "reason": e.kind, "from": plan.name, "to": plans[i + 1].name})
                self._log_line(f"{e.message} -> retrying as {plans[i + 1].name}")
        self.sfm_run.retries.extend(retries)
        self.brush_stats = {"steps": parser.step, "brushSplatCount": parser.splats, "brushReportedTime": parser.took,
                            "trainImages": len(sizes), "trainImageEdge": plan.edge, "quality": p.name,
                            "qualityRequested": self.profile.name, "maxSplats": plan.max_splats,
                            "trainEstimateMb": plan.estimate_mb}
        return ply

    def frame_and_crop(self, ply):
        splats = Splats.from_ply(read_ply(ply))
        if self.geometry:
            self.begin("align")
            # Photos only: the frames have no solved camera, and the alignment must not depend on them.
            centres = {stem: v for stem, v in ((os.path.splitext(os.path.basename(k))[0], v)
                                               for k, v in self.model["images"].items()) if not is_frame(stem)}
            frame = refine_frame(align(centres, self.geometry, self.opts.cropMarginMm), splats, self.geometry)
        else:
            frame = unaligned_frame(splats.xyz)
        self.begin("crop")
        keep = crop_mask(splats, frame["toViewer"], frame["crop"])
        if not keep.any():
            raise JobError("crop", "no splat inside the crop box: the alignment is probably wrong "
                                   f"(camera residual {frame.get('alignment')})")
        cropped = splats.subset(keep)
        if not (self.geometry and self.opts.cleanup):
            return splats, cropped, None, frame
        self.report(0.5, "removing floaters")
        centres = [v for v in self.model["images"].values()]  # photos AND video frames look through the air
        clean, frame["cleanup"] = clean_job(cropped, frame, self.geometry, centres)
        return splats, cropped.subset(clean), cropped if frame["cleanup"]["applied"] else None, frame

    def export(self, all_splats, kept, raw, frame):
        """wall.splat / wall.spz of the kept splats; wall.raw.spz = the scene before the clean-up."""
        self.begin("export")
        files = ["wall.splat"]
        write_splat(kept, os.path.join(self.dir, "wall.splat"))
        self.report(0.5)
        if self.opts.spz:
            write_spz(kept, os.path.join(self.dir, "wall.spz"))
            files.append("wall.spz")
        if raw is not None:
            write_spz(raw, os.path.join(self.dir, RAW_FILE))
            files.append(RAW_FILE)
        self.end()
        registered = {os.path.splitext(os.path.basename(k))[0] for k in self.model["images"]}
        stats = {
            "photos": len(self.photo_stems), "registeredImages": len(self.model["images"]),
            "unregistered": sorted(set(self.photo_stems) - registered),
            "videoFrames": len(self.frame_stems), "videoFramesRegistered": len(set(self.frame_stems) & registered),
            "sparsePoints": self.model["points"], "meanReprojErrorPx": self.model["meanReprojErrorPx"],
            "matcher": self.matcher, "cameraGroups": len(self.groups), **self.sfm_run.stats(),
            "splatsTrained": len(all_splats), "splatCount": len(kept),
            "splatsBeforeCleanup": len(raw) if raw is not None else None, **self.brush_stats,
            "trainingSeconds": self.timings.get("train"), "stageSeconds": self.timings,
            "alignmentResidualMm": (frame["alignment"] or {}).get("residualMmMedian"),
            "fileBytes": {f: os.path.getsize(os.path.join(self.dir, f)) for f in files},
        }
        doc = {"version": 1, "coordinates": "splat files are in the COLMAP frame of this run; "
                                            "apply matrix/toViewer (-> metres, X right, Y up, Z out of the wall, "
                                            "origin = centre of the reference facet) or toWorldMm "
                                            "(-> wall-geometry world, mm)",
               **{k: v for k, v in frame.items()}, "options": self.opts.to_dict(), "stats": stats}
        with open(os.path.join(self.dir, "frame.json"), "w") as fh:
            json.dump(doc, fh, indent=1)
        files.append("frame.json")
        return {"frame": {k: v for k, v in doc.items() if k != "stats"}, "stats": stats, "files": files}


def run(job_dir, progress):
    r = Run(job_dir, progress)
    img_dir = r.ingest()
    dataset = r.sfm(img_dir)
    ply = r.train(dataset)
    all_splats, kept, raw, frame = r.frame_and_crop(ply)
    result = r.export(all_splats, kept, raw, frame)
    for name in os.listdir(job_dir):  # only results (+ tool logs, never served) stay until the TTL
        if name not in result["files"] and not name.endswith(".log"):
            path = os.path.join(job_dir, name)
            shutil.rmtree(path, ignore_errors=True) if os.path.isdir(path) else os.remove(path)
    return result
