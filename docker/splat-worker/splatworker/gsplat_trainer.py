"""gsplat trainer (CUDA, NVIDIA): same shape as brush.py, `train(...) -> (ply path, parser)`.

The training itself is gsplat_train.py, run as a subprocess in the trainer's own Python (GSPLAT_PYTHON:
torch + gsplat live there, never in the worker). This module plans the run against the GPU's memory,
starts it, and checks the result is still in the COLMAP frame.

Memory: what gsplat needs is VRAM (splats, their Adam state, the rasteriser's per-pixel buffers), so
Brush's host-memory model (tuning.train_plans) does not apply. The plan fits the profile's splat cap
and edge to the free VRAM (vram_mb below); host memory only holds the decoded images (the cache,
sized from the host budget) plus torch, and the RSS watchdog still guards that. A CUDA out-of-memory
(or a watchdog kill) is retried once with a smaller cap and edge and no image cache.
"""
import os
import re
import shutil
import subprocess
from dataclasses import dataclass, replace

import numpy as np
from computejobs.child import JobError, tool_env

from .colmap_model import PINHOLE_MODELS, model_dir, read_cameras, read_points
from .parsers import GsplatParser
from .procs import ToolRun
from .profiles import ladder

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))  # the dir holding the splatworker package
HEADROOM = 0.9
# VRAM model (MB): CUDA context + torch, per 1000 splats (14 floats + gradients + 2 Adam moments + the
# rasteriser's per-splat buffers + MCMC's relocation), per megapixel of the largest training image
# (render, loss and SSIM buffers and their gradients). Calibrated on an RTX 4070 Ti SUPER (README).
VRAM_BASE_MB, VRAM_MB_PER_KSPLAT, VRAM_MB_PER_MPX = 700, 0.45, 160
HOST_BASE_MB = 3072  # torch + CUDA libraries resident in host memory, before the image cache
MIN_EDGE = 960
EDGE_STEP = 256
RETRY_CAP, RETRY_EDGE = 0.6, 0.75  # the one retry after an out-of-memory
# The trainer's options when the job has a wall geometry (zones.json), tuned on The Attic (README, "Wall
# zones"): 10 % of the cap for the surroundings, the needle penalty, per-frame appearance, a 20x weaker opacity
# regulariser than MCMC's 0.01 (it killed most of the wall's splats: 0.8 M of 3 M alive), D-SSIM on a 1024 px
# crop (the full-image SSIM cost more than the render), and a pose correction during the first steps
# (pose_steps: its gradient is an atomic sum over every splat, so it is frozen after that).
ZONED_ARGS = ["--surround-share", "0.1", "--aniso-reg", "0.1", "--appearance-lr", "1e-3", "--ssim-crop", "1024",
              "--opacity-reg", "0.0005",
              "--pose-lr", "1e-5"]


