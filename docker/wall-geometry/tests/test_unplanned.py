"""Unplanned markers (unplannedMarkerIds): kept when the solve confirms them on a solved surface, else dropped."""
import pytest

from wallgeometry import document_from_solution
from wallgeometry.request import RequestError, parse_request
from wallgeometry.solver import apply_gravity, solve_structure
from wallgeometry.unplanned import REASON

from test_plan_scheme import _minimal, _plan_request


def _unplanned(capture1_request, ids):
    doc = _plan_request(capture1_request)
    for i in ids:
        del doc["markerSegments"][str(i)]
    doc["unplannedMarkerIds"] = sorted(ids)
    return doc


def _dropped(sol):
    return {r["id"]: r for r in sol["rejected"] if r["reason"] == REASON}


def test_an_unplanned_marker_on_the_main_wall_is_kept(capture1_request):
    sol = solve_structure(parse_request(_unplanned(capture1_request, [47])))
    assert 47 in sol["members"]["0"]
    assert not _dropped(sol)
    doc = document_from_solution(apply_gravity(sol))
    m47 = next(m for m in doc["markers"] if m["id"] == 47)
    assert (m47["segment"], m47["nominalSegment"], m47.get("unplanned")) == (0, None, True)
    assert "47" not in doc["markerSegments"]


def test_an_unplanned_marker_seen_once_is_dropped(capture1_request):
    doc = _unplanned(capture1_request, [47])
    seen = [p for p in doc["photos"] if any(m["id"] == 47 for m in p["markers"])]
    for p in seen[1:]:
        p["markers"] = [m for m in p["markers"] if m["id"] != 47]
    sol = solve_structure(parse_request(doc))
    assert all(47 not in ms for ms in sol["members"].values())
    assert 47 not in {o["id"] for o in sol["obs"]}
    rec = _dropped(sol)[47]
    assert rec["markerDropped"] and rec["views"] == 1 and rec["photo"] == seen[0]["name"]


def test_an_unplanned_marker_off_every_solved_surface_is_dropped(capture1_request):
    # The kickboard's markers alone, unplanned: the kickboard is no declared facet then, and they lie on no other.
    kick = [6, 7, 8, 9, 10]
    sol = solve_structure(parse_request(_unplanned(capture1_request, kick)))
    placed = {m for ms in sol["members"].values() for m in ms}
    dropped = _dropped(sol)
    assert set(kick) == set(dropped) | (set(kick) & placed)
    assert dropped and all("does not lie on any solved surface" in r["detail"] for r in dropped.values())
    assert not set(dropped) & placed
    assert all(f.lstrip("-") == f for f in sol["members"])  # never a facet of an unplanned marker's own


@pytest.mark.parametrize("bad, msg", [
    ({"unplannedMarkerIds": [49]}, "both"),
    ({"unplannedMarkerIds": [50]}, "marker ids in"),
    ({"unplannedMarkerIds": "48"}, "must be a list"),
    ({"idScheme": "segment*6+role", "markerSegments": None, "unplannedMarkerIds": [3]}, "needs idScheme 'plan'"),
])
def test_unplanned_validation(bad, msg):
    with pytest.raises(RequestError, match=msg):
        parse_request(_minimal(**bad))


def test_an_unplanned_id_is_accepted_without_a_segment():
    doc = _minimal(markerSegments={"3": 0}, unplannedMarkerIds=[49])
    r = parse_request(doc)
    assert r.unplanned == {49} and r.segment_of(49) < 0 and r.nominal_segment(49) is None


def _misplaced(capture1_request, angle):
    # Marker 47 sits on the main wall (45 deg); the plan puts it alone on a panel declared at `angle`.
    doc = _plan_request(capture1_request)
    doc["markerSegments"]["47"] = 9
    doc["segments"].append({"index": 9, "name": "why is it there", "declaredAngleDeg": angle})
    return solve_structure(parse_request(doc))


def test_a_lone_marker_the_plan_puts_on_a_panel_at_another_angle_joins_the_facet_it_lies_on(capture1_request):
    sol = _misplaced(capture1_request, -45.0)
    assert 47 in sol["members"]["0"] and "9" not in sol["members"]
    move = next(d for d in sol["decisions"] if d["kind"] == "move" and d["marker"] == 47)
    assert (move["fromFacet"], move["intoFacet"], move["declaredAngleDeg"]) == ("9", "0", -45.0)
    doc = document_from_solution(apply_gravity(sol))
    m47 = next(m for m in doc["markers"] if m["id"] == 47)
    assert (m47["segment"], m47["nominalSegment"]) == (0, 9)


def test_a_lone_marker_on_a_panel_the_plan_declares_alike_stays_its_own_facet(capture1_request):
    sol = _misplaced(capture1_request, 40.0)
    assert sol["members"]["9"] == [47]
