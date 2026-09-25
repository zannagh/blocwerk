"""Which trainer turns the undistorted COLMAP dataset into splats, and which quality profile a job gets.

- brush (default, SPLAT_TRAINER unset): Brush on wgpu, Metal on the Mac runner, Vulkan on Linux
  (brush.py; memory-fitted by tuning.train_plans; pipeline.Run.train).
- gsplat: CUDA on NVIDIA hosts, Linux and Windows via WSL2 Docker, where Brush has no working GPU path
  (gsplat_trainer.py; fitted to the VRAM; train_gsplat below).

select() and job_profile() only need the settings and the request options, so the GPU runner (a
bundle of images + COLMAP sparse model instead of a whole job) can reuse them with gsplat_trainer.plans
and gsplat_trainer.train (or brush.train).
"""
import os

import numpy as np
from computejobs.child import JobError

from . import gpu, gsplat_trainer, zone_run
from .groups import image_sizes
from .procs import MemoryLimitError
from .profiles import PROFILES, QUALITIES, resolve
from .settings import settings
from .splatio import read_ply

TRAINERS = ("brush", "gsplat")
ULTRA_MIN_VRAM_MB = 12 * 1024  # ultra needs a 12 GB (or bigger) card; nvidia-smi's total of a "12 GB" card is 12282


def select(name=None):
    """The trainer's name (SPLAT_TRAINER unless given); ValueError for an unknown one."""
    t = (name or settings.splat_trainer or "brush").strip().lower()
    if t not in TRAINERS:
        raise ValueError(f"SPLAT_TRAINER={t!r} is not one of {TRAINERS}")
    return t


def job_profile(opts, trainer=None, gpu_info=None):
    """(profile, note): options.quality (or SPLAT_PROFILE_OVERRIDE, which wins) with the request's
    maxSteps / maxImageEdge. ultra only with gsplat on >= ULTRA_MIN_VRAM_MB of VRAM, else max (the note
    says why). gpu_info: gpu.vram() (read when needed and not given)."""
    quality, note = settings.profile_override or opts.quality, None
    if quality not in PROFILES:
        raise ValueError(f"SPLAT_PROFILE_OVERRIDE={quality!r} is not one of {QUALITIES}")
    if quality == "ultra":
        if select(trainer) != "gsplat":
            quality, note = "max", "ultra needs the gsplat trainer: trained as max"
        else:
            info = gpu_info if gpu_info is not None else gpu.vram()
            total = (info or {}).get("totalMb") or 0
            if total < 0.98 * ULTRA_MIN_VRAM_MB:
                quality, note = "max", f"ultra needs >= 12 GB of VRAM (found {total} MB): trained as max"
    return resolve(quality, opts.maxSteps, opts.maxImageEdge), note


def _log(run, text):
    with open(run.log, "a") as fh:
        fh.write(f"# {text}\n")


def train_gsplat(run, dataset):
    """pipeline.Run.train for gsplat: plan against the free VRAM, train, retry once on a smaller plan
    after a CUDA out-of-memory or a host-memory kill, then check the result is in the COLMAP frame."""
    run.begin("train")
    info = gpu.vram()
    budget = run.sfm_run.train_budget_mb()  # host memory: the image cache + the watchdog's ceiling
    sizes = image_sizes(os.path.join(dataset, "images"))
    plans = gsplat_trainer.plans((info or {}).get("freeMb") or 0, sizes, run.profile, budget)
    zones = write_zones(run)
    if getattr(run, "profile_note", None):
        _log(run, run.profile_note)
    retries = []
    for i, plan in enumerate(plans):
        run.note = plan.name + (f" (est. {plan.estimate_mb / 1024:.1f} of {info['freeMb'] / 1024:.1f} GB VRAM)"
                                if info else "")
        _log(run, f"train plan: {run.note}; image cache {plan.cache_mb} MB; host budget {budget} MB")
        try:
            ply, parser = gsplat_trainer.train(settings.gsplat_python, dataset, os.path.join(run.dir, "train"),
                                               plan, os.path.join(run.dir, "train.log"), run.report,
                                               budget, settings.max_swap_growth_mb, eval_every=settings.gsplat_eval_every,
                                               zones=zones)
            break
        except (gsplat_trainer.CudaOomError, MemoryLimitError) as e:
            if i == len(plans) - 1:
                raise JobError("train", f"{e.message}; the smaller retry ({plan.name}) did not fit either: "
                                        "use fewer photos or a lower quality, or free the GPU") from e
            retries.append({"stage": "train", "reason": e.kind, "from": plan.name, "to": plans[i + 1].name})
            _log(run, f"{e.message} -> retrying as {plans[i + 1].name}")
    cols = read_ply(ply)
    frame_check = gsplat_trainer.check_frame(np.stack([cols["x"], cols["y"], cols["z"]], 1), dataset)
    _log(run, f"COLMAP-frame check: {frame_check}")
    run.sfm_run.retries.extend(retries)
    run.brush_stats = {"trainer": "gsplat", "gpu": (info or {}).get("name"), "steps": parser.step,
                       "trainerSplatCount": parser.splats, "trainerReportedTime": parser.took,
                       "peakVramMb": parser.peak_vram_mb, "trainImageEdge": plan.edge,
                       "trainImages": len(sizes) - (parser.eval or {}).get("views", 0),  # held-out views don't train
                       "quality": plan.profile.name, "qualityRequested": settings.profile_override or run.opts.quality,
                       "maxSplats": plan.max_splats, "trainEstimateMb": plan.estimate_mb,
                       "frameCheck": frame_check, "zones": parser.zones, **eval_stats(parser)}
    return ply


def eval_stats(parser):
    """stats.eval* of the held-out views (GSPLAT_EVAL_EVERY), or {} when it was off."""
    if not parser.eval:
        return {}
    wall = parser.eval.get("wall") or {}
    return {"evalPsnr": parser.eval["psnr"], "evalSsim": parser.eval["ssim"], "evalViews": parser.eval["views"],
            "evalEvery": settings.gsplat_eval_every, "evalWallPsnr": wall.get("psnr"), "evalWallSsim": wall.get("ssim")}


def write_zones(run):
    """zones.json for the trainer (zone_run.write_zones) when the job has a wall geometry, else None.
    Sets run.zones (the spec, for the export cut). A geometry the photos cannot be aligned to trains
    unzoned: the align stage then reports why."""
    run.zones = None
    if not getattr(run, "geometry", None):
        return None
    path = os.path.join(run.dir, "zones.json")
    try:
        _, run.zones = zone_run.write_zones(path, run.model, run.geometry, zone_run.zone_params(run.opts))
    except (JobError, ValueError, KeyError) as e:
        _log(run, f"zones: off ({e})")
        return None
    if run.zones is None:
        return None
    _log(run, f"zones: box {run.zones['boxLo']} .. {run.zones['boxHi']} mm, {len(run.zones['facets'])} facets")
    return path