def pose_steps(steps):
    return max(500, min(5000, steps // 6))


class CudaOomError(JobError):
    """gsplat ran out of GPU memory (the job retries once on a smaller plan)."""
    kind = "vram"


@dataclass(frozen=True)
class GsplatPlan:
    profile: object  # profiles.Profile
    edge: int
    max_splats: int
    estimate_mb: int  # VRAM
    cache_mb: int  # host memory for decoded images
    fits: bool = True

    @property
    def name(self):
        return f"{self.profile.name} at {self.edge} px, <= {self.max_splats // 1000}k splats (gsplat)"


def vram_mb(sizes, edge, splats):
    """Estimated peak VRAM of a run with images of these (w, h) at `edge` growing to `splats`."""
    big = max((min(1.0, edge / max(w, h)) ** 2 * w * h for w, h in sizes), default=0) / 1e6
    return int(VRAM_BASE_MB + splats / 1000 * VRAM_MB_PER_KSPLAT + big * VRAM_MB_PER_MPX)


def _fit(profile, usable, sizes, cache_mb):
    edge = profile.edge
    while True:
        room = usable - vram_mb(sizes, edge, 0)
        cap = min(profile.max_splats, int(room / VRAM_MB_PER_KSPLAT * 1000)) if room > 0 else 0
        if cap >= profile.min_splats:
            return GsplatPlan(profile, edge, cap, vram_mb(sizes, edge, cap), cache_mb)
        if edge <= profile.min_edge:
            return None
        edge = max(profile.min_edge, edge - EDGE_STEP)


def plans(vram_budget_mb, sizes, profile, host_budget_mb=0):
    """[the plan, its out-of-memory retry]. The plan: the first profile of profiles.ladder(profile)
    whose min_splats fit HEADROOM x the free VRAM (cap raised to what fits, <= max_splats); nothing
    fits: the lowest profile at its floor (fits=False). vram_budget_mb 0 = unknown: as asked."""
    cache = max(0, int(host_budget_mb * HEADROOM) - HOST_BASE_MB) if host_budget_mb else 8192
    chain = ladder(profile)
    if vram_budget_mb <= 0:
        plan = GsplatPlan(profile, profile.edge, profile.max_splats,
                          vram_mb(sizes, profile.edge, profile.max_splats), cache)
    else:
        plan = next((p for p in (_fit(q, vram_budget_mb * HEADROOM, sizes, cache) for q in chain) if p), None)
        if plan is None:
            low = chain[-1]
            plan = GsplatPlan(low, low.min_edge, low.min_splats, vram_mb(sizes, low.min_edge, low.min_splats),
                              cache, False)
    retry = replace(plan, max_splats=int(plan.max_splats * RETRY_CAP),
                    edge=max(min(MIN_EDGE, plan.edge), int(plan.edge * RETRY_EDGE)), cache_mb=0)
    retry = replace(retry, estimate_mb=vram_mb(sizes, retry.edge, retry.max_splats))
    return [plan, retry]


def args_for(plan, eval_every=0, zones=None):
    """The trainer script's options for a plan (profile -> steps, edge, cap, growth stop), the held-out
    evaluation (GSPLAT_EVAL_EVERY; 0 = off), and with a zones.json (zone_run.write_zones) the
    wall-focused recipe (ZONED_ARGS)."""
    p = plan.profile
    return ["--steps", str(p.steps), "--max-edge", str(plan.edge), "--cap", str(plan.max_splats),
            "--refine-stop", str(p.growth_stop or 0.5), "--cache-mb", str(plan.cache_mb)] + \
        (["--eval-every", str(eval_every)] if eval_every > 0 else []) + \
        (["--zones", zones, *ZONED_ARGS, "--pose-steps", str(pose_steps(p.steps))] if zones else [])


def check_camera_models(dataset_dir):
    """gsplat_train renders pinhole cameras only: the job undistorts first (colmap.Colmap.undistort)."""
    cams = read_cameras(os.path.join(model_dir(dataset_dir), "cameras.bin"))
    bad = sorted({c["model"] for c in cams.values() if c["model"] not in PINHOLE_MODELS})
    if bad:
        raise JobError("train", f"gsplat needs undistorted (pinhole) cameras, got {', '.join(bad)}")


def tool_version(python):
    """'gsplat 1.5.3, torch 2.4.1+cu124, <GPU>' or None (not installed, or no CUDA device)."""
    code = ("import torch, gsplat; ok = torch.cuda.is_available(); "
            "print(gsplat.__version__, torch.__version__, torch.cuda.get_device_name(0) if ok else '-', ok)")
    try:
        r = subprocess.run([python, "-c", code], capture_output=True, text=True, timeout=120, env=tool_env())
    except (OSError, subprocess.SubprocessError):
        return None
    parts = r.stdout.strip().split(" ")
    if r.returncode != 0 or len(parts) < 4 or parts[-1] != "True":
        return None
    return f"gsplat {parts[0]}, torch {parts[1]}, {' '.join(parts[2:-1])}"


def train(python, dataset_dir, out_dir, plan, log_path, report, max_memory_mb=0, swap_limit_mb=0, eval_every=0,
          zones=None):
    """Train one plan on a COLMAP dataset (images/ + sparse/0, pinhole); returns (ply path, parser).
    zones: a zones.json (zone_run.write_zones) to focus the splats on the wall. Raises CudaOomError on a
    CUDA out-of-memory, procs.MemoryLimitError on a watchdog kill."""
    check_camera_models(dataset_dir)
    shutil.rmtree(out_dir, ignore_errors=True)
    os.makedirs(out_dir, exist_ok=True)
    ply = os.path.join(out_dir, "splat.ply")
    parser = GsplatParser()

    def on_line(line):
        r = parser(line)
        if r:
            report(*r)

    cmd = [python, "-m", "splatworker.gsplat_train", "--data", dataset_dir, "--out", ply, *args_for(plan, eval_every, zones)]
    env = {"PYTHONPATH": HERE, "PYTORCH_CUDA_ALLOC_CONF": "expandable_segments:True"}
    try:  # no RLIMIT_AS: CUDA reserves huge virtual ranges; the RSS watchdog sees host memory only
        ToolRun("train", cmd, out_dir, log_path, on_line, env=env, log_filter=_worth_logging,
                mem_limit_mb=max_memory_mb, swap_limit_mb=swap_limit_mb, name="gsplat").run()
    except JobError as e:
        if parser.oom:
            raise CudaOomError("train", f"gsplat ran out of GPU memory at {plan.name}: {parser.oom}") from e
        raise
    if not os.path.exists(ply):
        raise JobError("train", "gsplat exited without writing a splat (see the worker's train.log)")
    return ply, parser


def _worth_logging(line):
    m = re.match(r"(?:step|loaded) (\d+)/(\d+)", line)
    return m is None or int(m.group(1)) == int(m.group(2)) or int(m.group(1)) % (max(1, int(m.group(2)) // 20)) == 0


def check_frame(ply_xyz, dataset_dir, tolerance=1.0):
    """Guard: the trained splats must sit where COLMAP's sparse points are (the trainer must never
    normalise world space: frame.json and the alignment assume the COLMAP frame). Their median has to
    lie inside the sparse points' 2-98 % box grown by `tolerance` x its size, and their spread must be
    within 10x of the points'. Returns what it compared (stats.frameCheck), raises JobError otherwise."""
    pts, _ = read_points(os.path.join(model_dir(dataset_dir), "points3D.bin"))
    if len(pts) < 10 or len(ply_xyz) < 10:
        return None
    lo, hi = np.percentile(pts, 2, axis=0), np.percentile(pts, 98, axis=0)
    size = np.maximum(hi - lo, 1e-9)
    med = np.median(np.asarray(ply_xyz, float), axis=0)
    spread = np.linalg.norm(np.percentile(ply_xyz, 98, axis=0) - np.percentile(ply_xyz, 2, axis=0))
    ratio = spread / max(np.linalg.norm(size), 1e-9)
    if np.any(med < lo - tolerance * size) or np.any(med > hi + tolerance * size) or not 0.1 <= ratio <= 10:
        raise JobError("train", f"the trained splats are not in the COLMAP frame (median {np.round(med, 3)}, "
                                f"sparse points {np.round(lo, 3)}..{np.round(hi, 3)}, spread ratio {ratio:.2f})")
    return {"splatMedian": np.round(med, 4).tolist(), "sparseP2": np.round(lo, 4).tolist(),
            "sparseP98": np.round(hi, 4).tolist(), "spreadRatio": round(float(ratio), 3)}
