"""idScheme "plan": marker ids carry no meaning; the request's markerSegments says where each one sits."""
import copy

import pytest

from wallgeometry import document_from_solution
from wallgeometry.request import RequestError, parse_request
from wallgeometry.solver import apply_gravity, solve_structure

# The Attic's marker plan: the legacy ids, except the four spares (24-27, legacy "segment 4") are
# renumbered to 44-47 — ids the legacy scheme cannot even express — and planned on the main wall.
RENUMBER = {24: 44, 25: 45, 26: 46, 27: 47}
PLAN_SEGMENTS = {0: 0, 1: 0, 2: 0, 3: 0, 4: 0, 5: 0, 6: 1, 7: 1, 8: 1, 9: 1, 10: 1, 12: 2, 14: 2, 15: 2,
                 44: 0, 45: 0, 46: 0, 47: 0, 31: 5, 32: 5, 33: 5}


def _plan_request(capture1_request):
    doc = copy.deepcopy(capture1_request)
    for photo in doc["photos"]:
        for m in photo["markers"]:
            m["id"] = RENUMBER.get(m["id"], m["id"])
    doc["idScheme"] = "plan"
    doc["markerSegments"] = {str(k): v for k, v in PLAN_SEGMENTS.items()}
    for s in doc["segments"]:
        if s["index"] == 5:
            s["declaredAngleDeg"] = 45.0  # the plan states every surface's angle
    return doc


@pytest.fixture(scope="module")
def plan_structure(capture1_request):
    return solve_structure(parse_request(_plan_request(capture1_request)))


def test_plan_scheme_matches_the_legacy_solve(plan_structure):
    doc = document_from_solution(apply_gravity(plan_structure))
    seg0 = next(s for s in doc["segments"] if s["index"] == 0)
    assert 45.2 < seg0["measuredAngleDeg"] < 45.6
    assert set(plan_structure["members"]) == {"0", "1", "2", "5"}
    assert {44, 45, 46, 47} <= set(plan_structure["members"]["0"])
    assert not [d for d in plan_structure["decisions"] if d["kind"] == "merge"]  # planned, nothing to adopt
    m44 = next(m for m in doc["markers"] if m["id"] == 44)
    assert (m44["segment"], m44["nominalSegment"], m44["role"]) == (0, 0, None)
    assert doc["idScheme"] == "plan" and doc["markerSegments"]["44"] == 0


def _minimal(**over):
    doc = {"markerSizeMm": 125, "segments": [{"index": 0}], "idScheme": "plan", "markerSegments": {"49": 0},
           "photos": [{"name": "a", "width": 100, "height": 80, "focal35mm": 14,
                       "markers": [{"id": 49, "corners": [[1, 1], [2, 1], [2, 2], [1, 2]]}]}]}
    doc.update(over)
    return doc


def test_plan_scheme_accepts_every_dictionary_id():
    r = parse_request(_minimal())
    assert r.segment_of(49) == 0 and r.role_of(49) is None


@pytest.mark.parametrize("bad, msg", [
    ({"markerSegments": None}, "needs 'markerSegments'"),
    ({"markerSegments": {"48": 0}}, "not in 'markerSegments'"),
    ({"markerSegments": {"50": 0}}, "keys must be marker ids"),
    ({"markerSegments": {"49": -1}}, "segment index"),
    ({"idScheme": "whatever"}, "idScheme"),
])
def test_plan_scheme_validation(bad, msg):
    with pytest.raises(RequestError, match=msg):
        parse_request(_minimal(**bad))


def test_legacy_requests_are_unchanged():
    legacy = _minimal(idScheme="segment*6+role", markerSegments=None)
    legacy["photos"][0]["markers"][0]["id"] = 7
    r = parse_request(legacy)
    assert r.marker_segments == {} and r.segment_of(7) == 1 and r.role_of(7) == "TR"
