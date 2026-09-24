"""The CPU half after training: trained splats -> [align + refine] -> crop -> cleanup -> export.

Runs inside the all-in-one job (pipeline.run) and as kind `splat-finish` (a 3D runner's upload plus
the prepare job's prepared.json). Works from the prepared state only, so both paths export alike.
"""
import json
import os

import numpy as np

from computejobs.child import JobError

from .align import align, unaligned_frame
from .cleanup import cleanup
from .options import SplatOptions
from .refine import refine_frame
from .splatio import Splats, crop_mask, read_ply, read_spz, write_splat, write_spz
from .stages import Stages

FINISH_BANDS = {"load": (0.0, 0.15), "align": (0.15, 0.5), "crop": (0.5, 0.6), "export": (0.6, 1.0)}
# what a runner's trainStats may add to the stats (scalars only; anything else is dropped)
TRAIN_STAT_KEYS = ("steps", "brushSplatCount", "brushReportedTime", "trainImages", "trainImageEdge", "quality",
                   "qualityRequested", "maxSplats", "trainEstimateMb", "trainingSeconds", "runnerGpu",
                   "runnerVersion", "trainMemoryBudgetMb")


def load_splats(path):
    """A trained scene: a binary float .ply (Brush / the runner's slim ply) or an .spz v2."""
    with open(path, "rb") as fh:
        head = fh.read(4)
    if head[:2] == b"\x1f\x8b":
        with open(path, "rb") as fh:
            return Splats.from_spz(read_spz(fh.read()))
    if head != b"ply\n":
        raise JobError("load", "the trained scene is neither a .ply nor an .spz file")
    try:
        return Splats.from_ply(read_ply(path))
    except (ValueError, KeyError) as e:
        raise JobError("load", f"unreadable .ply: {e}") from e


def clean_train_stats(doc):
    """The allow-listed scalar keys of a runner's trainStats (+ its memory retries, bounded)."""
    doc = doc if isinstance(doc, dict) else {}
    out = {k: doc[k] for k in TRAIN_STAT_KEYS if k in doc and isinstance(doc[k], (int, float, str, type(None)))
           and not isinstance(doc[k], bool) and len(str(doc[k])) <= 200}
    retries = doc.get("retries")
    out["retries"] = [r for r in retries[:10] if isinstance(r, dict)] if isinstance(retries, list) else []
    return out


class Finish(Stages):
    def __init__(self, job_dir, progress, state, bands=FINISH_BANDS):
        super().__init__(job_dir, progress, bands)
        self.state = state
        self.opts = SplatOptions(**state["options"])
        self.geometry = state.get("geometry")
        self.timings.update(state.get("stageSeconds") or {})

    def frame_and_crop(self, splats):
        if self.geometry:
            self.begin("align")
            # Photos only: the frames have no solved camera, and the alignment must not depend on them.
            centres = {k: np.asarray(v, float) for k, v in self.state["photoCentres"].items()}
            frame = refine_frame(align(centres, self.geometry, self.opts.cropMarginMm), splats, self.geometry)
        else:
            frame = unaligned_frame(splats.xyz)
        self.begin("crop")
        keep = crop_mask(splats, frame["toViewer"], frame["crop"])
        if not keep.any():
            raise JobError("crop", "no splat inside the crop box: the alignment is probably wrong "
                                   f"(camera residual {frame.get('alignment')})")
        kept = cleanup(splats.subset(keep), frame, self.geometry, self._log_line)
        return splats, kept, frame

    def export(self, all_splats, kept, frame, train_stats):
        self.begin("export")
        files = ["wall.splat"]
        write_splat(kept, os.path.join(self.dir, "wall.splat"))
        self.report(0.5)
        if self.opts.spz:
            write_spz(kept, os.path.join(self.dir, "wall.spz"))
            files.append("wall.spz")
        self.end()
        stats = self.stats(all_splats, kept, frame, train_stats, files)
        doc = {"version": 1, "coordinates": "splat files are in the COLMAP frame of this run; "
                                            "apply matrix/toViewer (-> metres, X right, Y up, Z out of the wall, "
                                            "origin = centre of the reference facet) or toWorldMm "
                                            "(-> wall-geometry world, mm)",
               **{k: v for k, v in frame.items()}, "options": self.opts.to_dict(), "stats": stats}
        with open(os.path.join(self.dir, "frame.json"), "w") as fh:
            json.dump(doc, fh, indent=1)
        files.append("frame.json")
        return {"frame": {k: v for k, v in doc.items() if k != "stats"}, "stats": stats, "files": files}

    def stats(self, all_splats, kept, frame, train_stats, files):
        st = self.state
        photos, frames, registered = st["photoStems"], st["frameStems"], set(st["registered"])
        train_stats = dict(train_stats)
        sfm = dict(st["sfm"])
        sfm["memoryRetries"] = list(sfm.get("memoryRetries") or []) + train_stats.pop("retries", [])
        if train_stats.get("trainMemoryBudgetMb") is not None:
            sfm["trainMemoryBudgetMb"] = train_stats.pop("trainMemoryBudgetMb")
        if train_stats.get("trainingSeconds") is not None:
            self.timings.setdefault("train", train_stats["trainingSeconds"])
        return {
            "photos": len(photos), "registeredImages": len(registered),
            "unregistered": sorted(set(photos) - registered),
            "videoFrames": len(frames), "videoFramesRegistered": len(set(frames) & registered),
            "sparsePoints": st["points"], "meanReprojErrorPx": st["meanReprojErrorPx"],
            "matcher": st["matcher"], "cameraGroups": st["cameraGroups"], **sfm,
            "splatsTrained": len(all_splats), "splatCount": len(kept), **train_stats,
            "trainingSeconds": train_stats.get("trainingSeconds"), "stageSeconds": self.timings,
            "alignmentResidualMm": (frame["alignment"] or {}).get("residualMmMedian"),
            "fileBytes": {f: os.path.getsize(os.path.join(self.dir, f)) for f in files},
        }


def finish(job_dir, progress, state, splat_path, train_stats, bands=FINISH_BANDS):
    """state: prepared.json (pipeline.Run.prepared_state); splat_path: the trained scene."""
    f = Finish(job_dir, progress, state, bands)
    if "load" in bands:
        f.begin("load")
    splats = load_splats(splat_path)
    all_splats, kept, frame = f.frame_and_crop(splats)
    return f.export(all_splats, kept, frame, train_stats)


def run_finish(job_dir, progress):
    """kind `splat-finish`: inputs stored by main._finish (prepared.json, trainStats.json, splat.ply|spz)."""
    with open(os.path.join(job_dir, "prepared.json")) as fh:
        state = json.load(fh)
    stats_path = os.path.join(job_dir, "trainStats.json")
    train_stats = json.load(open(stats_path)) if os.path.exists(stats_path) else {}
    name = next(n for n in ("splat.ply", "splat.spz") if os.path.exists(os.path.join(job_dir, n)))
    result = finish(job_dir, progress, state, os.path.join(job_dir, name), clean_train_stats(train_stats))
    for n in (name, "prepared.json", "trainStats.json"):
        if os.path.exists(os.path.join(job_dir, n)):
            os.remove(os.path.join(job_dir, n))
    return result
