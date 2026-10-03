"""Plane-ICP fine alignment of the splat to the facets."""
import numpy as np

import splatworker.refine as refine_module
from splatworker.refine import refine, refine_frame

FACETS = [  # a vertical wall, an overhang folding out of it, and a side wall
    {"id": "0", "origin": [0, 0, 0], "u": [1, 0, 0], "v": [0, 0, 1], "normal": [0, -1, 0],
     "extentMm": {"aMin": 0, "aMax": 3000, "bMin": 0, "bMax": 2500}},
    {"id": "1", "origin": [0, 0, 2500], "u": [1, 0, 0], "v": [0, -0.7071, 0.7071], "normal": [0, -0.7071, -0.7071],
     "extentMm": {"aMin": 0, "aMax": 3000, "bMin": 0, "bMax": 1200}},
    {"id": "2", "origin": [3000, 0, 0], "u": [0, -1, 0], "v": [0, 0, 1], "normal": [-1, 0, 0],
     "extentMm": {"aMin": 0, "aMax": 2000, "bMin": 0, "bMax": 2500}},
]
DOC = {"segments": [{"facets": FACETS}]}


def wall_points(rng, per_facet=6000, noise=4.0, bumps=0.15):
    pts = []
    for f in FACETS:
        o, u, v, n = (np.array(f[k], float) for k in ("origin", "u", "v", "normal"))
        e = f["extentMm"]
        a = rng.uniform(e["aMin"], e["aMax"], per_facet)
        b = rng.uniform(e["bMin"], e["bMax"], per_facet)
        d = rng.normal(0, noise, per_facet)
        hold = rng.random(per_facet) < bumps          # holds: 20-120 mm proud of the wall
        d[hold] += rng.uniform(20, 120, hold.sum())
        pts.append(o + a[:, None] * u + b[:, None] * v + d[:, None] * n)
    return np.concatenate(pts)


def small_error():
    c, s = np.cos(np.radians(0.4)), np.sin(np.radians(0.4))
    E = np.eye(4)
    E[:3, :3] = np.array([[c, -s, 0], [s, c, 0], [0, 0, 1]])
    E[:3, 3] = [6.0, 14.0, -9.0]
    return E


def test_pulls_a_floating_splat_onto_the_planes():
    rng = np.random.default_rng(1)
    world = wall_points(rng)
    E = small_error()
    xyz = (world - E[:3, 3]) @ np.linalg.inv(E[:3, :3]).T   # splat frame: E maps it back onto the wall
    wrong = np.eye(4)                                        # the camera alignment missed E entirely
    M, report = refine(xyz, wrong, DOC)
    assert report["applied"]
    assert report["before"]["medianAbsMm"] > 8
    assert report["after"]["medianAbsMm"] < report["before"]["medianAbsMm"] - 5
    err = xyz @ M[:3, :3].T + M[:3, 3] - world
    for k, f in enumerate(FACETS):              # off-plane error on each facet's own points
        own = err[k * 6000:(k + 1) * 6000] @ np.array(f["normal"], float)
        assert np.median(np.abs(own)) < 2.0


def test_refuses_a_correction_beyond_the_bounds(monkeypatch):
    # A correction bigger than a fine alignment may make means the camera alignment itself is off.
    monkeypatch.setattr(refine_module, "MAX_SHIFT_MM", 5.0)
    world = wall_points(np.random.default_rng(2))
    E = small_error()
    M, report = refine((world - E[:3, 3]) @ np.linalg.inv(E[:3, :3]).T, np.eye(4), DOC)
    assert not report["applied"] and report["correction"]["shiftMm"] > 5
    assert np.allclose(M, np.eye(4))


def test_refine_frame_adds_the_block_and_never_raises():
    class S:
        xyz = wall_points(np.random.default_rng(3), per_facet=3000)
        alpha = np.ones(len(xyz))
        log_scale = np.full((len(xyz), 3), -6.0)
    frame = refine_frame({"toWorldMm": np.eye(4).tolist(), "scaleMmPerUnit": 1.0}, S, DOC)
    assert frame["refinement"]["method"] == "plane-icp" and len(frame["refinement"]["toWorldMm"]) == 4
    broken = refine_frame({"toWorldMm": np.eye(4).tolist(), "scaleMmPerUnit": 1.0}, S, {"segments": [{"facets": [{"id": "x"}]}]})
    assert broken["refinement"]["applied"] is False
