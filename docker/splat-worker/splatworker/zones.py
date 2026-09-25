"""Where the splat's budget goes and what is exported: three zones in the wall-geometry world (mm; x right
along the reference facet, y into the wall, z = gravity up), from the solved facets and markers.

  WALL (a)      the wall slab: each facet's outline grown by slab_margin_mm, from slab_back_mm behind its
                plane to slab_front_mm in front of it (holds and volumes stick out up to ~16 cm).
  SURROUND (b)  one axis-aligned box around the wall: the facets' and markers' bounding box grown by
                box_margin_mm sideways, into the room and above the top, down to box_floor_mm below the
                lowest facet corner (the floor / mats band). Minus the slab, and minus the AIR: what lies in
                front of a facet (within its outline, past the slab) above the floor band: a post holding the
                overhang, a rope, a person. That is OUTSIDE, so it gets no budget and is not exported: the
                wall behind it shows instead of a smeared, half-seen post. A point behind another facet's
                slab (within that one's outline) is never air: a side wall's prism in front of it runs
                through the main wall, and a plain wall's surface is often fitted a little behind its plane.
  OUTSIDE (c)   everything else: the room, poles and lamps in front of the box, the ceiling.

Training (gsplat_zones.py): MCMC's relocation and growth only sample WALL and SURROUND, SURROUND up to
surround_share of the cap; OUTSIDE keeps its sparse init (occluders stay explained, so they are not painted
into the wall) and is never grown. Export (cut_mask): WALL + SURROUND only, a hard box cut that also drops
every splat whose 3-sigma ellipsoid pokes out of the box; in the SURROUND no needles, floor spikes, lone
floaters or faint haze; the WALL keeps its surface (removing its large flat splats thins it, the old
clean-up's darkening) minus the needles that are not it (behind the facet, off a flat facet, faint, past
the outline).

numpy only; classify() also takes torch tensors (the trainer's copy on the GPU) through `xp`.
"""
from dataclasses import asdict, dataclass

import numpy as np

WALL, SURROUND, OUTSIDE = 0, 1, 2
ZONE_NAMES = ("wall", "surround", "outside")


@dataclass
class ZoneParams:
    slab_margin_mm: float = 100.0  # facet outlines grown by this (the alignment's slack, the wall's edges)
    slab_back_mm: float = 60.0  # behind the facet plane (plywood + the alignment's slack)
    slab_front_mm: float = 250.0  # in front of it: holds and volumes
    box_margin_mm: float = 400.0  # the surroundings: this far past the wall sideways, into the room, above
    box_floor_mm: float = 150.0  # below the lowest facet corner (the floor / mat surface lies above)
    floor_band_mm: float = 450.0  # the floor / mats: up to this above the lowest facet corner
    air_outside: bool = True  # the air in front of a facet (past the slab, above the floor band) is OUTSIDE
    surround_share: float = 0.10  # at most this share of the splat cap trains in the surroundings
    fray_mm: float = 0.0  # export: a splat whose fray_sigma ellipsoid reaches this far out of the box goes (any zone)
    fray_sigma: float = 3.0  # ... 3 sigma: its visible extent, so nothing pokes through the box faces
    needle_len_mm: float = 60.0  # export: a surroundings splat this long (1 sigma) ...
    needle_ratio: float = 5.0  # ... and this many times its middle axis is a needle
    spike_len_mm: float = 20.0  # export: in the floor band (floor, mats: flat) a surroundings splat this long ...
    spike_ratio: float = 3.0  # ... and this elongated is a spike (the streaks rimming the floor)
    lonely_cell_mm: float = 50.0  # export: above the floor band a surroundings splat with less opacity than ...
    lonely_alpha: float = 2.0  # ... this summed over the 3x3x3 cells around it is a floater (surfaces are dense)
    haze_alpha: float = 0.08  # export: a surroundings splat fainter than this ...
    haze_len_mm: float = 40.0  # ... and at least this long is haze
    wall_needle_len_mm: float = 60.0  # export: a WALL splat this long (1 sigma) ...
    wall_needle_ratio: float = 5.0  # ... and this elongated is a needle; it goes when it is not the surface:
    wall_behind_mm: float = 20.0  # behind its facet's plane by more (hidden from the front, a spike from aside),
    wall_off_cos: float = 0.5  # pointing off a flat facet (|cos(axis, normal)| above this, <= wall_flat_mm proud),
    wall_flat_mm: float = 30.0
    wall_needle_alpha: float = 0.2  # fainter than this, or outside every facet's outline (a hair on an edge)
    min_alpha: float = 0.02  # export: fainter splats are dropped everywhere (as the plain crop does)

    def to_dict(self):
        return asdict(self)


def _facets(doc):
    out = []
    for seg in doc.get("segments", []):
        for f in seg.get("facets", []):
            e = f.get("extentMm")
            if e:
                out.append({"id": str(f["id"]), "o": [float(v) for v in f["origin"]], "u": [float(v) for v in f["u"]],
                            "v": [float(v) for v in f["v"]], "n": [float(v) for v in f["normal"]],
                            "ext": [e["aMin"], e["aMax"], e["bMin"], e["bMax"]]})
    return out


