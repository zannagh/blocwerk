"""Nearly coplanar facets: a moved marker joins the NEAREST one, and a stray member cannot stretch an extent.

Layout after The Attic (2026-09-30): the main wall (facet "0", markers up to x = 5.0 m) and the "leftover bit"
(facet "2", x = 5.1..5.6 m) lie in one plane 13 mm apart; marker 39 is stuck among the main-wall markers at x = 4.1 m.
"""
from types import SimpleNamespace

import numpy as np

from wallgeometry.export import EXTENT_MARGIN_MM, _facet, _markers
from wallgeometry.extents import stray_markers
from wallgeometry.facets import DEFAULTS, _Assigner

SIZE = 125.0
MAIN = {0: (60, 60), 4: (1220, 40), 6: (3680, 40), 1: (4970, 60), 2: (4990, 2170), 7: (3640, 2200), 3: (100, 2180)}
LEFTOVER = {22: (5100, 50), 23: (5620, 50), 25: (5130, 1580), 24: (5630, 1580)}
STUCK = (4120, 1880)


def _corners(c, z=0.0):
    h = SIZE / 2
    return np.array([[c[0] - h, c[1] - h, z], [c[0] + h, c[1] - h, z], [c[0] + h, c[1] + h, z],
                     [c[0] - h, c[1] + h, z]])


def _world(stuck_z=10.0):
    mw = {m: _corners(c) for m, c in MAIN.items()}
    mw.update({m: _corners(c, 13.0) for m, c in LEFTOVER.items()})
    mw[39] = _corners(STUCK, stuck_z)
    return mw


def _assigner(facets):
    a = _Assigner.__new__(_Assigner)
    a.p, a.suspect, a.log = dict(DEFAULTS), set(), []
    a.mw = _world()
    a.nm = {m: np.array([0, 0, 1.0]) for m in a.mw}
    a.facets = facets
    a.declared_fids = list(facets)
    return a


def test_a_marker_fitting_two_coplanar_facets_joins_the_one_whose_markers_are_nearest():
    a = _assigner({"0": (0, sorted(MAIN)), "2": (2, sorted(LEFTOVER))})
    f, ang, off = a.best_host([39], exclude=set())
    # the leftover bit's plane fits better (3 mm off vs 10 mm) but its markers are 1 m away, the main wall's 0.6 m
    assert f == "0" and ang < 1 and off < DEFAULTS["mergeMm"]
    choice = next(d for d in a.log if d["kind"] == "hostChoice")
    assert choice["intoFacet"] == "0" and set(choice["candidates"]) == {"0", "2"}
    assert choice["candidates"]["0"]["nearestMarkerMm"] < choice["candidates"]["2"]["nearestMarkerMm"]


def test_a_single_fitting_facet_is_chosen_without_a_choice_record():
    a = _assigner({"2": (2, sorted(LEFTOVER))})
    assert a.best_host([39], exclude=set())[0] == "2"
    assert not a.log


def test_no_host_when_the_marker_is_off_every_plane():
    a = _assigner({"0": (0, sorted(MAIN)), "2": (2, sorted(LEFTOVER))})
    a.mw[39] = _corners(STUCK, 200.0)
    assert a.best_host([39], exclude=set()) is None


def _raw(members, mw, normals=None):
    out = {}
    for fid, ms in members.items():
        n = (normals or {}).get(fid, np.array([0, 0, 1.0]))
        u = np.array([1.0, 0, 0])
        v = np.cross(n, u)
        o = mw[ms[0]].mean(0)
        out[fid] = {"origin_w": o, "u": u, "v": v, "normal": n,
                    "ab": {m: np.stack([(mw[m] - o) @ u, (mw[m] - o) @ v], 1) for m in ms}}
    return out


def test_a_member_inside_a_coplanar_facets_region_is_left_out_of_the_extent_only():
    mw = _world()
    raw = _raw({"0": sorted(MAIN), "2": sorted(LEFTOVER) + [39]}, mw)
    stray = stray_markers(raw, mw, EXTENT_MARGIN_MM)
    assert stray == {"2": [39]}
    full, kept = _facet(raw["2"], []), _facet(raw["2"], stray["2"])
    assert np.allclose(full["origin"], kept["origin"])  # frame and plane coordinates unchanged
    assert 39 in kept["ab"]
    width_full = full["extent"]["aMax"] - full["extent"]["aMin"]
    width_kept = kept["extent"]["aMax"] - kept["extent"]["aMin"]
    assert width_full > 1500 and width_kept < 800
    # the kept extent no longer reaches over the main wall to marker 39 (x = 4.1 m)
    assert (kept["origin"] + kept["extent"]["aMin"] * kept["u"])[0] > 4900


def test_a_far_member_outside_every_other_region_keeps_stretching_its_extent():
    mw = _world()
    mw[39] = _corners((7000, 1880), 13.0)  # beyond the leftover bit, on no other facet
    raw = _raw({"0": sorted(MAIN), "2": sorted(LEFTOVER) + [39]}, mw)
    assert stray_markers(raw, mw, EXTENT_MARGIN_MM) == {}


def test_a_facet_at_another_angle_does_not_claim_a_member():
    mw = _world()
    raw = _raw({"0": sorted(MAIN), "2": sorted(LEFTOVER) + [39]}, mw,
               normals={"0": np.array([0, np.sin(0.5), np.cos(0.5)])})  # ~29 deg off
    assert stray_markers(raw, mw, EXTENT_MARGIN_MM) == {}


def test_a_facet_always_keeps_one_marker_in_its_extent():
    mw = _world()
    raw = _raw({"0": sorted(MAIN), "2": [39]}, mw)
    assert stray_markers(raw, mw, EXTENT_MARGIN_MM) == {}


def test_the_document_flags_a_marker_left_out_of_its_extent():
    # the flag lets occlusion, coverage and the 3D view leave it out of the seam-side vote (docs/geometry-kernel.md)
    ab = {m: np.zeros((4, 2)) for m in (22, 39)}
    req = SimpleNamespace(nominal_segment=lambda m: 2, role_of=lambda m: None, marker_size=lambda m: SIZE, unplanned=set())
    sol = {"world": {"facets": {"2": {"ab": ab}}, "corners": {m: np.zeros((4, 3)) for m in ab}, "stray": {"2": [39]}},
           "req": req, "members": {"2": [22, 39]}, "obs": [], "facet_segment": {"2": 2}, "downweighted": {}}
    recs = {r["id"]: r for r in _markers(sol, {}, {})}
    assert recs[39].get("extentExcluded") is True and "extentExcluded" not in recs[22]
