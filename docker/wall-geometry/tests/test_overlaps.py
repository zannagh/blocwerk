"""Nearly coplanar facets whose extents overlap in-plane are clipped apart at the midline of their marker clusters."""
import numpy as np

from wallgeometry.export import EXTENT_MARGIN_MM, _facet
from wallgeometry.overlaps import clip_overlaps

SIZE = 125.0
LEFT = {0: (60, 60), 1: (1000, 60), 2: (1000, 1500), 3: (60, 1500)}
RIGHT = {10: (1130, 80), 11: (1700, 80), 12: (1700, 1400), 13: (1130, 1400)}


def _corners(c, z=0.0):
    h = SIZE / 2
    return np.array([[c[0] - h, c[1] - h, z], [c[0] + h, c[1] - h, z], [c[0] + h, c[1] + h, z],
                     [c[0] - h, c[1] + h, z]])


def _facets(right_z=0.0, right_tilt=0.0, flip_right=False, right=RIGHT):
    mw = {m: _corners(c) for m, c in LEFT.items()}
    mw.update({m: _corners(c, right_z) for m, c in right.items()})
    t = np.radians(right_tilt)
    rot = np.array([[np.cos(t), 0, np.sin(t)], [0, 1, 0], [-np.sin(t), 0, np.cos(t)]])
    pivot = np.array([1130.0, 0, right_z])
    for m in right:
        mw[m] = (mw[m] - pivot) @ rot.T + pivot
    out = {}
    for fid, ms, r in (("0", sorted(LEFT), np.eye(3)), ("2", sorted(right), rot)):
        n = r @ np.array([0, 0, 1.0])
        u = r @ np.array([1.0, 0, 0]) * (-1 if fid == "2" and flip_right else 1)
        v = np.cross(n, u)
        o = mw[ms[0]].mean(0)
        raw = {"origin_w": o, "u": u, "v": v, "normal": n,
               "ab": {m: np.stack([(mw[m] - o) @ u, (mw[m] - o) @ v], 1) for m in ms}}
        out[fid] = _facet(raw, [])
    return out, {"0": sorted(LEFT), "2": sorted(right)}


def _x_range(f):
    e = f["extent"]
    xs = [(f["origin"] + a * f["u"] + b * f["v"])[0] for a in (e["aMin"], e["aMax"]) for b in (e["bMin"], e["bMax"])]
    return min(xs), max(xs)


def test_side_by_side_coplanar_facets_are_clipped_at_the_midline_between_their_markers():
    facets, core = _facets(right_z=15.0)
    assert _x_range(facets["0"])[1] > _x_range(facets["2"])[0]  # the margins reach over each other
    [rec] = clip_overlaps(facets, core)
    mid = (1000 + SIZE / 2 + 1130 - SIZE / 2) / 2
    assert rec["facets"] == ["0", "2"] and rec["axis"] == "a"
    assert abs(rec["clippedAtWorldMm"][0] - mid) < 1e-6
    assert abs(_x_range(facets["0"])[1] - mid) < 1e-6
    assert abs(_x_range(facets["2"])[0] - mid) < 1e-6
    assert _x_range(facets["0"])[0] == 60 - SIZE / 2 - EXTENT_MARGIN_MM  # the far sides keep their margin


def test_a_facet_whose_axis_points_the_other_way_is_clipped_on_the_right_side():
    facets, core = _facets(flip_right=True)
    assert clip_overlaps(facets, core)
    mid = (1000 + SIZE / 2 + 1130 - SIZE / 2) / 2
    assert abs(_x_range(facets["0"])[1] - mid) < 1e-6
    assert abs(_x_range(facets["2"])[0] - mid) < 1e-6
    assert _x_range(facets["2"])[1] > 1700 + SIZE / 2


def test_a_parallel_panel_in_front_of_the_wall_is_not_clipped():
    facets, core = _facets(right_z=120.0)
    before = {f: dict(facets[f]["extent"]) for f in facets}
    assert clip_overlaps(facets, core) == []
    assert all(facets[f]["extent"] == before[f] for f in facets)


def test_a_facet_at_another_angle_is_not_clipped():
    facets, core = _facets(right_tilt=12.0)
    assert clip_overlaps(facets, core) == []


def test_interleaved_marker_clusters_are_left_alone():
    right = {10: (900, 800), 11: (1700, 80), 12: (1700, 1400), 13: (1130, 1400)}
    facets, core = _facets(right=right)
    assert clip_overlaps(facets, core) == []


def test_facets_that_do_not_overlap_are_untouched():
    far = {m: (x + 500, y) for m, (x, y) in RIGHT.items()}
    facets, core = _facets(right=far)
    assert clip_overlaps(facets, core) == []


def test_an_l_shaped_seam_whose_marker_boxes_interleave_slightly_is_still_cut_at_the_midline():
    # The Attic: the main wall's top-right marker reaches 15 mm past the left edge of the lower "leftover bit"
    main = {0: (60, 60), 1: (4970, 60), 2: (4990, 2170), 3: (100, 2180)}
    leftover = {22: (5100, 50), 23: (5620, 50), 25: (5130, 1580), 24: (5630, 1580)}
    mw = {m: _corners(c) for m, c in main.items()}
    mw.update({m: _corners(c, 13.0) for m, c in leftover.items()})
    facets = {}
    for fid, ms in (("0", sorted(main)), ("2", sorted(leftover))):
        n, u = np.array([0, 0, 1.0]), np.array([1.0, 0, 0])
        o = mw[ms[0]].mean(0)
        facets[fid] = _facet({"origin_w": o, "u": u, "v": np.cross(n, u), "normal": n,
                              "ab": {m: np.stack([(mw[m] - o) @ u, (mw[m] - o) @ np.cross(n, u)], 1) for m in ms}}, [])
    [rec] = clip_overlaps(facets, {"0": sorted(main), "2": sorted(leftover)})
    seam = (4990 + SIZE / 2 + 5100 - SIZE / 2) / 2
    assert rec["axis"] == "a"
    assert abs(_x_range(facets["0"])[1] - seam) < 1e-6 and abs(_x_range(facets["2"])[0] - seam) < 1e-6


def test_facets_at_a_fold_too_shallow_for_a_seam_are_clipped_at_the_midline():
    # 7 deg apart: above mergeDeg (5), below the seam threshold (~9.8 deg), so occlusion and the 3D view cut nothing
    # there; without the clip their margins would overlap uncut (review F10)
    facets, core = _facets(right_tilt=7.0)
    assert _x_range(facets["0"])[1] > _x_range(facets["2"])[0]
    [rec] = clip_overlaps(facets, core)
    assert rec["facets"] == ["0", "2"] and "too shallow for a seam" in rec["reason"]
    mid = (1000 + SIZE / 2 + 1130 - SIZE / 2) / 2  # in the left facet's plane; the right one is turned 7 deg
    assert abs(_x_range(facets["0"])[1] - mid) < 1.0
    assert _x_range(facets["2"])[0] >= _x_range(facets["0"])[1] - 1e-6


def test_a_shallow_fold_that_does_not_meet_at_the_midline_is_a_step_and_not_clipped():
    facets, core = _facets(right_z=120.0, right_tilt=7.0)
    assert clip_overlaps(facets, core) == []
