"""Regression tests on The Attic's REAL data (test/real-data/attic/, shared with the C# tests; no photos, no users).

The active model of 2026-09-30 (8 facets, 49 plan markers, 356 solved cameras) with its marker corners is rebuilt into
the solver's facet frames and pushed through the kernel's rules, pinning the bugs that only real data showed:
  * a stray marker (39, among the main-wall markers) stretching the "leftover bit" over the main wall (extent inheritance),
  * coplanar facets overlapping in-plane instead of meeting at the midline between their marker clusters,
  * a misplaced marker joining the coplanar facet with the nearest markers, not the best-fitting plane,
  * duplicate facets / markers after a plan change,
  * a far facet (the board at x = 5.1 m, the corner piece) hiding the main wall from the cameras in the texture choice.
"""
import json
import os
from pathlib import Path

import numpy as np
import pytest

from wallgeometry import occlusion
from wallgeometry import textures as tx
from wallgeometry.export import EXTENT_MARGIN_MM, _facet
from wallgeometry.extents import stray_markers
from wallgeometry.facets import DEFAULTS, _Assigner
from wallgeometry.overlaps import clip_overlaps


def _fixture_dir():
    here = Path(__file__).resolve().parent
    candidates = [os.environ.get("REAL_DATA_DIR"), here / "real-data" / "attic"]  # the test image copies it here
    candidates += [p / "test" / "real-data" / "attic" for p in here.parents]  # the repository
    for c in candidates:
        if c and (Path(c) / "model.json").is_file():
            return Path(c)
    raise FileNotFoundError(f"no real-data fixtures in {[str(c) for c in candidates if c]}")


DIR = _fixture_dir()
MODEL = json.loads((DIR / "model.json").read_text())
PLAN = json.loads((DIR / "plan.json").read_text())
FACETS = [f for s in MODEL["segments"] for f in s["facets"]]
BY_ID = {f["id"]: f for f in FACETS}
MARKERS = MODEL["markers"]
CORNERS = {m["id"]: np.array(m["cornersWorldMm"], float) for m in MARKERS}


def _raw(members=None):
    """The solver's raw facets {fid: {origin_w, u, v, normal, ab}} rebuilt from the model (optionally regrouped)."""
    members = members or {f["id"]: [m["id"] for m in MARKERS if m["facet"] == f["id"]] for f in FACETS}
    ab = {m["id"]: np.array(m["cornersPlaneMm"], float) for m in MARKERS}
    return {fid: {"origin_w": np.array(BY_ID[fid]["origin"], float), "u": np.array(BY_ID[fid]["u"], float),
                  "v": np.array(BY_ID[fid]["v"], float), "normal": np.array(BY_ID[fid]["normal"], float),
                  "ab": {m: ab[m] for m in sorted(ms)}} for fid, ms in members.items()}


def _world_extent(f, e):
    """The 4 world corners of facet f's extent e."""
    o, u, v = (np.array(f[k], float) for k in ("origin", "u", "v"))
    return np.array([o + a * u + b * v for a in (e["aMin"], e["aMax"]) for b in (e["bMin"], e["bMax"])])


def _x_range(f, e):
    xs = _world_extent(f, e)[:, 0]
    return xs.min(), xs.max()


def test_the_model_has_one_facet_per_plan_segment_and_every_plan_marker_once():
    assert len(FACETS) == len(PLAN["segments"]) == 8
    assert len({f["id"] for f in FACETS}) == len(FACETS)
    assert sorted(m["id"] for m in MARKERS) == sorted(m["id"] for m in PLAN["markers"])
    assert len({m["id"] for m in MARKERS}) == len(MARKERS)
    assert {m["facet"] for m in MARKERS} == set(BY_ID)  # no empty facet, no marker on a missing one


def test_no_two_facets_are_duplicates():
    for a, b in ((x, y) for i, x in enumerate(FACETS) for y in FACETS[i + 1:]):
        same_plane = abs(float(np.dot(a["normal"], b["normal"]))) > 0.9999
        same_spot = float(np.abs(np.array(a["origin"]) - np.array(b["origin"])).max()) < 1.0
        assert not (same_plane and same_spot), f"{a['id']} and {b['id']} are duplicates"
        assert set(a["markerIds"]).isdisjoint(b["markerIds"])


def test_rebuilding_the_extents_from_the_markers_reproduces_the_exported_model():
    facets = {fid: _facet(r, []) for fid, r in _raw().items()}
    core = {fid: sorted(r["ab"]) for fid, r in _raw().items()}
    [rec] = clip_overlaps(facets, core)  # exactly one coplanar overlap on The Attic: main wall x leftover bit
    assert rec["facets"] == ["0", "2"] and rec["axis"] == "a"
    for fid, f in facets.items():
        mine = _world_extent(f, f["extent"])
        theirs = _world_extent(BY_ID[fid], BY_ID[fid]["extentMm"])
        assert float(np.abs(np.sort(mine, axis=0) - np.sort(theirs, axis=0)).max()) < 5.0, fid


