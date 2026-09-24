"""Floater clean-up of a trained splat, from the known wall geometry (CPU, numpy only).

A trained scene is the wall, its holds, the floor and mats and the room around them (side walls, a
window, a clock) plus junk the optimiser left in the air: fog blobs, "needles" and lines that only
make sense from the training views. Everything happens in the wall-geometry world (mm; x right, y into
the wall, z = gravity up) and every splat gets one verdict (the first rule that removes it wins):

  on-surface   KEEP: within a slab of a facet (inside its outline + margin) or in the floor/mat band,
               unless the splat sticks out of that surface (a hair on the wall's edge); and the room's
               side wall: side_slab_mm behind a side facet's plane, past its outline too, up to the
               wall top. These are never touched by the rules below, except for the hairs of `needle`.
  above-top    the highest marker + top_margin_mm is the top of the climbing wall; above it, over the
               wall's lateral extent (NOT beyond its side edges, where the room goes on), is removed.
  behind-wall  behind a front-facing facet (beyond behind_tol_mm) inside its outline + margin, or below
               the floor. Side facets (yawed > side_yaw_deg from the reference facet) keep what is
               behind them: that is the room past the wall's edges.
  seen-through the free space the cameras look through: the segments from every camera to the surface
               evidence it saw, ending carve_stop_mm short of it. A cell that carve_min_cameras
               cameras see through is air (carve_min_cameras_dense when it holds evidence itself).
  needle       a long, thin splat (the "lines") away from any surface evidence, or a very long one
               anywhere (a hair). Near evidence a streak is texture (a window's view, an edge).
  sparse       far from any surface evidence: an occupancy grid of the compact splats' opacity.

Dense surface-like clusters beyond the wall (side walls, window, clock) survive: they are their own
surface evidence. The rule is "remove sparse junk, keep dense surfaces".
"""
from dataclasses import asdict, dataclass

import numpy as np

from .cleanup_grid import Grid, carve, dilate
from .refine import facets_of

KEEP, ABOVE_TOP, BEHIND_WALL, SEEN_THROUGH, NEEDLE, SPARSE = range(6)
REASONS = {ABOVE_TOP: "aboveWallTop", BEHIND_WALL: "behindWall", SEEN_THROUGH: "seenThrough",
           NEEDLE: "needle", SPARSE: "sparse"}


@dataclass
class CleanupParams:
    """Tunable thresholds (millimetres unless named otherwise)."""
    top_margin_mm: float = 100.0          # above the highest marker
    behind_tol_mm: float = 60.0           # a facet's back side: plywood + the fine alignment's slack
    facet_front_mm: float = 180.0         # holds up to this proud of a facet are "on the facet"
    facet_margin_mm: float = 60.0         # facet outlines are grown by this for every facet test
    side_yaw_deg: float = 45.0            # a facet yawed more than this from the reference is a side
    side_slab_mm: float = 500.0           # behind a side facet's (unbounded) plane this deep is the room
    floor_below_mm: float = 120.0         # below the floor plane (kickboard bottom) by more is removed
    floor_band_mm: float = 450.0          # floor plane .. this above it is the floor / mats band
    cell_mm: float = 50.0                 # occupancy grid cell
    evidence_size_mm: float = 100.0       # surface evidence: compact splats (longest axis, 1 sigma) ...
    dense_alpha: float = 1.0              # ... whose opacities sum to this in a cell make it surface
    surface_reach_cells: int = 2          # splats within this many cells of a surface cell are kept
    carve_min_cameras: int = 3            # a cell this many cameras see through is air ...
    carve_min_cameras_dense: int = 8      # ... or this many when it holds surface evidence itself
    carve_stop_mm: float = 250.0          # rays end this short of the surface they looked at
    carve_targets: int = 30000            # surface cells sampled as ray targets
    needle_len_mm: float = 100.0          # a needle: longest axis (1 sigma) at least this ...
    needle_ratio: float = 6.0             # ... and this many times its middle axis
    hair_len_mm: float = 200.0            # a hair: a needle so long and thin that no surface keeps it
    hair_ratio: float = 10.0
    reach_sigma: float = 2.0              # a splat reaches this many sigmas along its longest axis
    stick_out_mm: float = 120.0           # a surface splat reaching further off its surface is a hair

    def to_dict(self):
        return asdict(self)


