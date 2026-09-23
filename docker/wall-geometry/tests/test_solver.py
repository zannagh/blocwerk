"""Solver on capture 1 (the real wall), without any wall-specific code in the solver."""
import copy
import dataclasses

import numpy as np
import pytest

from wallgeometry import document_from_solution
from wallgeometry.request import RequestError, Segment, parse_request
from wallgeometry.solver import apply_gravity


def _doc(sol):
    return document_from_solution(apply_gravity(sol))


def test_capture1_reproduces_known_result(structure):
    doc = _doc(structure)
    seg0 = next(s for s in doc["segments"] if s["index"] == 0)
    # previous hand-tuned solve: 45.38 deg; the generic path (auto down-weighting) lands within
    # a small fraction of the leave-one-photo-out spread (44.88..45.87)
    assert 45.2 < seg0["measuredAngleDeg"] < 45.6
    assert doc["quality"]["n1n2AngleDeg"] == pytest.approx(90.13, abs=0.1)
    assert doc["quality"]["reprojRmsPx"] < 1.7
    assert doc["quality"]["checks"]["markerSideRmsErrMm"] < 8
    assert doc["world"]["gravityKnown"] is True
    assert doc["quality"]["gravity"] != "unknown"


def test_spares_auto_merge_into_main_wall(structure):
    members = structure["members"]
    assert {24, 25, 26, 27} <= set(members["0"])
    assert set(members) == {"0", "1", "2", "5"}  # no facet for undeclared seg 4, no split
    merge = [d for d in structure["decisions"] if d["kind"] == "merge"]
    assert merge and merge[0]["markers"] == [24, 25, 26, 27] and merge[0]["intoFacet"] == "0"
    assert merge[0]["normalAngleDeg"] < 5 and merge[0]["maxOffsetMm"] < 40
    doc = _doc(structure)
    m24 = next(m for m in doc["markers"] if m["id"] == 24)
    assert (m24["segment"], m24["nominalSegment"], m24["facet"]) == (0, 4, "0")


def test_bent_marker_is_downweighted_not_a_fold(structure):
    assert 32 in structure["downweighted"]
    assert structure["members"]["5"] == [31, 32, 33]


def test_level_pair_moves_gravity(structure):
    base = apply_gravity(copy.copy(structure), [])
    lev = apply_gravity(copy.copy(structure), [(14, 15)])
    dz = lambda s: abs((s["centres"][14] - s["centres"][15]) @ s["up"])
    assert dz(lev) < dz(base)
    assert lev["angles"]["0"]["tiltDeg"] != pytest.approx(base["angles"]["0"]["tiltDeg"], abs=0.1)
    assert "level pair 14-15" in lev["gravity"]["constraints"]


def test_gravity_unknown_still_gives_millimetres(structure):
    req = structure["req"]
    segs = {k: dataclasses.replace(v, vertical_reference=(k == 1)) for k, v in req.segments.items()}
    structure["req"] = dataclasses.replace(req, segments=segs, level_pairs=[])
    doc = _doc(structure)
    assert doc["quality"]["gravity"] == "unknown"
    assert doc["world"]["gravityKnown"] is False
    assert all(f["measuredAngleDeg"] is None for s in doc["segments"] for f in s["facets"])
    for m in doc["markers"]:
        c = np.array(m["cornersPlaneMm"])
        side = np.linalg.norm(c - np.roll(c, -1, 0), axis=1).mean()
        assert side == pytest.approx(125.0, abs=0.5)  # facet-constrained: exact marker size


def _minimal(**over):
    doc = {"markerSizeMm": 125, "segments": [{"index": 0}],
           "photos": [{"name": "a", "width": 100, "height": 80, "focal35mm": 14,
                       "markers": [{"id": 1, "corners": [[1, 1], [2, 1], [2, 2], [1, 2]]}]}]}
    doc.update(over)
    return doc


@pytest.mark.parametrize("bad, msg", [
    ({"markerSizeMm": -3}, "markerSizeMm"),
    ({"segments": []}, "segments"),
    ({"photos": [{"name": "a", "width": 100, "height": 80, "markers": []}]}, "focal"),
    ({"photos": [{"name": "a", "width": 100, "height": 80, "focal35mm": 14,
                  "markers": [{"id": 48, "corners": [[1, 1], [2, 1], [2, 2], [1, 2]]}]}]}, "id"),
    ({"photos": [{"name": "a", "width": 100, "height": 80, "focal35mm": 14,
                  "markers": [{"id": 1, "corners": [[1, 1], [2, 1], [2, 2], [1, 2]]},
                              {"id": 1, "corners": [[5, 1], [6, 1], [6, 2], [5, 2]]}]}]}, "twice"),
    ({"photos": [{"name": "a", "width": 100, "height": 80, "focal35mm": 14,
                  "markers": [{"id": 1, "corners": [[1, 1], [2, 1], [2, 2]]}]}]}, "corners"),
    ({"levelPairs": [[3, 3]]}, "levelPairs"),
    ({"dictionary": "DICT_ARUCO_ORIGINAL"}, "dictionary"),
])
def test_request_validation(bad, msg):
    with pytest.raises(RequestError, match=msg):
        parse_request(_minimal(**bad))


def test_minimal_request_parses():
    r = parse_request(_minimal())
    assert r.segments[0] == Segment(0, "segment 0", None, False)
