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


@pytest.mark.parametrize("n_false, scale", [(4, 0.6), (5, 0.6), (5, 1.0)])
def test_a_hold_read_as_a_marker_in_several_photos_costs_only_that_marker(n_false, scale):
    """Whichever stage removes the false 17s depends on the platform's numerics: this scene sits on the edge of the
    free solve's basin, so on one machine the plausibility rounds clean it up, on another only the isolation does. What
    must hold everywhere: a clean model, and nothing but 17 dropped or down-weighted for it."""
    doc, sol = solve_document(mv.with_repeated_false_marker(n_false, scale))
    assert _q(doc)["reprojRmsPx"] < 0.6
    assert set(sol["downweighted"]) <= {17}
    assert {r["id"] for r in _q(doc)["rejectedObservations"]} <= {17}
    real = {m: sum(1 for p in mv.request()[0]["photos"] for k in p["markers"] if k["id"] == m) for m in range(6, 17)}
    used = {m["id"]: m["observations"] for m in doc["markers"]}
    assert all(used.get(m) == n for m, n in real.items() if n)
    for rec in _isolated(doc):
        assert (rec["id"], rec["markerDropped"]) == (17, True)
        assert "something else on the wall" in rec["detail"]


def _stub_solves(*results):
    """solve_once returning (doc, sol) pairs in order, and the requests it was given."""
    seen = []

    def solve_once(req):
        seen.append(req)
        return results[len(seen) - 1]
    return solve_once, seen


def test_run_drops_the_suspect_and_keeps_the_better_solve():
    first = (_doc(4.98), _sol({17: 195.0, 4: 60.0, 5: 50.0}, downweighted=[17, 4, 5]))
    second = (_doc(3.55), _sol({4: 2.0, 5: 2.0}))
    solve_once, seen = _stub_solves(second)
    req = {"photos": [{"name": "p1", "markers": [{"id": 17}, {"id": 4}]}, {"name": "p2", "markers": [{"id": 5}]}]}

    doc, sol, records = isolate.run(*first, req, solve_once, lambda *_: None)

    assert (doc, sol) == second
    assert [m["id"] for p in seen[0]["photos"] for m in p["markers"]] == [4, 5]
    [rec] = records
    assert (rec["id"], rec["reason"], rec["markerDropped"], rec["views"]) == (17, isolate.REASON, True, 1)
    assert "3.55 px instead of 4.98 px" in rec["detail"]


def test_run_tries_the_next_suspect_when_the_worst_one_was_innocent():
    first = (_doc(121.3), _sol({14: 103.0, 17: 98.0, 16: 95.0, 5: 1.0}))
    still_poor = (_doc(71.0), _sol({17: 90.0, 16: 80.0, 5: 1.0}))
    clean = (_doc(0.39), _sol({14: 0.4, 16: 0.4, 5: 0.4}))
    solve_once, seen = _stub_solves(still_poor, clean)
    req = {"photos": [{"name": "p1", "markers": [{"id": 14}, {"id": 17}, {"id": 16}]}]}

    doc, _, records = isolate.run(*first, req, solve_once, lambda *_: None)

    assert doc == clean[0]
    assert [r["id"] for r in records] == [17]
    assert [[m["id"] for m in r["photos"][0]["markers"]] for r in seen] == [[17, 16], [14, 16]]


def test_run_keeps_the_original_when_the_trial_is_not_better():
    first = (_doc(3.0), _sol({17: 195.0, 4: 60.0, 5: 50.0}, downweighted=[17, 4, 5]))
    worse = (_doc(3.4), _sol({4: 2.0}))
    solve_once, seen = _stub_solves(worse)

    out = isolate.run(*first, {"photos": []}, solve_once, lambda *_: None)

    assert out == (*first, [])
    assert len(seen) == 1


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
    assert [m for m, _ in isolate.suspects(sol, _doc(4.98))] == [17]


def test_no_suspect_without_collateral_damage_or_without_a_dominant_marker():
    assert isolate.suspects(_sol({17: 195.0, 4: 3.0}, downweighted=[17]), _doc(2.0)) == []
    assert isolate.suspects(_sol({17: 60.0, 4: 50.0, 5: 40.0}, downweighted=[17, 4, 5]), _doc(3.0)) == []
    assert isolate.suspects(_sol({17: 8.0, 4: 2.0, 5: 2.0}, downweighted=[17, 4, 5]), _doc(3.0)) == []


def test_a_solve_too_poor_to_use_tries_the_three_worst_markers():
    """A wrecked start ranked the innocent 14 (103 px) above the false 17 (98 px)."""
    sol = _sol({14: 103.0, 17: 98.0, 16: 95.0, 12: 67.0, 5: 1.0})
    assert [m for m, _ in isolate.suspects(sol, _doc(121.3))] == [14, 17, 16]
    assert isolate.suspects(sol, _doc(3.9)) == []


def test_a_marker_whose_own_views_are_mostly_rejected_is_a_suspect():
    sol = _sol({17: 5.0, 4: 4.0}, rejected=[(17, "inconsistent-with-other-views")] * 3)
    assert [m for m, _ in isolate.suspects(sol, _doc(2.0))] == [17]
    one = _sol({17: 5.0, 4: 4.0}, rejected=[(17, "inconsistent-with-other-views")])
    assert isolate.suspects(one, _doc(2.0)) == []


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