def test_the_main_wall_and_the_leftover_bit_meet_at_the_midline_between_their_marker_clusters():
    facets = {fid: _facet(r, []) for fid, r in _raw().items()}
    core = {fid: sorted(r["ab"]) for fid, r in _raw().items()}
    assert abs(_x_range(facets["0"], facets["0"]["extent"])[1] - _x_range(facets["2"], facets["2"]["extent"])[0]) > 100  # overlap
    clip_overlaps(facets, core)
    main_right = max(CORNERS[m][:, 0].max() for m in core["0"])
    leftover_left = min(CORNERS[m][:, 0].min() for m in core["2"])
    mid = (main_right + leftover_left) / 2  # the clusters interleave by ~15 mm
    assert abs(_x_range(facets["0"], facets["0"]["extent"])[1] - mid) < 1.0
    assert abs(_x_range(facets["2"], facets["2"]["extent"])[0] - mid) < 1.0
    assert clip_overlaps(facets, core) == []  # clipped apart for good: a second pass finds nothing to do
    # the exported model has them where we put them (its overlapClipped check records the same pair)
    [clipped] = MODEL["quality"]["checks"]["overlapClipped"]
    assert clipped["facets"] == ["0", "2"] and abs(clipped["clippedAtWorldMm"][0] - mid) < 1.0


def test_no_marker_stretches_an_extent_over_its_coplanar_neighbour():
    raw = _raw()
    assert stray_markers(raw, CORNERS, EXTENT_MARGIN_MM) == {}


def test_marker_39_misplaced_on_the_leftover_bit_would_stretch_it_over_the_main_wall_but_is_left_out_of_the_extent():
    members = {f["id"]: [m["id"] for m in MARKERS if m["facet"] == f["id"]] for f in FACETS}
    members["0"].remove(39)
    members["2"].append(39)
    raw = _raw(members)
    plain = _facet(raw["2"], [])  # the old behaviour: the extent grows to the stuck marker
    guarded = _facet(raw["2"], [39])
    assert stray_markers(raw, CORNERS, EXTENT_MARGIN_MM) == {"2": [39]}
    # the stuck marker alone stretches the 0.7 m wide leftover bit to 3.3 m, over the main wall; the guard keeps it at 0.7 m
    assert plain["extent"]["aMax"] - plain["extent"]["aMin"] > 3000
    assert guarded["extent"]["aMax"] - guarded["extent"]["aMin"] < 800
    assert guarded["extent"]["bMax"] < plain["extent"]["bMax"]


def test_a_marker_fitting_both_coplanar_facets_joins_the_one_with_the_nearest_markers():
    members = {f["id"]: [m["id"] for m in MARKERS if m["facet"] == f["id"]] for f in FACETS}
    a = _Assigner.__new__(_Assigner)
    a.p, a.suspect, a.log = dict(DEFAULTS), set(), []
    a.mw = CORNERS
    a.nm = {m["id"]: np.array(BY_ID[m["facet"]]["normal"], float) for m in MARKERS}
    a.facets = {"0": (0, members["0"]), "2": (2, members["2"])}
    a.declared_fids = ["0", "2"]
    f, ang, off = a.best_host([39], exclude=set())
    assert f == "0" and ang < 1.0 and off < DEFAULTS["mergeMm"]
    [choice] = [d for d in a.log if d["kind"] == "hostChoice"]
    assert set(choice["candidates"]) == {"0", "2"}
    near = {k: v["nearestMarkerMm"] for k, v in choice["candidates"].items()}
    assert near["0"] < near["2"] and 800 < near["2"] < 2000


# ---- texture photo choice: only a facet's real region hides the wall ---------------------------------------

CAMS = {c["image"]: tx._cam(c) for c in MODEL["cameras"]}
NAMES = sorted(CAMS)
PARAMS = {**tx.DEFAULTS, "mmPerPx": 25.0, "labelCellPx": 4}  # 100 mm cells


@pytest.fixture(scope="module")
def main_wall_views():
    """Per photo, the main wall's cell scores without occlusion, and with each other facet blocking alone."""
    f = BY_ID["0"]
    g = tx._grid(f, PARAMS)
    occs = occlusion.occluders(FACETS, MARKERS)
    _, free = tx._cell_views(f, g, CAMS, NAMES, PARAMS, [])
    base = free.full("S", len(NAMES)) > 0
    alone = {}
    for o in occs:
        if o.id != "0":
            _, v = tx._cell_views(f, g, CAMS, NAMES, PARAMS, [o])
            alone[o.id] = int((base & (v.full("S", len(NAMES)) <= 0)).sum())
    _, v_all = tx._cell_views(f, g, CAMS, NAMES, PARAMS, occs)
    return base, v_all.full("S", len(NAMES)) > 0, alone


def test_far_facets_hide_none_of_the_main_wall_in_the_texture_choice(main_wall_views):
    _, _, alone = main_wall_views
    # the leftover bit (coplanar, clipped at the midline), the far board (x = 5.1 m) and the corner piece never stand
    # between a photo and a main-wall cell; "behind another facet's plane and inside its extent" used to hide most of it
    assert (alone["2"], alone["4"], alone["6"]) == (0, 0, 0)


def test_the_side_wall_the_kickboard_and_the_closing_pieces_hide_a_minority_of_the_main_wall_views(main_wall_views):
    base, with_occ, alone = main_wall_views
    lost = int((base & ~with_occ).sum())
    assert all(alone[k] > 0 for k in ("1", "3", "5a", "7a")), alone
    assert 0.05 < lost / base.sum() < 0.20
    seen_cells = int(with_occ.any(axis=0).sum())
    assert seen_cells > 0.9 * int(base.any(axis=0).sum())  # nearly every cell the cameras see stays seen
