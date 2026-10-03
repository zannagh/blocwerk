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

With COLMAP_USE_GPU the tiers come from gpu_matching_tiers instead: guided on the GPU, fitted to the free
VRAM (not to the host budget, which GPU matching barely touches), then unguided on the GPU, then CPU
unguided as the fallback when the GPU run fails.
"""
from dataclasses import dataclass

from .profiles import Profile, ladder

GUIDED_BYTES_PER_PAIR = 16
GUIDED_BASE, GUIDED_PER_THREAD = 1.95, 0.5
UNGUIDED_MB = 400
# CPU guided matching time (guided_seconds): ~1 s per pair of 8192-feature images on one thread (a
# conservative figure: the 16k-feature, 1-thread tier took hours on a few thousand pairs). Guided tiers
# estimated above GUIDED_MAX_S are skipped for unguided + triangulate.
GUIDED_S_PER_PAIR, GUIDED_MAX_S = 1.0, 2400
EXTRACT_MB_PER_THREAD, EXTRACT_BASE_MB = 620, 150
HEADROOM = 0.9  # plan to use at most 90 % of the budget
# CUDA matching (SiftGPU, COLMAP 4.2 src/thirdparty/SiftGPU/SiftMatchCU.cpp): each matcher allocates a
# max_num_matches^2 float distance matrix (4 B) plus, with cross-check, a max_num_matches x /8 x 4-channel
# column table (2 B per pair); guided matching runs a second (guided) matcher: 2 x 6 B x M^2 (16384: 3 GB).
# SiftGPU refuses buffers of 2^31 elements or more (M < 46341). Host memory stays small (descriptor cache
# + RANSAC verifiers). Base = CUDA context + SiftGPU's textures. RTX 4070 Ti SUPER, South Building 48 photos
# (2026-09-25): 26.6k features per image -> 8.3 GB peak (model 8.8), 29.0k -> 9.9 GB (model 10.4).
GPU_DOT_BYTES, GPU_CROSS_BYTES = 4, 2
GPU_MATCH_BASE_MB = 700
GPU_HOST_MB = 1500
GPU_MIN_MATCHES, GPU_MAX_MATCHES = 4096, 46336

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
    gpu: bool = False  # CUDA matching (COLMAP_USE_GPU): estimate_mb is then host memory, vram_mb the GPU's
    max_matches: int = 0  # GPU: SiftGPU's buffer size = the features per image it matches (0 = the caps')
    vram_mb: int = 0

    @property
    def name(self):
        if self.gpu:
            return f"GPU {'guided' if self.guided else 'unguided + triangulate'}, <= {self.max_matches} features"
        return f"guided, {self.threads} thread{'s' if self.threads != 1 else ''}" if self.guided \
            else "unguided + triangulate"


def guided_mb(feature_counts, threads):
    """Estimated peak MB of guided matching with `threads` over images with these feature counts."""
    top = sorted(feature_counts, reverse=True)[:2] + [0, 0]
    unit = GUIDED_BYTES_PER_PAIR * top[0] * (top[1] or top[0]) / (1024 * 1024)
    return int(unit * (GUIDED_BASE + GUIDED_PER_THREAD * threads))


def guided_seconds(feature_counts, threads, pairs):
    """Rough wall-clock of CPU guided matching: GUIDED_S_PER_PAIR per pair at 8192 x 8192 features,
    growing with the product of the pair's feature counts (brute-force descriptor distances), split
    over the threads."""
    if not pairs or not feature_counts:
        return 0
    f = sorted(feature_counts)[len(feature_counts) // 2]
    return int(pairs * GUIDED_S_PER_PAIR * (f / 8192) ** 2 / max(1, threads))


def matching_tiers(budget_mb, feature_counts, max_threads, pairs=0):
    """The tiers to try, best first: [guided t, guided 1 (if t > 1), unguided + triangulate]; the
    guided ones only when they fit HEADROOM x budget AND (pairs given) their estimated time stays within
    GUIDED_MAX_S: a guided tier that fits the memory on 1-2 threads only can take hours on hundreds of
    images, where unguided + triangulate takes minutes (the time-blind choice that once ran for hours)."""
    usable = budget_mb * HEADROOM
    tiers = []
    for t in range(max(1, max_threads), 0, -1):
        est = guided_mb(feature_counts, t)
        if est <= usable:
            if guided_seconds(feature_counts, t, pairs) <= GUIDED_MAX_S:
                tiers.append(Tier(True, t, est))
            if t > 1 and guided_seconds(feature_counts, 1, pairs) <= GUIDED_MAX_S:
                tiers.append(Tier(True, 1, guided_mb(feature_counts, 1)))
            break
    tiers.append(Tier(False, max(1, max_threads), UNGUIDED_MB, loose=True))
    return tiers


def gpu_match_vram_mb(max_matches, guided, cross_check=True):
    """Estimated VRAM of COLMAP's CUDA matching with SiftGPU buffers for `max_matches` features."""
    m = (max_matches + 31) // 32 * 32
    per_matcher = GPU_DOT_BYTES + (GPU_CROSS_BYTES if cross_check else 0)
    return int(GPU_MATCH_BASE_MB + (2 if guided else 1) * per_matcher * m * m / (1024 * 1024))


def _gpu_fit(usable_mb, target, guided, cross_check):
    """The largest multiple of 1024 <= target (or target itself) whose buffers fit, or 0."""
    m = target
    while m >= GPU_MIN_MATCHES:
        if gpu_match_vram_mb(m, guided, cross_check) <= usable_mb:
            return m
        m = (m - 1) // 1024 * 1024
    return 0


def gpu_matching_tiers(vram_free_mb, feature_counts, max_matches, max_threads):
    """The tiers for COLMAP_USE_GPU, best first: [GPU guided, GPU unguided + triangulate, CPU unguided +
    triangulate at max_threads (the fallback if the GPU fails)]. Never a CPU guided tier: on the CPU that
    means 1-2 threads for an hour, which the GPU does in minutes. SiftGPU matches at most `max_matches`
    features per image (the largest-scale ones; it warns "Clamping features"), so the buffer is sized to
    the largest image's features (as COLMAP does) and lowered only as far as the free VRAM demands."""
    target = max(feature_counts, default=0)
    if max_matches > 0:
        target = min(target, max_matches)
    target = min(max(target, GPU_MIN_MATCHES), GPU_MAX_MATCHES)
    usable = vram_free_mb * HEADROOM
    tiers = []
    for guided in (True, False):
        m = _gpu_fit(usable, target, guided, cross_check=guided)
        if m:
            tiers.append(Tier(guided, max(1, max_threads), GPU_HOST_MB, loose=not guided, gpu=True,
                              max_matches=m, vram_mb=gpu_match_vram_mb(m, guided, cross_check=guided)))
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


def feature_budget(n, configured):
    """SIFT features per image for a job of n images. COLMAP_MAX_FEATURES (`configured`, default 8192:
    the M4-safe cap) is the value whatever n is, so raising it also raises jobs past 60 images (video
    frames); 0 = the job's own count, 16384 up to 60 images, 8192 beyond."""
    if configured > 0:
        return configured
    return 16384 if n <= 60 else 8192
