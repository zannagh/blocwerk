"""The texture renderer projects with the full OpenCV lens model + skew, like SolvedCamera.Project (C#)."""
import cv2
import numpy as np
from synthetic import _look_at

from wallgeometry import textures as tx

W, H = 4032, 3024
DIST = [0.05, -0.07, 0.0012, -0.0008, 0.01]  # k1, k2, p1, p2, k3 (COLMAP OPENCV exports p1, p2)


def _cam(dist, skew=0.0):
    centre = np.array([300.0, -1500.0, 1200.0])
    R = _look_at(centre, np.array([0.0, 0.0, 1000.0]))
    K = np.array([[3000.0, skew, 2015.0], [0, 3010.0, 1508.0], [0, 0, 1]])
    return tx._cam({"K": K.ravel().tolist(), "dist": dist, "R": R.ravel().tolist(),
                    "t": (-R @ centre).tolist(), "width": W, "height": H})


def _points():
    rng = np.random.default_rng(0)
    return np.stack([rng.uniform(-900, 1500, 500), np.zeros(500), rng.uniform(300, 1800, 500)], -1)


def test_tangential_distortion_matches_opencv():
    cam = _cam(DIST)
    X = _points()
    px, _, _ = tx.project(cam, X)
    rvec, _ = cv2.Rodrigues(cam["R"])
    want, _ = cv2.projectPoints(X, rvec, cam["t"], cam["K"], np.array(DIST))
    assert np.abs(px - want[:, 0]).max() < 1e-6
    radial, _, _ = tx.project(_cam(DIST[:2] + [0.0, 0.0, DIST[4]]), X)
    assert np.abs(px - radial).max() > 1.0  # the tangential terms matter at the image edge


def test_skew_shears_x_by_the_distorted_y_like_solved_camera():
    skew = 4.0
    cam = _cam(DIST, skew)
    X = _points()
    px, _, Xc = tx.project(cam, X)
    # SolvedCamera.Project: (K[0] xd + K[1] yd + K[2], K[4] yd + K[5])
    x, y = Xc[:, 0] / Xc[:, 2], Xc[:, 1] / Xc[:, 2]
    k1, k2, p1, p2, k3 = DIST
    r2 = x * x + y * y
    radial = 1 + k1 * r2 + k2 * r2 * r2 + k3 * r2 ** 3
    xd = x * radial + 2 * p1 * x * y + p2 * (r2 + 2 * x * x)
    yd = y * radial + p1 * (r2 + 2 * y * y) + 2 * p2 * x * y
    K = cam["K"]
    assert np.abs(px[:, 0] - (K[0, 0] * xd + skew * yd + K[0, 2])).max() < 1e-6
    assert np.abs(px[:, 1] - (K[1, 1] * yd + K[1, 2])).max() < 1e-6


def test_short_dist_reads_missing_terms_as_zero_like_solved_camera():
    cam = _cam([0.05, -0.07, 0.001])  # SolvedCamera.D(i): dist[i], 0 past the end
    assert cam["p"] == (0.001, 0.0) and cam["k"] == (0.05, -0.07, 0.0)


def test_radial_only_cameras_project_bit_identically_to_before():
    cam = _cam(DIST[:2] + [0.0, 0.0, DIST[4]])
    px, z, Xc = tx.project(cam, _points())
    xn, yn = Xc[..., 0] / z, Xc[..., 1] / z
    r2 = xn * xn + yn * yn
    k1, k2, k3 = cam["k"]
    d = 1 + k1 * r2 + k2 * r2 * r2 + k3 * r2 * r2 * r2
    K = cam["K"]
    before = np.stack([K[0, 0] * xn * d + K[0, 2], K[1, 1] * yn * d + K[1, 2]], -1)
    assert np.array_equal(px, before)