def _up(doc):
    up = np.array((doc.get("world") or {}).get("up") or [0, 0, 1], float)
    return up / np.linalg.norm(up)


def _corners(f):
    e = f["e"]
    return np.array([f["o"] + a * f["u"] + b * f["v"] for a in (e["aMin"], e["aMax"]) for b in (e["bMin"], e["bMax"])])


def _horizontal(vec, up):
    h = vec - (vec @ up) * up
    n = np.linalg.norm(h)
    return h / n if n > 1e-9 else None


def wall_frame(doc, p):
    """Facets (front/side flag), up, floor height, wall-top height and the lateral range."""
    facets, up = facets_of(doc), _up(doc)
    if not facets:
        raise ValueError("the geometry has no facets with an extent")
    ref_n = _horizontal(facets[0]["n"], up)
    ref_x = _horizontal(facets[0]["u"], up)
    for f in facets:
        fn = _horizontal(f["n"], up)
        # a roof-like facet (normal ~vertical) or one facing the same way as the reference is "front"
        f["side"] = bool(fn is not None and ref_n is not None
                         and abs(float(fn @ ref_n)) < np.cos(np.radians(p.side_yaw_deg)))
    corners = np.concatenate([_corners(f) for f in facets])
    floor = float((corners @ up).min())
    marks = [c for m in doc.get("markers", []) for c in (m.get("cornersWorldMm") or [])]
    top_ref = np.array(marks, float) @ up if marks else corners @ up
    lateral = corners @ ref_x if ref_x is not None else None
    return {"facets": facets, "up": up, "floor": floor, "top": float(top_ref.max()) + p.top_margin_mm,
            "x": ref_x, "lateral": (float(lateral.min()), float(lateral.max())) if lateral is not None else None}


def _facet_coords(world, f, margin):
    rel = world - f["o"]
    a, b, d = rel @ f["u"], rel @ f["v"], rel @ f["n"]
    e = f["e"]
    inside = (a > e["aMin"] - margin) & (a < e["aMax"] + margin) & (b > e["bMin"] - margin) & (b < e["bMax"] + margin)
    return inside, d


def geometry_rules(world, wf, p, reach=None):
    """(on_surface, verdict) from the facets, the floor and the wall top alone. `reach` (n,3): the
    half-length vector of each splat's longest axis; a splat sticking out of its surface further than
    stick_out_mm (a hair on the wall's edge) is not protected by that surface."""
    verdict = np.zeros(len(world), np.int8)
    on_surface = np.zeros(len(world), bool)
    behind = np.zeros(len(world), bool)
    flat = (lambda n: np.abs(reach @ n) < p.stick_out_mm) if reach is not None else (lambda n: True)
    for f in wf["facets"]:
        inside, d = _facet_coords(world, f, p.facet_margin_mm)
        on_surface |= inside & (d > -p.behind_tol_mm) & (d < p.facet_front_mm) & flat(f["n"])
        if f["side"]:
            # the room's side wall usually lies right behind a side facet and goes on past its outline
            # (a window, a clock): its slab is kept along the whole plane, within the wall's height
            h = world @ wf["up"] - wf["floor"]
            on_surface |= (d > -p.side_slab_mm) & (d < p.facet_front_mm) & (h < wf["top"] - wf["floor"])
        else:
            behind |= inside & (d <= -p.behind_tol_mm)
    h = world @ wf["up"] - wf["floor"]
    on_surface |= (h > -p.floor_below_mm) & (h < p.floor_band_mm) & flat(wf["up"])
    above = h + wf["floor"] > wf["top"]
    if wf["lateral"] is not None:
        s = world @ wf["x"]
        above &= (s > wf["lateral"][0]) & (s < wf["lateral"][1])
    verdict[~on_surface & above] = ABOVE_TOP
    verdict[~on_surface & (verdict == KEEP) & (behind | (h <= -p.floor_below_mm))] = BEHIND_WALL
    return on_surface, verdict