def spec(doc, params=None):
    """The zones of a geometry document (JSON-able: the trainer reads it from zones.json), or None
    when the document has no facet with an extent."""
    p = params or ZoneParams()
    facets = _facets(doc)
    if not facets:
        return None
    pts = []
    for f in facets:
        o, u, v = (np.array(f[k]) for k in ("o", "u", "v"))
        a0, a1, b0, b1 = f["ext"]
        pts += [o + a * u + b * v for a in (a0, a1) for b in (b0, b1)]
    floor = float(np.min(np.array(pts)[:, 2]))
    pts += [c for m in doc.get("markers", []) for c in (m.get("cornersWorldMm") or [])]
    pts = np.array(pts, float)
    lo, hi = pts.min(0) - p.box_margin_mm, pts.max(0) + p.box_margin_mm
    lo[2] = floor - p.box_floor_mm
    return {"version": 1, "facets": facets, "floorMm": round(floor, 1), "boxLo": lo.round(1).tolist(), "boxHi": hi.round(1).tolist(),
            "params": p.to_dict()}


def classify(world, zs, xp=np):
    """Zone per point (WALL / SURROUND / OUTSIDE) of (n, 3) world-mm points; `xp` numpy or torch."""
    p = zs["params"]
    if xp is np:
        t, zone = np.asarray, np.full(len(world), OUTSIDE, np.int8)
    else:
        def t(a):
            return xp.tensor(a, dtype=world.dtype, device=world.device)
        zone = xp.full((len(world),), OUTSIDE, dtype=xp.int8, device=world.device)
    lo, hi = t(zs["boxLo"]), t(zs["boxHi"])
    in_box = ((world >= lo) & (world <= hi)).all(1)
    wall, air, behind = in_box & False, in_box & False, in_box & False
    m = p["slab_margin_mm"]
    for f in zs["facets"]:
        rel = world - t(f["o"])
        a, b, d = rel @ t(f["u"]), rel @ t(f["v"]), rel @ t(f["n"])
        a0, a1, b0, b1 = f["ext"]
        inside = (a > a0 - m) & (a < a1 + m) & (b > b0 - m) & (b < b1 + m)
        wall = wall | (inside & (d > -p["slab_back_mm"]) & (d < p["slab_front_mm"]))
        air = air | (inside & (d >= p["slab_front_mm"]))
        behind = behind | (inside & (d <= -p["slab_back_mm"]))
    if p["air_outside"]:  # in front of the wall, above the floor band, not behind a facet: posts, ropes, people
        air = air & ~behind & (world[:, 2] > zs["floorMm"] + p["floor_band_mm"])
        in_box = in_box & ~air
    zone[in_box] = SURROUND
    zone[wall] = WALL
    return zone


def to_world(xyz, M):
    M = np.asarray(M, float)
    return np.asarray(xyz, float) @ M[:3, :3].T + M[:3, 3]


def counts(zone):
    return {name: int((zone == i).sum()) for i, name in enumerate(ZONE_NAMES)}


def _axes_world(quat_wxyz, lin):
    """(n, 3, 3) the splats' principal axes as columns, in world directions (unit length)."""
    q = quat_wxyz / np.maximum(np.linalg.norm(quat_wxyz, axis=1, keepdims=True), 1e-12)
    w, x, y, z = (q[:, i] for i in range(4))
    R = np.stack([np.stack([1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y)], 1),
                  np.stack([2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x)], 1),
                  np.stack([2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y)], 1)], 1)
    R = np.einsum("ij,njk->nik", np.asarray(lin, float) / np.cbrt(abs(np.linalg.det(lin))), R)
    return R


def _box_out(world, R, scale_mm, lo, hi, sigmas):
    """How far (mm) each splat's `sigmas` ellipsoid reaches out of the box (<= 0: inside)."""
    half = np.sqrt(np.einsum("nik,nk->ni", R ** 2, (sigmas * scale_mm) ** 2))
    return np.maximum(lo - (world - half), (world + half) - hi).max(1)


