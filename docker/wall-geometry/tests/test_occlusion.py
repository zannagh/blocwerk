"""Facet occlusion in the texture photo choice: only a facet's real region between camera and point hides it.

Regression: a far perpendicular facet whose plane merely passes behind the main wall used to blank most
of the wall ("behind another facet's plane and inside its extent rectangle", no ray test).
"""
import numpy as np
from synthetic import _look_at

from wallgeometry import occlusion
from wallgeometry import textures as tx

W, H, F = 2000, 1500, 1000.0


def _facet(fid, origin, u, v, ext):
    u, v = np.array(u, float), np.array(v, float)
    return {"id": fid, "origin": list(origin), "u": u.tolist(), "v": v.tolist(), "normal": np.cross(u, v).tolist(),
            "extentMm": dict(zip(("aMin", "aMax", "bMin", "bMax"), map(float, ext)))}


def _cam(centre, target):
    R = _look_at(np.array(centre, float), np.array(target, float))
    K = np.array([[F, 0, (W - 1) / 2], [0, F, (H - 1) / 2], [0, 0, 1]])
    return tx._cam({"K": K.ravel().tolist(), "dist": [0] * 5, "R": R.ravel().tolist(),
                    "t": (-R @ np.array(centre, float)).tolist(), "width": W, "height": H})


# main wall: vertical plane y = 0, x 0..3000, z 0..2000, facing the cameras at -y
WALL = _facet("0", (0, 0, 0), (1, 0, 0), (0, 0, 1), (0, 3000, 0, 2000))
CAMS = {"A": _cam((500, -3000, 1000), (1500, 0, 1000)), "B": _cam((2500, -3000, 1000), (1500, 0, 1000))}


def _scores(others, markers=()):
    facets = [WALL] + others
    p = {**tx.DEFAULTS, "mmPerPx": 20.0, "extraMarginMm": 0.0, "imageMarginPx": 0}
    g = tx._grid(WALL, p)
    names = sorted(CAMS)
    X, views = tx._cell_views(WALL, g, CAMS, names, p, occlusion.occluders(facets, list(markers)))
    return X, dict(zip(names, views.full("S", len(names))))


def _at(X, S, x, z):
    k = np.argmin(np.hypot(X[..., 0] - x, X[..., 2] - z))
    return S.reshape(-1)[k]


def test_far_perpendicular_facet_does_not_hide_the_wall():
    # a vertical panel at x = 6000 facing +x: the whole wall is "behind" its plane and inside its extent
    # in its own frame, but no camera looks through it
    far = _facet("1", (6000, 0, 0), (0, -1, 0), (0, 0, 1), (0, 2000, 0, 2000))
    X, S = _scores([far])
    seen = (S["A"] > 0) | (S["B"] > 0)
    assert seen.mean() > 0.99


def test_close_fin_hides_only_what_it_blocks_per_camera():
    # a fin standing out of the wall at x = 2000 (1 m deep, towards the cameras)
    fin = _facet("1", (2000, 0, 0), (0, -1, 0), (0, 0, 1), (0, 1000, 0, 2000))
    X, S = _scores([fin])
    # camera A (x = 500) cannot see behind the fin (wall x 2000..2750) but sees past it
    assert _at(X, S["A"], 2400, 1000) == 0
    assert _at(X, S["A"], 1000, 1000) > 0 and _at(X, S["A"], 2950, 1000) > 0
    # camera B (x = 2500) is blocked left of the fin (x 1750..2000) but sees the part A cannot
    assert _at(X, S["B"], 1900, 1000) == 0
    assert _at(X, S["B"], 2400, 1000) > 0 and _at(X, S["B"], 1000, 1000) > 0
    # every wall spot is still seen by some photo
    assert ((S["A"] > 0) | (S["B"] > 0)).mean() > 0.97


def test_triangle_region_keeps_its_marker_side_only():
    # the fin's rectangle reaches 1 m through the wall plane; its markers are all in front, so the seam
    # with the wall cuts the rectangle there and the part behind the wall blocks nothing
    fin = _facet("1", (2000, 0, 0), (0, -1, 0), (0, 0, 1), (-1000, 1000, 0, 2000))
    marker = {"facet": "1", "cornersPlaneMm": [[300, 600], [500, 600], [500, 400], [300, 400]]}
    occ = {o.id: o for o in occlusion.occluders([WALL, fin], [marker])}["1"]
    assert occ.contains(np.array([500.0]), np.array([1000.0]))[0]
    assert not occ.contains(np.array([-500.0]), np.array([1000.0]))[0]
    # without markers there is no evidence for a side: the full rectangle is kept
    assert occlusion.occluders([WALL, fin], [])[1].halfplanes == []
