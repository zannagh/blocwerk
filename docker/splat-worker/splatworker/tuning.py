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
import math
from dataclasses import dataclass

GUIDED_BYTES_PER_PAIR = 16
GUIDED_BASE, GUIDED_PER_THREAD = 1.95, 0.5
UNGUIDED_MB = 400
EXTRACT_MB_PER_THREAD, EXTRACT_BASE_MB = 620, 150
HEADROOM = 0.9  # plan to use at most 90 % of the budget

# Brush (v0.3.0, Metal, M4, 2026-09-23) keeps every training image resident and densifies more with more
# views, so its peak grows with images x pixels: 14 photos at 1800 px peaked at 2.2 GB (118k splats);
# 53 photos + 120 video frames (173) at 1800 px were killed at 3.43 GB right after loading (limit 3.27 GB);
# the same 173 at 1280 px peaked at 3.38 GB (534k splats). Model through the two peaks:
# BRUSH_BASE_MB + images x edge^2 x 3/4 x BRUSH_BYTES_PER_PIXEL.
BRUSH_BASE_MB, BRUSH_BYTES_PER_PIXEL = 2000, 6.9
MIN_TRAIN_EDGE = 960  # below this the splat loses the holds' detail; better to fail on memory than to train blind


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


def brush_mb(images, edge):
    """Estimated peak MB of Brush training `images` images at --max-resolution `edge` (4:3 worst case)."""
    return int(BRUSH_BASE_MB + images * edge * edge * 0.75 * BRUSH_BYTES_PER_PIXEL / (1024 * 1024))


def train_edge(budget_mb, images, requested_edge):
    """The largest --max-resolution <= requested_edge (a multiple of 16) whose Brush estimate fits
    HEADROOM x budget; never below MIN_TRAIN_EDGE (nor above what was asked)."""
    if budget_mb <= 0 or images <= 0 or brush_mb(images, requested_edge) <= budget_mb * HEADROOM:
        return requested_edge
    room = budget_mb * HEADROOM - BRUSH_BASE_MB
    edge = int(math.sqrt(max(room, 0) * 1024 * 1024 / (images * 0.75 * BRUSH_BYTES_PER_PIXEL))) // 16 * 16
    return max(min(MIN_TRAIN_EDGE, requested_edge), min(requested_edge, edge))
