"""Training a bundle through the all-in-one job's OWN trainer path: pipeline.Run.train (gsplat fitted to the
VRAM with one out-of-memory retry and, with the bundle's zones.json, the tuned wall-zone training; or
Brush fitted to the memory budget), with trainers.select() / trainers.job_profile() as on the worker."""
import os
import time
from types import SimpleNamespace

from .. import gpu, trainers
from ..pipeline import Run
from ..prepare import banded
from ..profiles import PROFILES
from ..resources import memory_budget, system_memory
from ..settings import settings

TRAIN_BANDS = {"train": (0.0, 1.0)}


class RunnerSfm:
    """Stands in for sfm.Sfm in Run.train: the host memory budget and the retries list."""

    def __init__(self):
        self.retries, self.brush_budget_mb = [], None

    def train_budget_mb(self):
        self.brush_budget_mb, _ = memory_budget(system_memory(), settings.max_memory_mb, settings.min_memory_mb)
        return self.brush_budget_mb


class BundleRun(Run):
    """Just enough of a pipeline.Run for Run.train on an unpacked bundle (no photos, no geometry: the zones
    come as a file, trainers.write_zones)."""
    begin, report = banded(TRAIN_BANDS)

    def __init__(self, work_dir, progress, profile, profile_note, zones_file, requested=None):  # noqa: no photos
        self.dir, self.progress = work_dir, progress
        self.timings, self.stage, self.t0 = {}, None, None
        self.log = os.path.join(work_dir, "tools.log")
        self.suffix, self.note, self.inputs = None, None, None
        self.profile, self.profile_note = profile, profile_note
        self.opts = SimpleNamespace(quality=requested or profile.name)  # stats.qualityRequested
        self.geometry, self.model, self.zones, self.zones_file = None, None, None, zones_file
        self.sfm_run = RunnerSfm()


def runner_profile(doc_profile, trainer=None, gpu_info=None):
    """(profile, note) of a bundle's train.json through trainers.job_profile: the request's quality with its
    maxSteps / maxImageEdge where they differ from the profile's defaults; ultra only with gsplat on >= 12 GB
    here (else max)."""
    base = PROFILES[doc_profile.name]
    opts = SimpleNamespace(quality=doc_profile.name,
                           maxSteps=doc_profile.steps if doc_profile.steps != base.steps else None,
                           maxImageEdge=doc_profile.edge if doc_profile.edge != base.edge else None)
    return trainers.job_profile(opts, trainer, gpu_info)


def flat_stats(stats):
    """The trainer's stats as the server keeps them: a flat object (dicts flattened or dropped)."""
    out = {}
    for k, v in stats.items():
        if k == "zones" and isinstance(v, dict):
            out.update({f"zones{n.capitalize()}": v.get(n) for n in ("wall", "surround", "outside")})
        elif k == "frameCheck" and isinstance(v, dict):
            out["frameCheckSpreadRatio"] = v.get("spreadRatio")
        elif not isinstance(v, (dict, list)) and not (k == "zones" and v is None):
            out[k] = v
    return out


def train_bundle(dataset, work_dir, doc_profile, zones_file, progress):
    """Trains the unpacked bundle; returns (ply path, stats). progress(fraction, stage, detail=None)."""
    trainer = trainers.select()
    info = gpu.vram() if trainer == "gsplat" else None
    profile, note = runner_profile(doc_profile, trainer, info)
    run = BundleRun(work_dir, progress, profile, note, zones_file, doc_profile.name)
    t0 = time.time()
    ply = run.train(dataset)
    run.end()
    stats = {**flat_stats(run.brush_stats), "trainer": trainer, "zoned": run.zones is not None,
             "trainingSeconds": round(time.time() - t0, 1), "trainMemoryBudgetMb": run.sfm_run.brush_budget_mb,
             "profileNote": note, "retries": run.sfm_run.retries}
    return ply, stats
