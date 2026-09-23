"""The splat job: stages, progress bands, failure reasons, frame.json.

ingest -> sfm-features -> sfm-matching -> sfm-mapping -> undistort -> train -> [align] -> crop -> export
"""
import hashlib
import json
import math
import os
import shutil
import time

from computejobs.child import JobError

from . import brush
from .align import align, unaligned_frame
from .frames import build_pairs, is_frame, split
from .ingest import clean_jpeg, downscale
from .options import SplatOptions, resolve_matcher
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
        groups = {}
        for i, (stem, facts) in enumerate(sorted(photos.items())):
            src = os.path.join(self.dir, "arrived", f"{stem}.jpg")
            with Image.open(src) as im:
                small = downscale(im.convert("RGB"), self.opts.maxImageEdge)
            key, params = video_group(small.size) if is_frame(stem) else camera_group(stem, facts, small.size, cams.get(stem))
            os.makedirs(os.path.join(img_dir, key), exist_ok=True)
            with open(os.path.join(img_dir, key, f"{stem}.jpg"), "wb") as fh:
                fh.write(clean_jpeg(small, 92))
            groups.setdefault(key, {"names": [], "params": params})["names"].append(f"{key}/{stem}.jpg")
            self.report((i + 1) / len(photos), f"{i + 1}/{len(photos)} photos")
        shutil.rmtree(os.path.join(self.dir, "arrived"), ignore_errors=True)
        self.groups = groups
        return img_dir

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
        sfm.cm.undistort(img_dir, model_dir, undist, self.opts.maxImageEdge, self.report)
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
        self.begin("train")
        ply, parser = brush.train(settings.brush_bin, dataset, os.path.join(self.dir, "train"),
                                  self.opts.maxSteps, self.opts.maxImageEdge, settings.brush_cache_dir,
                                  os.path.join(self.dir, "train.log"), self.report,
                                  self.sfm_run.train_budget_mb(), settings.max_swap_growth_mb)
        self.brush_stats = {"steps": parser.step, "brushSplatCount": parser.splats, "brushReportedTime": parser.took}
        return ply

    def frame_and_crop(self, ply):
        splats = Splats.from_ply(read_ply(ply))
        if self.geometry:
            self.begin("align")
            # Photos only: the frames have no solved camera, and the alignment must not depend on them.
            centres = {stem: v for stem, v in ((os.path.splitext(os.path.basename(k))[0], v)
                                               for k, v in self.model["images"].items()) if not is_frame(stem)}
            frame = align(centres, self.geometry, self.opts.cropMarginMm)
        else:
            frame = unaligned_frame(splats.xyz)
        self.begin("crop")
        keep = crop_mask(splats, frame["toViewer"], frame["crop"])
        if not keep.any():
            raise JobError("crop", "no splat inside the crop box: the alignment is probably wrong "
                                   f"(camera residual {frame.get('alignment')})")
        return splats, splats.subset(keep), frame

    def export(self, all_splats, kept, frame):
        self.begin("export")
        files = ["wall.splat"]
        write_splat(kept, os.path.join(self.dir, "wall.splat"))
        self.report(0.5)
        if self.opts.spz:
            write_spz(kept, os.path.join(self.dir, "wall.spz"))
            files.append("wall.spz")
        self.end()
        registered = {os.path.splitext(os.path.basename(k))[0] for k in self.model["images"]}
        stats = {
            "photos": len(self.photo_stems), "registeredImages": len(self.model["images"]),
            "unregistered": sorted(set(self.photo_stems) - registered),
            "videoFrames": len(self.frame_stems), "videoFramesRegistered": len(set(self.frame_stems) & registered),
            "sparsePoints": self.model["points"], "meanReprojErrorPx": self.model["meanReprojErrorPx"],
            "matcher": self.matcher, "cameraGroups": len(self.groups), **self.sfm_run.stats(),
            "splatsTrained": len(all_splats), "splatCount": len(kept), **self.brush_stats,
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


def camera_group(stem, facts, small, geo_cam):
    """(group key, RADIAL params f,cx,cy,k1,k2 at the downscaled size or None).

    Photos sharing a lens (and orientation, and size) share one COLMAP camera. Intrinsics prior:
    the solver's calibrated K for this photo if the geometry has it, else the EXIF 35 mm focal length.
    Without either, the photo gets its own camera (safe for mixed lenses)."""
    w, h = small
    orient = "land" if w >= h else "port"
    gw, gh = (geo_cam or {}).get("width") or 0, (geo_cam or {}).get("height") or 0
    if geo_cam and geo_cam.get("K") and gw and gh and abs(w / gw - h / gh) < 0.01 * w / gw:
        s = w / gw  # same aspect and orientation as the photo the solver calibrated
        K = [float(v) for row in geo_cam["K"] for v in (row if isinstance(row, list) else [row])]
        fx, fy, cx, cy = K[0], K[4], K[2], K[5]  # 3x3 or flat row-major 9
        dist = list(geo_cam.get("dist") or []) + [0.0, 0.0]
        full = str(geo_cam.get("group") or "geo")
        group = "".join(ch if ch.isalnum() else "_" for ch in full)[:40]
        # The readable prefix is truncated, and two lenses of one phone share it ("iPhone 16 Pro back
        # triple camera 2.22mm" vs "…6.765mm"), so the key also carries a digest of the full group and
        # its calibration: photos only share a COLMAP camera when the solver calibrated them alike.
        digest = hashlib.sha1(f"{full}|{fx:.1f}|{fy:.1f}|{cx:.1f}|{cy:.1f}".encode()).hexdigest()[:8]
        return f"geo_{group}_{digest}_{orient}_{w}x{h}", [(fx + fy) / 2 * s, cx * s, cy * s, dist[0], dist[1]]
    if facts.get("focal35"):
        f = facts["focal35"] / 36.0 * max(w, h)
        return f"exif_{facts.get('lensKey') or 'lens'}_{int(facts['focal35'])}_{orient}_{w}x{h}", \
            [f, w / 2, h / 2, 0.0, 0.0]
    return "single", None


def video_group(small):
    """All frames of the walk-along video share one COLMAP camera (one lens, one size, no EXIF prior:
    COLMAP estimates the focal length from the many views)."""
    w, h = small
    return f"video_{'land' if w >= h else 'port'}_{w}x{h}", None


def run(job_dir, progress):
    r = Run(job_dir, progress)
    img_dir = r.ingest()
    dataset = r.sfm(img_dir)
    ply = r.train(dataset)
    all_splats, kept, frame = r.frame_and_crop(ply)
    result = r.export(all_splats, kept, frame)
    for name in os.listdir(job_dir):  # only results (+ tool logs, never served) stay until the TTL
        if name not in result["files"] and not name.endswith(".log"):
            path = os.path.join(job_dir, name)
            shutil.rmtree(path, ignore_errors=True) if os.path.isdir(path) else os.remove(path)
    return result
