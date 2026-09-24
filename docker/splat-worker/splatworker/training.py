"""Brush training fitted to a memory budget, shared by the worker's all-in-one job (pipeline.Run.train)
and the 3D runner (gpurunner): the first plan of tuning.train_plans that fits; a memory-guard kill
steps down to the next one (the next lower quality profile), like the matching tiers."""
import os
from dataclasses import dataclass, field

from . import brush, tuning
from .procs import MemoryLimitError
from .settings import settings


@dataclass
class TrainOutcome:
    ply: str
    stats: dict
    retries: list = field(default_factory=list)


def image_sizes(img_dir):
    """(w, h) of every training image under img_dir (headers only)."""
    from PIL import Image
    sizes = []
    for root, _, files in os.walk(img_dir):
        for f in files:
            with Image.open(os.path.join(root, f)) as im:
                sizes.append(im.size)
    return sizes


def plan_note(plan, profile, budget):
    note = f"{profile.name} -> {plan.name}" if plan.profile.name != profile.name or \
        plan.edge < profile.edge else plan.name
    return note + f" (est. {plan.estimate_mb / 1024:.1f} of {budget / 1024:.1f} GB)"


def train_fitted(dataset, out_dir, log_path, profile, budget, report, log_line, set_note, stop=None):
    """Train `profile` on the COLMAP dataset (images/ + sparse/0) within `budget` MB.
    report(fraction, detail): progress; log_line(text): tools.log note; set_note(text): the stage note;
    stop: a threading.Event that kills Brush when set (the runner's cancel). Returns a TrainOutcome."""
    sizes = image_sizes(os.path.join(dataset, "images"))
    plans = tuning.train_plans(budget, sizes, profile)
    retries = []
    extra = {} if stop is None else {"stop": stop}
    for i, plan in enumerate(plans):
        note = plan_note(plan, profile, budget)
        set_note(note)
        log_line(f"train plan: {note}")
        p = plan.profile
        try:
            ply, parser = brush.train(settings.brush_bin, dataset, out_dir, p.steps, plan.edge,
                                      settings.brush_cache_dir, log_path, report, budget,
                                      settings.max_swap_growth_mb, p.brush_args(p.steps, plan.max_splats),
                                      p.checkpoints, **extra)
            break
        except MemoryLimitError as e:
            if i == len(plans) - 1 or (stop is not None and stop.is_set()):
                raise
            retries.append({"stage": "train", "reason": e.kind, "from": plan.name, "to": plans[i + 1].name})
            log_line(f"{e.message} -> retrying as {plans[i + 1].name}")
    stats = {"steps": parser.step, "brushSplatCount": parser.splats, "brushReportedTime": parser.took,
             "trainImages": len(sizes), "trainImageEdge": plan.edge, "quality": p.name,
             "qualityRequested": profile.name, "maxSplats": plan.max_splats, "trainEstimateMb": plan.estimate_mb}
    return TrainOutcome(ply, stats, retries)