def _neighbourhood_mass(pts, world, alpha, lo, hi, cell):
    """Summed opacity of `world` splats in the 3x3x3 cells (of `cell` mm) around each of `pts`."""
    shape = np.maximum(np.ceil((hi - lo) / cell).astype(int), 1) + 2  # one empty cell of padding around
    grid = np.zeros(shape)

    def ijk(x):
        return np.clip(((x - lo) // cell).astype(int) + 1, 0, shape - 1)

    np.add.at(grid, tuple(ijk(world).T), alpha)
    for ax in range(3):  # separable 3-cell box sum (the padding keeps np.roll from wrapping mass around)
        grid = np.roll(grid, 1, ax) + grid + np.roll(grid, -1, ax)
    return grid[tuple(ijk(pts).T)]


def _wall_needles(world, axis, alpha, zs):
    """Of WALL needles (world centres, unit long axes): the ones that are not the wall's surface."""
    p = zs["params"]
    m = p["slab_margin_mm"]
    best = np.full(len(world), np.inf)
    d, cos, inside_any = np.zeros(len(world)), np.zeros(len(world)), np.zeros(len(world), bool)
    for f in zs["facets"]:
        o, u, v, n = (np.array(f[k]) for k in ("o", "u", "v", "n"))
        rel = world - o
        a, b, dist = rel @ u, rel @ v, rel @ n
        a0, a1, b0, b1 = f["ext"]
        inside_any |= (a > a0) & (a < a1) & (b > b0) & (b < b1)
        near = (a > a0 - m) & (a < a1 + m) & (b > b0 - m) & (b < b1 + m) & (np.abs(dist) < best)
        best = np.where(near, np.abs(dist), best)
        d = np.where(near, dist, d)
        cos = np.where(near, np.abs(axis @ n), cos)
    off = (d < p["wall_flat_mm"]) & (cos > p["wall_off_cos"])
    return (d < -p["wall_behind_mm"]) | off | (alpha < p["wall_needle_alpha"]) | ~inside_any


def cut_mask(world, scale_mm, alpha, quat_wxyz, lin, zs):
    """Export filter: (keep mask, report). A clean box: every splat whose 3-sigma ellipsoid pokes out of it
    goes. The WALL keeps its surface, minus needles that are not it (behind the facet, sticking off a flat
    facet, faint, or past the outline). The SURROUND drops needles, spikes on the floor, lone floaters above
    it and haze."""
    p = zs["params"]
    zone = classify(world, zs)
    s = np.sort(scale_mm, axis=1)
    longest, ratio = s[:, 2], s[:, 2] / np.maximum(s[:, 1], 1e-6)
    dead = alpha <= p["min_alpha"]  # MCMC's dead splats (never relocated after growth stops): invisible
    live = (zone != OUTSIDE) & ~dead
    idx = np.flatnonzero(live)
    R = _axes_world(quat_wxyz[idx], lin)
    lo, hi = np.array(zs["boxLo"]), np.array(zs["boxHi"])
    fray = np.zeros(len(world), bool)
    fray[idx] = _box_out(world[idx], R, scale_mm[idx], lo, hi, p.get("fray_sigma", 2.0)) > p["fray_mm"]
    sur = live & (zone == SURROUND) & ~fray
    air = world[:, 2] > zs["floorMm"] + p["floor_band_mm"]
    needle = sur & (longest >= p["needle_len_mm"]) & (ratio >= p["needle_ratio"])
    needle |= sur & ~air & (longest >= p.get("spike_len_mm", np.inf)) & (ratio >= p.get("spike_ratio", np.inf))
    haze = sur & ~needle & (alpha < p["haze_alpha"]) & (longest >= p["haze_len_mm"])
    wall_needle = np.zeros(len(world), bool)
    lonely = np.zeros(len(world), bool)
    if p.get("lonely_alpha", 0) > 0:  # a floater: little else around it (surfaces are dense)
        lonely[sur & air] = _neighbourhood_mass(world[sur & air], world[live], alpha[live], lo, hi,
                                                p["lonely_cell_mm"]) < p["lonely_alpha"]
        lonely &= ~needle & ~haze
    cand = live & (zone == WALL) & ~fray & (longest >= p.get("wall_needle_len_mm", np.inf)) \
        & (ratio >= p.get("wall_needle_ratio", np.inf))
    if cand.any():
        pos = np.searchsorted(idx, np.flatnonzero(cand))
        axis = R[pos, :, np.argmax(scale_mm[cand], 1)]
        wall_needle[cand] = _wall_needles(world[cand], axis, alpha[cand], zs)
    keep = live & ~fray & ~needle & ~haze & ~lonely & ~wall_needle
    report = {"zones": counts(zone), "outsideShare": round(float((zone == OUTSIDE).mean()), 4) if len(zone) else 0,
              "removed": {"outside": int((zone == OUTSIDE).sum()), "dead": int((dead & (zone != OUTSIDE)).sum()),
                          "fray": int(fray.sum()), "needle": int(needle.sum()), "haze": int(haze.sum()),
                          "lonely": int(lonely.sum()),
                          "wallNeedle": int(wall_needle.sum())},
              "kept": int(keep.sum())}
    return keep, report


def viewer_box(zs, centre):
    """The SURROUND box in the viewer frame (align.WORLD_TO_VIEWER: an axis permutation, so it stays a box)."""
    from .align import WORLD_TO_VIEWER
    lo, hi = np.array(zs["boxLo"]), np.array(zs["boxHi"])
    box = np.array([[x, y, z] for x in (lo[0], hi[0]) for y in (lo[1], hi[1]) for z in (lo[2], hi[2])])
    v = (WORLD_TO_VIEWER @ (box - centre).T).T
    return [v.min(0).round(4).tolist(), v.max(0).round(4).tolist()]
