"""Implausible models: detected by the same limits as the app's activation gate; worst offenders dropped and re-solved."""
import numpy as np
import pytest

import multiview as mv
from wallgeometry import plausible, solve_document
from wallgeometry.request import RequestError, parse_request


def _doc(mean=0.8, rms=6.0, main=45.8, kick=0.4, cam_mm=4000.0, gravity=True):
    marker = {"id": 0, "cornersWorldMm": [[0, 0, 0], [125, 0, 0], [125, 0, 125], [0, 0, 125]]}
    cam = lambda name, d: {"image": name, "R": [1, 0, 0, 0, 0, -1, 0, 1, 0], "t": [0, 0, d]}
    return {
        "markerSizeMm": 125, "world": {"gravityKnown": gravity},
        "segments": [
            {"index": 0, "name": "main wall", "declaredAngleDeg": 45, "facets": [{"id": "0", "measuredAngleDeg": main}]},
            {"index": 1, "name": "kickboard", "declaredAngleDeg": None, "angleIsGravityReference": True,
             "facets": [{"id": "1", "measuredAngleDeg": kick}]},
        ],
        "markers": [marker], "cameras": [cam("p01", 3000.0), cam("p02", cam_mm)],
        "quality": {"checks": {"markerSideMeanErrMm": mean, "markerSideRmsErrMm": rms}},
    }


def test_a_sound_model_is_plausible():
    assert plausible.problems(_doc()) == []


def test_the_broken_attic_model_fails_every_check():
    why = plausible.problems(_doc(mean=52193.7, rms=352208.8, main=-17.2, kick=15.3, cam_mm=881_318_000.0))
    assert len(why) == 4
    assert "+52193.7 mm" in why[0]
    assert "main wall (facet 0) measures -17.2 deg, declared 45 deg" in why[1]
    assert "kickboard (facet 1) measures 15.3 deg, declared 0 deg" in why[2]
    assert "p02: 881318 m" in why[3]


@pytest.mark.parametrize("mean,rms,bad", [(6.0, 2.0, False), (6.5, 2.0, True), (1.0, 9.9, False), (1.0, 10.1, True)])
def test_marker_sizes_are_judged_against_the_printed_size(mean, rms, bad):
    assert bool(plausible.problems(_doc(mean=mean, rms=rms))) == bad


def test_angles_are_not_judged_without_gravity():
    assert plausible.problems(_doc(main=-17.2, gravity=False)) == []


def _fake(obs_rms):
    """A solution whose final observations have the given per-observation RMS (px), photo p0k, id k."""
    obs = [{"image": img, "id": mid} for img, mid, _ in obs_rms]
    return {"rejected": [], "obs": obs, "err_facet": np.array([[r] * 4 for _, _, r in obs_rms])}


def test_offenders_are_the_worst_but_never_a_photos_last_detection():
    request = {"photos": [{"name": "p01", "markers": [{"id": 1}, {"id": 2}, {"id": 3}]},
                          {"name": "p02", "markers": [{"id": 17}]}]}
    sol = _fake([("p01", 1, 1.0), ("p01", 2, 90.0), ("p01", 3, 2.0), ("p02", 17, 500.0)])
    pick = plausible.offenders(_doc(), sol, request, {})
    # p02's only detection stays; p01 keeps at least one.
    assert list(pick) == [("p01", 2), ("p01", 3)]


def test_offenders_start_with_the_detections_reject_py_removed():
    request = {"photos": [{"name": "p01", "markers": [{"id": m} for m in range(6)]}]}
    sol = _fake([("p01", m, 1.0) for m in range(1, 6)])
    sol["rejected"] = [{"photo": "p01", "id": 0, "residualPx": 2371.0}]
    assert next(iter(plausible.offenders(_doc(), sol, request, {}))) == ("p01", 0)


def test_the_loop_leaves_a_plausible_scene_alone_and_can_be_switched_off():
    req, _ = mv.request()
    doc, _ = solve_document(req)
    assert "plausibility" not in doc["quality"]
    off = dict(req, options={**req.get("options", {}), "plausibilityRounds": False})
    parse_request(off)
    with pytest.raises(RequestError):
        parse_request(dict(req, options={"plausibilityRounds": "yes"}))
