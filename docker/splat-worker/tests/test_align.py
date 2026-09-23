"""Similarity alignment to the solver's cameras + crop box from the facets."""
import numpy as np
import pytest

from computejobs.child import JobError
from splatworker.align import align, umeyama


def rot(axis, deg):
    a = np.radians(deg)
    c, s = np.cos(a), np.sin(a)
    x, y, z = np.array(axis) / np.linalg.norm(axis)
    return np.array([[c + x * x * (1 - c), x * y * (1 - c) - z * s, x * z * (1 - c) + y * s],
                     [y * x * (1 - c) + z * s, c + y * y * (1 - c), y * z * (1 - c) - x * s],
                     [z * x * (1 - c) - y * s, z * y * (1 - c) + x * s, c + z * z * (1 - c)]])


def geometry(centres_mm):
    cams = []
    for i, C in enumerate(centres_mm):
        R = rot([0.3, 1, 0.2], 10 * i)
        cams.append({"image": f"IMG_{i}", "R": R.flatten().tolist(), "t": (-R @ C).tolist()})
    facet = {"origin": [0, 0, 0], "u": [1, 0, 0], "v": [0, 0, 1],
             "extentMm": {"aMin": 0, "aMax": 4000, "bMin": 0, "bMax": 3000}}
    return {"cameras": cams, "segments": [{"index": 0, "facets": [facet]}]}


def test_recovers_similarity_and_frames_the_wall():
    rng = np.random.default_rng(3)
    C = rng.uniform([-500, -4000, 1000], [4500, -2000, 2000], size=(8, 3))  # in front of the wall (y < 0)
    s_true, R_true, t_true = 250.0, rot([1, 2, 3], 40), np.array([100.0, -50.0, 30.0])
    colmap = {f"IMG_{i}": R_true.T @ (c - t_true) / s_true for i, c in enumerate(C)}
    colmap["IMG_99"] = np.zeros(3)  # registered but unknown to the solver: ignored
    f = align(colmap, geometry(C), margin_mm=400)
    assert f["alignment"]["cameras"] == 8 and f["alignment"]["residualMmMax"] < 1e-6
    assert f["scaleMmPerUnit"] == pytest.approx(s_true)
    V = np.array(f["toViewer"])
    centre_world = np.array([2000.0, 0, 1500])  # facet centre -> viewer origin
    p = V[:3, :3] @ (R_true.T @ (centre_world - t_true) / s_true) + V[:3, 3]
    assert np.allclose(p, 0, atol=1e-6)
    M = np.array(f["matrix"]).reshape(4, 4).T  # column-major == toViewer
    assert np.allclose(M, V)
    (lo, hi) = f["crop"]
    assert lo == pytest.approx([-2.4, -1.9, -0.4]) and hi == pytest.approx([2.4, 1.9, 0.4])


def test_needs_three_shared_cameras():
    g = geometry(np.eye(3) * 1000)
    with pytest.raises(JobError, match="only 2 registered"):
        align({"IMG_0": np.zeros(3), "IMG_1": np.ones(3)}, g, 400)


def test_umeyama_rejects_degenerate():
    with pytest.raises(JobError):
        umeyama(np.zeros((3, 3)), np.ones((3, 3)))