def splat_shape(scale_mm):
    s = np.sort(scale_mm, axis=1)
    return s[:, 2], s[:, 2] / np.maximum(s[:, 1], 1e-3)


def longest_axis(quat_wxyz, scale, linear):
    """Unit direction (after the 3x3 `linear` map, e.g. splat -> world) of each splat's longest axis."""
    w, x, y, z = (quat_wxyz[:, i] for i in range(4))
    k = np.argmax(scale, axis=1)
    # column k of the rotation matrix of each unit quaternion
    cols = np.stack([
        np.stack([1 - 2 * (y * y + z * z), 2 * (x * y + w * z), 2 * (x * z - w * y)], 1),
        np.stack([2 * (x * y - w * z), 1 - 2 * (x * x + z * z), 2 * (y * z + w * x)], 1),
        np.stack([2 * (x * z + w * y), 2 * (y * z - w * x), 1 - 2 * (x * x + y * y)], 1)])
    axis = cols[k, np.arange(len(k))] @ np.asarray(linear, float).T
    return axis / np.maximum(np.linalg.norm(axis, axis=1, keepdims=True), 1e-12)


def classify(world, scale_mm, alpha, doc, cameras=None, params=None, axis=None):
    """Per-splat verdict (KEEP or a removal reason) and the report {rule: count, ...}.

    world: (n,3) splat centres in the geometry world (mm); scale_mm: (n,3) axis lengths (1 sigma, mm);
    alpha 0..1; cameras: (k,3) camera centres in the world (optional: no carving without them);
    axis: (n,3) unit direction of each splat's longest axis in the world (optional: without it a hair
    rooted on a surface counts as that surface).
    """
    p = params or CleanupParams()
    wf = wall_frame(doc, p)
    longest, ratio = splat_shape(scale_mm)
    reach = None if axis is None else axis * (p.reach_sigma * longest)[:, None]
    on_surface, verdict = geometry_rules(world, wf, p, reach)
    live = verdict == KEEP
    lo = world[live].min(0) - p.cell_mm if live.any() else world.min(0)
    hi = world[live].max(0) + p.cell_mm if live.any() else world.max(0) + 1
    grid = Grid(lo, hi, p.cell_mm)
    evidence = live & (longest <= p.evidence_size_mm)
    dense = grid.counts(world[evidence], alpha[evidence]) >= p.dense_alpha
    near = dilate(dense, p.surface_reach_cells)
    free = ~on_surface & live
    carved_cells = 0
    if cameras is not None and len(cameras) and dense.any():
        targets = grid.centres(dense)
        if len(targets) > p.carve_targets:
            targets = targets[np.random.default_rng(0).choice(len(targets), p.carve_targets, replace=False)]
        seen = carve(grid, cameras, targets, p.carve_stop_mm)
        # next to evidence (not in it) a faint splat is more likely the surface's own fringe (a window's
        # glass) than air: only cells clear of evidence, or evidence many cameras look through, go
        air = np.where(dense, seen >= p.carve_min_cameras_dense, (seen >= p.carve_min_cameras) & ~near)
        carved_cells = int(air.sum())
        verdict[free & grid.lookup(air, world)] = SEEN_THROUGH
    free &= verdict == KEEP
    by_surface = grid.lookup(near, world)
    hair = (verdict == KEEP) & (longest >= p.hair_len_mm) & (ratio >= p.hair_ratio)
    needle = free & ~by_surface & (longest >= p.needle_len_mm) & (ratio >= p.needle_ratio)
    verdict[hair | needle] = NEEDLE
    free &= verdict == KEEP
    verdict[free & ~by_surface] = SPARSE
    report = {"params": p.to_dict(), "splats": int(len(world)), "kept": int((verdict == KEEP).sum()),
              "removed": {name: int((verdict == code).sum()) for code, name in REASONS.items()},
              "floorMm": round(wf["floor"], 1), "wallTopMm": round(wf["top"], 1),
              "surfaceCells": int(dense.sum()), "carvedCells": carved_cells,
              "cameras": 0 if cameras is None else int(len(cameras))}
    return verdict, report
