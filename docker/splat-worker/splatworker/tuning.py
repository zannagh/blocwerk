"""Fit COLMAP's settings to the job's memory budget (resources.memory_budget).

Cost model, measured on the real 14-photo B3 capture (M4, COLMAP 4.2, 1800 px, 8.2k-13.4k SIFT
features per photo, physical footprint via /usr/bin/time -l, 2026-09-23):
- guided matching: one pair of 10354 x 12382 features alone peaks at 2.0 GB (~16 bytes per
  feature pair: a features x features distance matrix plus its guided copy). The whole exhaustive run
  peaked at 6.2 GB with 1 thread, 6.7 GB with 2, 9.7 GB with 4: COLMAP keeps 2 matchers per thread,
  and neighbouring big pairs overlap. Model: U x (GUIDED_BASE + GUIDED_PER_THREAD x threads), U = 16 B x
  the two largest feature counts (2.53 GB here) -> 6.2 / 7.5 / 10.0 GB, at or above every measurement.
  `max_num_matches` does not change it (2048 vs 8192: same peak).
- unguided matching: 140-280 MB whatever the threads (the matchers keep an index, not a matrix).
- feature extraction (affine shape + domain-size pooling): ~620 MB per thread (2.5 GB at 4).

Tiers, best first: guided with as many threads as fit (<= COLMAP_THREADS), then "unguided +
triangulate" (looser ratio test, no cross-check, then point_triangulator keeping two-view tracks:
16k points, 0.66 px on the same photos vs 9k guided / 4.2k plain unguided). A step the memory guard
kills is retried on the next tier down (pipeline.Run._match).
"""
from dataclasses import dataclass

from .profiles import Profile, ladder

GUIDED_BYTES_PER_PAIR = 16
GUIDED_BASE, GUIDED_PER_THREAD = 1.95, 0.5
UNGUIDED_MB = 400
EXTRACT_MB_PER_THREAD, EXTRACT_BASE_MB = 620, 150
HEADROOM = 0.9  # plan to use at most 90 % of the budget

# Brush (v0.3.0, Metal, M4, 2026-09-23) keeps every training image resident and its peak grows with the
# splat count: base + resident pixels + splats. Fit through three measured runs: 14 photos at 1800 px
# (34 Mpx, 118k splats) peaked at 2.2 GB; 53 photos + 120 video frames at 1280 px (175 Mpx, 534k splats)
# at 3.38 GB; the same 173 at 1800 px (347 Mpx, ~140k splats) were killed at 3.43 GB right after loading.
# ~1 kB per splat = its 59 floats (SH degree 3) with gradients and two Adam moments (Profile.mb_per_ksplat).
BRUSH_BASE_MB, BRUSH_MB_PER_MPX = 1900, 5.4
MIN_TRAIN_EDGE = 960  # below this the splat loses the holds' detail; better to fail on memory than to train blind
EDGE_STEP = 160  # how far one fitting step lowers the edge


@dataclass(frozen=True)
class Tier:
    guided: bool
    threads: int  # 0 = COLMAP_THREADS
    estimate_mb: int
    loose: bool = False  # unguided tier: looser ratio test, no cross-check, then point_triangulator

    @property
    def name(self):
        return f"guided, {self.threads} thread{'s' if self.threads != 1 else ''}" if self.guided \
            else "unguided + triangulate"


def guided_mb(feature_counts, threads):
    """Estimated peak MB of guided matching with `threads` over images with these feature counts."""
    top = sorted(feature_counts, reverse=True)[:2] + [0, 0]
    unit = GUIDED_BYTES_PER_PAIR * top[0] * (top[1] or top[0]) / (1024 * 1024)
    return int(unit * (GUIDED_BASE + GUIDED_PER_THREAD * threads))


def matching_tiers(budget_mb, feature_counts, max_threads):
    """The tiers to try, best first: [guided t, guided 1 (if t > 1), unguided + triangulate]; the
    guided ones only when they fit HEADROOM x budget."""
    usable = budget_mb * HEADROOM
    tiers = []
    for t in range(max(1, max_threads), 0, -1):
        est = guided_mb(feature_counts, t)
        if est <= usable:
            tiers.append(Tier(True, t, est))
            if t > 1:
                tiers.append(Tier(True, 1, guided_mb(feature_counts, 1)))
            break
    tiers.append(Tier(False, max(1, max_threads), UNGUIDED_MB, loose=True))
    return tiers


def extraction_threads(budget_mb, max_threads):
    """SIFT extraction threads that fit the budget (at least 1, at most COLMAP_THREADS)."""
    fit = int((budget_mb * HEADROOM - EXTRACT_BASE_MB) // EXTRACT_MB_PER_THREAD)
    return max(1, min(max(1, max_threads), fit))


def dataset_mpx(sizes, edge):
    """Megapixels Brush keeps resident for images of these (w, h) sizes at --max-resolution `edge`."""
    total = 0.0
    for w, h in sizes:
        s = min(1.0, edge / max(w, h))
        total += w * s * h * s
    return total / 1e6


def brush_mb(mpx, splats, mb_per_ksplat=1.0):
    """Estimated peak MB of Brush with `mpx` resident megapixels growing to `splats` splats."""
    return int(BRUSH_BASE_MB + mpx * BRUSH_MB_PER_MPX + splats / 1000 * mb_per_ksplat)


@dataclass(frozen=True)
class TrainPlan:
    profile: Profile
    edge: int
    max_splats: int
    estimate_mb: int
    fits: bool = True

    @property
    def name(self):
        return f"{self.profile.name} at {self.edge} px, <= {self.max_splats // 1000}k splats"


def _fit(profile, usable, sizes):
    """The largest edge (EDGE_STEP steps from profile.edge down to profile.min_edge) at which
    profile.min_splats fit `usable` MB, with the splat cap raised to what fits (<= max_splats); or None."""
    edge = profile.edge
    while True:
        mpx = dataset_mpx(sizes, edge)
        room = usable - brush_mb(mpx, 0)
        splats = min(profile.max_splats, int(room / profile.mb_per_ksplat * 1000)) if room > 0 else 0
        if splats >= profile.min_splats:
            return TrainPlan(profile, edge, splats, brush_mb(mpx, splats, profile.mb_per_ksplat))
        if edge <= profile.min_edge:
            return None
        edge = max(profile.min_edge, edge - EDGE_STEP)


def train_plans(budget_mb, sizes, profile):
    """The Brush runs to try, best first: one per profile of profiles.ladder(profile) that fits
    HEADROOM x budget (the memory guard stepping down to the next one on a kill). When nothing fits,
    the lowest profile at its floor anyway (fits=False): the guard decides. budget 0 = unknown: as asked."""
    chain = ladder(profile)
    if budget_mb <= 0:
        return [TrainPlan(p, p.edge, p.max_splats, brush_mb(dataset_mpx(sizes, p.edge), p.max_splats, p.mb_per_ksplat))
                for p in chain]
    plans = [plan for plan in (_fit(p, budget_mb * HEADROOM, sizes) for p in chain) if plan]
    if not plans:
        low = chain[-1]
        edge = max(min(MIN_TRAIN_EDGE, low.edge), low.min_edge)
        est = brush_mb(dataset_mpx(sizes, edge), low.min_splats, low.mb_per_ksplat)
        plans.append(TrainPlan(low, edge, low.min_splats, est, False))
    return plans
