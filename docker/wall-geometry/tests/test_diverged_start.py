"""A free-BA start whose poses diverge (non-finite) stops there and keeps its best finite poses instead of crashing.

On Linux the five-photo repeated false 17 (test_isolate.py) drove one plausibility re-solve's poses-only BA to
non-finite poses; re-choosing the IPPE branches from them handed a NaN rotation to Rotation.from_matrix ("SVD did not
converge") and the whole solve failed."""
import numpy as np

import multiview as mv
from wallgeometry import freeba, solve_document
from wallgeometry.request import observations, parse_request


def _start_inputs():
    req = parse_request(mv.request()[0])
    obs = observations(req)
    cams, intr, _, prior = freeba.build_cameras(req)
    freeba.ippe_candidates(obs, cams, intr)
    freeba.select_branches(obs, {o["id"]: o["seg"] for o in obs})
    return obs, cams, intr, prior, {o["id"]: o["obj"] for o in obs}


def test_non_finite_marker_poses_stop_the_alternation_and_the_best_start_is_kept(monkeypatch):
    real = freeba.chain_fixed

    def diverged(obs, cams, intr, cam_pose):
        cp, mp = real(obs, cams, intr, cam_pose)
        return cp, {m: (np.full((3, 3), np.nan), np.full(3, np.nan)) for m in mp}

    monkeypatch.setattr(freeba, "chain_fixed", diverged)
    obs, cams, intr, prior, obj = _start_inputs()
    score, _, x, _, _ = freeba._pose_start(obs, cams, intr, prior, obj, freeba.root_order(obs)[0])
    assert np.isfinite(score)
    assert np.isfinite(x).all()


def test_a_start_that_diverges_at_once_loses_to_any_finite_one(monkeypatch):
    real = freeba.Problem.solve

    def nan_solve(self, x, *a, **k):
        out, res = real(self, x, *a, **k)
        return np.full_like(out, np.nan), res

    obs, cams, intr, prior, obj = _start_inputs()
    monkeypatch.setattr(freeba.Problem, "solve", nan_solve)
    score, *_ = freeba._pose_start(obs, cams, intr, prior, obj, freeba.root_order(obs)[0])
    assert score == np.inf


def test_the_clean_scene_is_unchanged():
    doc, _ = solve_document(mv.request()[0])
    assert doc["quality"]["reprojRmsPx"] < 0.6
