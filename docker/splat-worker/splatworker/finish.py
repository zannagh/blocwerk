"""kind `splat-finish`: a 3D runner's trained scene + the prepare job's prepared.json -> the same files as
the all-in-one job (wall.splat, wall.spz, the uncleaned wall.raw.spz when the clean-up ran, frame.json).

FinishRun rebuilds just enough of a pipeline.Run from prepared.json to call the all-in-one job's OWN
frame_and_crop and export: align + plane-ICP refinement, the wall-zone export cut when the runner trained
with the zones (else the crop box), the floater clean-up over every camera (photos and video frames),
and the export. Whatever the tuned all-in-one path does there, a runner's result gets the same.
"""
import json
import os

import numpy as np
from computejobs.child import JobError

from .options import SplatOptions
from .pipeline import Run
from .prepare import banded, keep_only
from .slimply import spz_columns, write_slim_ply

FINISH_BANDS = {"load": (0.0, 0.1), "align": (0.1, 0.5), "crop": (0.5, 0.65), "export": (0.65, 1.0)}
# What a runner's trainStats may add to the stats: flat scalars only (the server keeps a flat object anyway).
TRAIN_STAT_KEYS = (
    "trainer", "gpu", "steps", "brushSplatCount", "brushReportedTime", "trainerSplatCount", "trainerReportedTime",
    "peakVramMb", "trainImages", "trainImageEdge", "quality", "qualityRequested", "maxSplats", "trainEstimateMb",
    "evalPsnr", "evalSsim", "evalViews", "evalEvery", "evalWallPsnr", "evalWallSsim", "frameCheckSpreadRatio",
    "zoned", "zonesWall", "zonesSurround", "zonesOutside", "trainingSeconds", "runnerGpu", "runnerVersion",
    "runnerTrainer", "runnerPlatform", "trainMemoryBudgetMb", "profileNote")
ZONE_STATS = {"zonesWall": "wall", "zonesSurround": "surround", "zonesOutside": "outside"}


def clean_train_stats(doc):
    """The allow-listed scalar keys of a runner's trainStats (+ its memory retries, bounded)."""
    doc = doc if isinstance(doc, dict) else {}
    out = {k: doc[k] for k in TRAIN_STAT_KEYS if k in doc and isinstance(doc[k], (int, float, str, bool, type(None)))
           and len(str(doc[k])) <= 200}
    retries = doc.get("retries")
    out["retries"] = [{k: str(v)[:100] for k, v in r.items() if isinstance(k, str)}
                      for r in retries[:10] if isinstance(r, dict)] if isinstance(retries, list) else []
    return out


class SfmState:
    """Stands in for sfm.Sfm in Run.export: the prepare job's SfM stats plus the runner's training retries."""

    def __init__(self, saved, retries, train_budget):
        self.saved, self.retries, self.train_budget = dict(saved or {}), list(retries), train_budget

    def stats(self):
        return {**self.saved, "memoryRetries": list(self.saved.get("memoryRetries") or []) + self.retries,
                "trainMemoryBudgetMb": self.train_budget}


class FinishRun(Run):
    begin, report = banded(FINISH_BANDS)

    def __init__(self, job_dir, progress, state, train_stats):  # noqa: super().__init__ reads photo inputs
        self.dir, self.progress = job_dir, progress
        self.timings, self.stage, self.t0 = dict(state.get("stageSeconds") or {}), None, None
        self.log = os.path.join(job_dir, "tools.log")
        self.inputs, self.suffix, self.note, self.profile_note = None, None, None, None
        self.opts = SplatOptions(**state["options"])
        self.geometry = state.get("geometry")
        self.photo_stems, self.frame_stems = list(state["photoStems"]), list(state["frameStems"])
        centres = {**state["photoCentres"], **(state.get("frameCentres") or {})}
        self.model = {"images": {f"{s}.jpg": np.asarray(c, float) for s, c in centres.items()},
                      "points": state["points"], "meanReprojErrorPx": state["meanReprojErrorPx"],
                      "meanTrackLength": state.get("meanTrackLength")}
        self.matcher, self.groups = state["matcher"], dict.fromkeys(range(int(state["cameraGroups"])))
        stats = dict(train_stats)
        self.sfm_run = SfmState(state.get("sfm"), stats.pop("retries", []), stats.pop("trainMemoryBudgetMb", None))
        if any(k in stats for k in ZONE_STATS):
            stats["zones"] = {name: stats.pop(k, None) for k, name in ZONE_STATS.items()}
        self.brush_stats = stats
        if stats.get("trainingSeconds") is not None:
            self.timings["train"] = stats["trainingSeconds"]
        # the export cut applies to a scene trained WITH the zones (gsplat), exactly as in the all-in-one job
        self.zones = state.get("zones") if stats.get("zoned") else None


def as_ply(job_dir, name):
    """The uploaded scene as a float .ply for Run.frame_and_crop (an .spz is converted)."""
    path = os.path.join(job_dir, name)
    with open(path, "rb") as fh:
        head = fh.read(4)
    if name.endswith(".spz") and head[:2] == b"\x1f\x8b":
        with open(path, "rb") as fh:
            cols = spz_columns(fh.read())
        path = os.path.join(job_dir, "splat-from-spz.ply")
        write_slim_ply(cols, path)
        return path
    if head != b"ply\n":
        raise JobError("load", "the trained scene is neither a .ply nor an .spz file")
    return path


def run_finish(job_dir, progress):
    """Inputs stored by split_api (prepared.json, trainStats.json, splat.ply | splat.spz)."""
    with open(os.path.join(job_dir, "prepared.json")) as fh:
        state = json.load(fh)
    stats_path = os.path.join(job_dir, "trainStats.json")
    train_stats = json.load(open(stats_path)) if os.path.exists(stats_path) else {}
    f = FinishRun(job_dir, progress, state, clean_train_stats(train_stats))
    f.begin("load")
    name = next(n for n in ("splat.ply", "splat.spz") if os.path.exists(os.path.join(job_dir, n)))
    ply = as_ply(job_dir, name)
    all_splats, kept, raw, frame = f.frame_and_crop(ply)
    result = f.export(all_splats, kept, raw, frame)
    keep_only(job_dir, result["files"])
    return result
