"""The radial lens model folds back far outside the field of view; textures must never sample there.

Regression (The Attic, photo p03 on the main wall): with k1 > 0, k2 < 0 (a typical iPhone solve) a wall
point at ~2.5x the corner's normalized radius distorted back INTO the image, mirrored and squeezed, and
won the cell (it looked close and sharp): a streaky smear across the middle of the wall.
"""
import numpy as np
from synthetic import _look_at

from wallgeometry import camera
from wallgeometry import textures as tx

W, H, F = 4032, 3024, 3000.0
K12 = [0.057181, -0.07646, 0.0, 0.0, 0.0]  # p03's solved distortion
WALL = {"id": "0", "origin": [0, 0, 0], "u": [1, 0, 0], "v": [0, 0, 1], "normal": [0, -1, 0],
        "extentMm": {"aMin": -8000.0, "aMax": 8000.0, "bMin": 0.0, "bMax": 2000.0}}


def _cam(dist):
    centre = np.array([0.0, -600.0, 1000.0])
    R = _look_at(centre, np.array([0.0, 0.0, 1000.0]))
    K = np.array([[F, 0, (W - 1) / 2], [0, F, (H - 1) / 2], [0, 0, 1]])
    return tx._cam({"K": K.ravel().tolist(), "dist": dist, "R": R.ravel().tolist(),
                    "t": (-R @ centre).tolist(), "width": W, "height": H})


def _wall_row():
    a = np.linspace(-8000, 8000, 1601)
    return np.stack([a, np.zeros_like(a), np.full_like(a, 1000.0)], -1), a


def test_fold_radius_of_a_folding_and_a_plain_lens():
    r2 = camera.max_valid_radius2(K12[:2] + [0.0])
    assert 1.3 < np.sqrt(r2) < 1.45
    assert camera.max_valid_radius2((0.0, 0.0, 0.0)) == np.inf
    assert camera.max_valid_radius2((0.05, 0.01, 0.0)) == np.inf


def test_points_past_the_fold_are_not_seen():
    cam = _cam(K12)
    X, a = _wall_row()
    # the raw polynomial really lands far-away wall points inside the image (the bug's premise)
    xn = a / 600.0
    r2 = xn * xn
    raw = F * xn * (1 + K12[0] * r2 + K12[1] * r2 * r2) + (W - 1) / 2
    folded = (np.abs(xn) > 2.0) & (raw >= 0) & (raw <= W - 1)
    assert folded.any()
    s = tx._score(cam, WALL, X, 16)
    assert (s[np.abs(xn) > 1.3] == 0).all()
    # the real field of view (corner radius ~0.84) is still seen
    assert (s[np.abs(xn) < 0.6] > 0).all()


def test_render_does_not_paint_from_past_the_fold():
    X, a = _wall_row()
    cam = _cam(K12)
    px, z, _ = tx.project(cam, X)
    inside = (px[:, 0] >= 0) & (px[:, 0] <= W - 1)
    assert not inside[np.abs(a / 600.0) > 1.4].any()
    plain = _cam([0.0] * 5)
    assert plain["r2max"] == np.inf
    ppx, _, _ = tx.project(plain, X)
    assert np.allclose(np.abs(ppx[:, 0] - (W - 1) / 2), F * np.abs(a) / 600.0)
