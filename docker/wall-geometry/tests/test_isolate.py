"""One marker id the photos cannot agree on: left out and solved again, instead of bending or down-weighting the rest."""
import copy

import multiview as mv
import numpy as np
import pytest

from wallgeometry import isolate, solve_document


def _q(doc):
    return doc["quality"]


def _isolated(doc):
    return [r for r in _q(doc)["rejectedObservations"] if r["reason"] == isolate.REASON]


@pytest.mark.parametrize("n_false", [4, 5])
def test_a_hold_read_as_a_marker_in_several_photos_takes_the_marker_out_not_the_others(n_false):
    doc, sol = solve_document(mv.with_repeated_false_marker(n_false))
    [rec] = _isolated(doc)
    assert (rec["id"], rec["markerDropped"]) == (17, True)
    assert "something else on the wall" in rec["detail"]
    assert 17 not in {m["id"] for m in doc["markers"]}
    assert _q(doc)["reprojRmsPx"] < 0.6
    assert sol["downweighted"] == {}
    assert isolate.affected(sol) == set()


def test_without_isolation_the_same_capture_fails_or_loses_other_markers():
    req = mv.with_repeated_false_marker(4)
    req["options"] = {"validate": False, "isolateMarkers": False}
    doc, _ = solve_document(req)
    assert _isolated(doc) == []
    assert _q(doc)["reprojRmsPx"] > 4.0  # the app's gate refuses it


def test_a_wrecked_first_solve_is_rescued_by_leaving_the_marker_out():
    """Five photos read a full-size 'marker' off a hold 30 cm out of the side wall: the free solve starts in a wrong
    basin (~95 px) that the plausibility rounds cannot repair."""
    req = mv.with_repeated_false_marker(5, scale=1.0)
    for p in req["photos"][:5]:
        hold = next(m for m in p["markers"] if m["id"] == 17)
        hold["corners"] = [[x - 20.0 * k, y + 15.0 * k] for k, (x, y) in enumerate(hold["corners"])]
    doc, _ = solve_document(req)
    assert [r["id"] for r in _isolated(doc)] == [17]
    assert _q(doc)["reprojRmsPx"] < 0.6


def test_a_clean_capture_is_solved_once_and_left_alone(monkeypatch):
    req, _ = mv.request()
    solves = []
    monkeypatch.setattr(isolate, "without", lambda *a: solves.append(a) or pytest.fail("solved again"))
    doc, _ = solve_document(req)
    assert solves == []
    assert _isolated(doc) == []


def _sol(first, downweighted=(), rejected=()):
    obs = [{"id": m} for m in first for _ in range(6)]
    return {"marker_rms_first": dict(first), "downweighted": {m: {} for m in downweighted},
            "rejected": [{"id": m, "reason": r} for m, r in rejected], "obs": obs}


def _doc(rms):
    return {"quality": {"reprojRmsPx": rms}}


def test_the_attic_numbers_single_out_17():
    """fd040baf: ten markers down-weighted; 17's free RMS 195 px, the next one 68 px."""
    first = {17: 195.4, 4: 67.8, 15: 67.6, 10: 65.6, 11: 56.1, 0: 48.2, 18: 45.4, 13: 41.7, 19: 38.9, 16: 38.4, 5: 1.8}
    sol = _sol(first, downweighted=[4, 0, 11, 13, 15, 16, 17, 18, 19, 10])
    assert isolate.suspect(sol, _doc(4.98))[0] == 17


def test_no_suspect_without_collateral_damage_or_without_a_dominant_marker():
    assert isolate.suspect(_sol({17: 195.0, 4: 3.0}, downweighted=[17]), _doc(2.0)) is None
    assert isolate.suspect(_sol({17: 60.0, 4: 50.0, 5: 40.0}, downweighted=[17, 4, 5]), _doc(3.0)) is None
    assert isolate.suspect(_sol({17: 8.0, 4: 2.0, 5: 2.0}, downweighted=[17, 4, 5]), _doc(3.0)) is None


def test_a_marker_whose_own_views_are_mostly_rejected_is_a_suspect():
    sol = _sol({17: 5.0, 4: 4.0}, rejected=[(17, "inconsistent-with-other-views")] * 3)
    assert isolate.suspect(sol, _doc(2.0))[0] == 17
    one = _sol({17: 5.0, 4: 4.0}, rejected=[(17, "inconsistent-with-other-views")])
    assert isolate.suspect(one, _doc(2.0)) is None


@pytest.mark.parametrize("old, new, keep", [
    ((10, 4.98), (1, 3.55), True),    # the Attic: fewer markers it cannot fit, lower error
    ((10, 3.0), (1, 3.2), False),     # fewer, but a worse fit
    ((0, 128.0), (0, 71.0), False),   # a wrecked solve that stays unusable: an innocent marker was left out
    ((0, 94.6), (0, 0.39), True),     # a wrecked solve made usable
])
def test_the_solve_without_the_suspect_is_kept_only_when_it_is_better(old, new, keep):
    def s(n):
        return _sol({}, downweighted=list(range(n)))
    assert isolate.better(s(new[0]), _doc(new[1]), s(old[0]), _doc(old[1])) is keep


def test_a_solve_that_loses_gravity_is_never_kept():
    def s():
        return _sol({})
    old = {"quality": {"reprojRmsPx": 94.6}, "world": {"gravityKnown": True}}
    new = {"quality": {"reprojRmsPx": 0.39}, "world": {"gravityKnown": False}}
    assert isolate.better(s(), new, s(), old) is False
    assert isolate.better(s(), {**new, "world": {"gravityKnown": True}}, s(), old) is True


def test_without_drops_every_detection_of_the_marker_but_keeps_the_photos():
    req = {"photos": [{"name": "a", "markers": [{"id": 17}]}, {"name": "b", "markers": [{"id": 17}, {"id": 3}]}],
           "unplannedMarkerIds": [17, 48]}
    out = isolate.without(req, 17)
    assert out["photos"] == [{"name": "a", "markers": []}, {"name": "b", "markers": [{"id": 3}]}]
    assert out["unplannedMarkerIds"] == [48]
    assert len(req["photos"]) == 2  # the caller's request is untouched


def test_the_option_is_checked():
    from wallgeometry.request import RequestError, parse_request
    req, _ = mv.request()
    with pytest.raises(RequestError, match="isolateMarkers"):
        parse_request({**copy.deepcopy(req), "options": {"isolateMarkers": "yes"}})


def test_a_trial_that_fails_numerically_keeps_the_solve_with_the_marker():
    sol = _sol({17: 195.0, 4: 60.0, 5: 50.0}, downweighted=[17, 4, 5])
    doc = _doc(4.98)

    def crash(_):
        raise np.linalg.LinAlgError("SVD did not converge")

    out = isolate.run(doc, sol, {"photos": []}, crash, lambda *_: None)
    assert out == (doc, sol, [])


def test_a_plausibility_retry_that_fails_numerically_keeps_the_best_model(monkeypatch):
    import wallgeometry
    real, calls = wallgeometry._solve_once, []

    def once(req, progress, limits):
        calls.append(1)
        if len(calls) > 1:
            raise np.linalg.LinAlgError("SVD did not converge")
        doc, sol = real(req, progress, limits)
        doc["quality"]["checks"]["markerSideRmsErrMm"] = 99.0  # implausible: forces a retry
        return doc, sol

    monkeypatch.setattr(wallgeometry, "_solve_once", once)
    doc, _ = wallgeometry.solve_document(mv.request()[0])
    assert len(calls) == 2
    assert doc["quality"]["plausibility"]["problems"]
