"""A declared vertical reference that folds into several facets: gravity from the whole segment's plane,
reported in quality; near-threshold facet decisions are flagged."""
import copy
import json
import os

import multiview as mv
import numpy as np
import pytest

from wallgeometry import solve_document
from wallgeometry.refplanes import BORDERLINE_DEG, _shares, borderline_decisions

ATTIC = os.path.join(os.path.dirname(os.path.abspath(__file__)), "fixtures", "attic53-false17-request.json")
LEAN_DEG = 8.0


def _angles(doc):
    return {f["id"]: f["measuredAngleDeg"] for s in doc["segments"] for f in s["facets"]}


def _tilt_error_deg(sol):
    """Angle between the solved up and the synthetic scene's true up (world z of the scene)."""
    fp, fx = sol["fprob"], sol["fx"]
    root = fp.root
    R, _ = fp.cam(fx, root)
    true_R = next(r for n, r, _ in mv._cameras() if n == root)
    up_true = R.T @ (true_R @ [0, 0, 1.0])  # scene z in the solve's (root-camera) frame
    return float(np.degrees(np.arccos(np.clip(sol["up"] @ up_true, -1, 1))))


@pytest.fixture(scope="module")
def clean():
    req, _ = mv.request()
    return solve_document(req)


@pytest.fixture(scope="module")
def folded():
    """Markers 6 and 9 (the left column of the front segment) lean 8 deg against the other four."""
    req, _ = mv.request(mk=mv.leaned(mv._markers(), [6, 9], LEAN_DEG))
    return solve_document(req)


def test_no_split_leaves_gravity_unchanged(clean):
    doc, sol = clean
    assert sol["gravity_planes"] == {}
    assert sol["gravity"]["constraints"] == ["facet 1 normal", "facet 2 normal"]
    assert "splitReferences" not in doc["quality"]["gravityDetail"]
    assert doc["quality"]["checks"]["warnings"] == []
    assert doc["quality"]["checks"]["borderlineFacetDecisions"] == []
    # exactly the unit-weight least squares over the facet normals (n1 x n2 for two references)
    n1, n2 = sol["normals"]["1"], sol["normals"]["2"]
    cross = np.cross(n1, n2) / np.linalg.norm(np.cross(n1, n2))
    assert abs(cross @ sol["up"]) == pytest.approx(1.0, abs=1e-9)
    assert _tilt_error_deg(sol) < 0.2


def test_folded_reference_is_one_gravity_constraint(folded):
    doc, sol = folded
    split = next(d for d in sol["decisions"] if d["kind"] == "split")
    assert split["facets"] == [[6, 9], [7, 8, 10, 11]]
    assert split["minPlaneAngleDeg"] == pytest.approx(LEAN_DEG, abs=0.5) and split["foldDeg"] == 5.0
    assert sol["gravity"]["constraints"] == ["segment 1 plane (facets 1a+1b)", "facet 2 normal"]
    # the old equal vote for 1a, 1b and 2 put the vertical 3.95 deg off; the whole plane follows 1b (1.7)
    assert _tilt_error_deg(sol) < 2.5


def test_folded_reference_is_reported(folded):
    doc, _ = folded
    rep = doc["quality"]["gravityDetail"]["splitReferences"]
    assert len(rep) == 1 and rep[0]["segment"] == 1 and rep[0]["method"] == "whole-segment plane"
    assert rep[0]["foldDeg"] == pytest.approx(LEAN_DEG, abs=0.5)
    a, b = rep[0]["pieces"]
    assert (a["facet"], a["markerIds"], b["facet"], b["markerIds"]) == ("1a", [6, 9], "1b", [7, 8, 10, 11])
    assert b["share"] > 0.6 and a["share"] + b["share"] == pytest.approx(1.0, abs=0.002)
    assert b["observations"] > a["observations"] and b["extentMm"] > a["extentMm"]
    assert abs(b["leanDeg"]) < abs(a["leanDeg"])
    assert a["leanDeg"] - b["leanDeg"] == pytest.approx(LEAN_DEG, abs=0.5)  # leans out = overhang (+)
    text = doc["quality"]["checks"]["warnings"][0]
    assert text.startswith("front is declared vertical but is not flat: markers [6, 9] (facet 1a)")
    assert "better-supported part [7, 8, 10, 11]" in text


def test_shares_follow_the_lever_rule():
    assert _shares([1.0, 3.0]).tolist() == pytest.approx([0.75, 0.25])
    assert _shares([0.0, 2.0])[0] > 0.99


@pytest.mark.parametrize("kind, angle, fold, flagged", [
    ("split", 5.33, 5.0, True), ("splitRejected", 4.97, 5.0, True), ("split", 5.0 + BORDERLINE_DEG, 5.0, True),
    ("split", 5.6, 5.0, False), ("splitRejected", 4.2, 5.0, False)])
def test_borderline_decisions(kind, angle, fold, flagged):
    d = {"kind": kind, "segment": 1, "minPlaneAngleDeg": angle, "foldDeg": fold}
    other = {"kind": "splitRejected", "segment": 3, "clusters": [[18], [19, 20]], "reason": "odd marker"}
    assert borderline_decisions([d, other]) == ([{"segment": 1, "kind": kind, "minPlaneAngleDeg": angle,
                                                   "foldDeg": fold}] if flagged else [])


def test_attic_kickboard_split_keeps_the_main_wall_angle():
    """The Attic: the kickboard (markers 6-10) folds by 4.97 deg in this capture (one facet at the default
    5.0; main wall 45.18). Forced to split, each piece used to get a full gravity vote (46.42 deg)."""
    with open(ATTIC) as fh:
        req = json.load(fh)
    req = copy.deepcopy(req)
    req["options"] = {**req.get("options", {}), "validate": False, "facets": {"foldDeg": 4.9}}
    doc, _ = solve_document(req)
    ang = _angles(doc)
    assert set(ang) >= {"1a", "1b"}
    assert ang["0"] == pytest.approx(45.18, abs=0.15)
    assert ang["5"] == pytest.approx(44.91, abs=0.15)
    rep = doc["quality"]["gravityDetail"]["splitReferences"][0]
    assert [p["markerIds"] for p in rep["pieces"]] == [[6, 9], [7, 8, 10]]
    assert rep["pieces"][1]["share"] > 0.7
    warn = doc["quality"]["checks"]["warnings"]
    assert warn[0].startswith("kickboard is declared vertical but is not flat")
    assert "borderline" in warn[1] and "split it into facets" in warn[1]
