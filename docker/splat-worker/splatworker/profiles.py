"""Quality profiles (options.quality): how sharp a splat to train, and how the job fits one to memory.

A profile sets the photos' long edge (ingest, undistort and Brush's --max-resolution), the video
frames' own long edge (Brush keeps every training image resident, and a walk-along frame carries far
less detail per pixel than a 24 MP still: frames cost memory without adding sharpness, so they stay
smaller), the step count, and Brush's refine options: a splat cap, when growth stops, and how often it
refines. SH degree 0 for high and max: the exports (`.splat`, `.spz`, splatio.py) keep only the DC colour,
so higher SH bands only cost memory and compute (59 instead of 14 floats per splat, each with gradients
and two Adam moments) and let the DC colour drift from what every view shows.
draft keeps Brush's default (3) so it stays the pre-profile run.

ultra (gsplat on >= 12 GB of VRAM only) trains the photos at the ingest cap for 30k steps up to 3 M splats.

draft is the pre-profile behaviour (5000 steps at 1800 px, Brush's refine defaults) plus a memory-fitted
splat cap. See the README for the measured M4 numbers and the expected GPU hours.
"""
from dataclasses import dataclass, replace

QUALITIES = ("draft", "high", "max", "ultra")
DEFAULT_QUALITY = "high"


@dataclass(frozen=True)
class Profile:
    name: str
    edge: int  # photos' long edge
    frame_edge: int  # video frames' long edge
    min_edge: int  # the profile's floor when fitting memory; below it the next profile down
    steps: int
    max_splats: int  # upper bound (fitted down to the memory budget, never below min_splats)
    min_splats: int
    growth_stop: float = 0.0  # fraction of the steps after which growth stops (0 = Brush's default)
    refine_every: int = 0  # 0 = Brush's default (200)
    growth_select_fraction: float = 0.0  # 0 = Brush's default (0.1)
    sh_degree: int = 3
    checkpoints: int = 1  # exports during training (the last one is the result; earlier ones survive a kill)

    @property
    def mb_per_ksplat(self):
        """Brush's footprint per 1000 splats. SH 3 at 1280 px: ~1 MB (measured, 534k splats). SH 0 at
        2400 px: ~2 MB (the first high run on the M4 was killed at 5.0 GB while growing past ~0.6-0.8 M
        splats from 3.85 GB at 0.23 M): the per-splat cost grows with the training resolution (the
        rasteriser's per-tile buffers, presumably) far more than it shrinks with the SH degree."""
        return 1.0 if self.sh_degree > 0 else 2.0

    def brush_args(self, steps, max_splats):
        args = ["--max-splats", str(max_splats), "--sh-degree", str(self.sh_degree)]
        if self.growth_stop:
            args += ["--growth-stop-iter", str(int(steps * self.growth_stop))]
        if self.refine_every:
            args += ["--refine-every", str(self.refine_every)]
        if self.growth_select_fraction:
            args += ["--growth-select-fraction", str(self.growth_select_fraction)]
        return args


PROFILES = {
    "draft": Profile("draft", edge=1800, frame_edge=1800, min_edge=960, steps=5000,
                     max_splats=1_000_000, min_splats=250_000),
    "high": Profile("high", edge=2400, frame_edge=1280, min_edge=1920, steps=15000,
                    max_splats=2_000_000, min_splats=800_000, growth_stop=0.6, growth_select_fraction=0.15,
                    sh_degree=0, checkpoints=3),
    "max": Profile("max", edge=4032, frame_edge=1920, min_edge=3000, steps=30000,
                   max_splats=5_000_000, min_splats=2_000_000, growth_stop=0.5, growth_select_fraction=0.2,
                   sh_degree=0, checkpoints=3),
    # gsplat on a big NVIDIA GPU only (trainers.job_profile: >= ULTRA_MIN_VRAM_MB of VRAM, else max): the
    # photos at the ingest cap (INGEST_MAX_EDGE, 4096), 30k steps, up to 3 M splats. With the wall zones the
    # cap goes to the wall: on The Attic 3 M beat 6 M at 50k steps (held-out wall PSNR 23.0 vs 21.1) and
    # 5 M at 30k (22.5), in a third of the time (README, "Wall zones").
    "ultra": Profile("ultra", edge=4096, frame_edge=1920, min_edge=3000, steps=30000,
                     max_splats=3_000_000, min_splats=1_500_000, growth_stop=0.5, growth_select_fraction=0.2,
                     sh_degree=0, checkpoints=3),
}


def resolve(quality, max_steps=None, max_image_edge=None):
    """The requested profile with the request's explicit maxSteps / maxImageEdge overrides applied."""
    p = PROFILES[quality or DEFAULT_QUALITY]
    if max_steps:
        p = replace(p, steps=max_steps)
    if max_image_edge:
        p = replace(p, edge=max_image_edge, frame_edge=min(p.frame_edge, max_image_edge),
                    min_edge=min(p.min_edge, max_image_edge))
    return p


def ladder(profile):
    """The profile, then every lower one (for the memory step-down), keeping the request's overrides
    only where they still make sense (a lower profile never trains longer or larger than asked)."""
    names = QUALITIES[:QUALITIES.index(profile.name) + 1][::-1] if profile.name in QUALITIES else ()
    out = [profile]
    for name in names[1:]:
        lower = PROFILES[name]
        out.append(replace(lower, steps=min(lower.steps, profile.steps), edge=min(lower.edge, profile.edge),
                           frame_edge=min(lower.frame_edge, profile.frame_edge),
                           min_edge=min(lower.min_edge, profile.edge)))
    return out
