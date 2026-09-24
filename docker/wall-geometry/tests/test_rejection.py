"""False-detection rejection: removed (not just down-weighted), reported, capped."""
import copy
import json
import os

import multiview as mv
import numpy as np
import pytest

from wallgeometry import solve_document
from wallgeometry.reject import MAX_REJECT_FRACTION
from wallgeometry.request import RequestError, parse_request

ATTIC = os.path.join(os.path.dirname(os.path.abspath(__file__)), "fixtures", "attic53-false17-request.json")


def _q(doc):
    return doc["quality"]


@pytest.fixture(scope="module")
def clean():
    req, _ = mv.request()
    doc, _ = solve_document(req)
    return req, doc


def _facet_angles(doc):
    return {f["id"]: f["yawDeg"] for s in doc["segments"] for f in s["facets"]}


def test_clean_scene_has_no_rejections(clean):
    _, doc = clean
    assert _q(doc)["rejectedObservations"] == []
    assert _q(doc)["reprojRmsPx"] < 0.6
    assert _q(doc)["n1n2AngleDeg"] == pytest.approx(90.0, abs=0.2)


def test_single_view_false_marker_is_dropped(clean):
    req, ref = clean
    doc, sol = solve_document(mv.with_false_single_view(req))
    rej = _q(doc)["rejectedObservations"]
    assert [(r["photo"], r["id"], r["reason"], r["markerDropped"]) for r in rej] == \
        [("SYN_03", 17, "single-view-misfit", True)]
    assert 17 not in {m["id"] for m in doc["markers"]}
    assert 17 not in sol["downweighted"]
    assert _q(doc)["coplanarityFreeSolveMm"]["2"] < 1.0
    assert _q(doc)["reprojRmsPx"] == pytest.approx(_q(ref)["reprojRmsPx"], abs=0.05)
    for fid, yaw in _facet_angles(ref).items():
        assert _facet_angles(doc)[fid] == pytest.approx(yaw, abs=0.05)


def test_false_detection_of_a_real_id_is_removed_from_that_photo_only(clean):
    req, ref = clean
    doc, _ = solve_document(mv.with_moved_observation(req, "SYN_04", 9))
    rej = _q(doc)["rejectedObservations"]
    assert [(r["photo"], r["id"], r["reason"], r["markerDropped"]) for r in rej] == \
        [("SYN_04", 9, "inconsistent-with-other-views", False)]
    assert rej[0]["otherViewsResidualPx"] > rej[0]["thresholdPx"]
    m9 = next(m for m in doc["markers"] if m["id"] == 9)
    assert m9["observations"] == sum(1 for p in req["photos"] for m in p["markers"] if m["id"] == 9) - 1
    assert _q(doc)["coplanarityFreeSolveMm"]["1"] < 0.5


def test_rejections_are_capped_at_ten_percent(clean):
    req, _ = clean
    bad = copy.deepcopy(req)
    n_obs = sum(len(p["markers"]) for p in bad["photos"])
    rng = np.random.default_rng(1)
    for p in bad["photos"]:  # corrupt ~20 % of all observations
        for m in p["markers"][:2]:
            m["corners"] = (np.array(m["corners"]) + rng.uniform(40, 90, 2)).tolist()
    doc, _ = solve_document(bad)
    rej = _q(doc)["rejectedObservations"]
    assert 0 < len(rej) <= int(MAX_REJECT_FRACTION * n_obs)


def test_option_turns_rejection_off(clean):
    req, _ = clean
    off = mv.with_false_single_view(req)
    off["options"] = {"validate": False, "rejectOutliers": False}
    doc, sol = solve_document(off)
    assert _q(doc)["rejectedObservations"] == [] and 17 in sol["downweighted"]
    with pytest.raises(RequestError, match="rejectOutliers"):
        parse_request({**off, "options": {"rejectOutliers": "yes"}})


def test_attic_false_17_on_a_black_hold_is_rejected():
    """Real 53-photo capture: id 17 read on a black hold in IMG_2803 (only there). Down-weighted,
    it bent the left triangle to 267 mm coplanarity and the RMS to 5.7 px; without it: 1.4 mm, 2.4 px."""
    with open(ATTIC) as fh:
        doc, _ = solve_document(json.load(fh))
    q = _q(doc)
    got = {(r["photo"], r["id"]): r for r in q["rejectedObservations"]}
    assert got[("IMG_2803", 17)]["markerDropped"] is True
    # the only others: marker 33 at a grazing, motion-blurred angle in two consecutive photos
    assert set(got) <= {("IMG_2803", 17), ("IMG_2822", 33), ("IMG_2823", 33)}
    assert 17 not in {m["id"] for m in doc["markers"]}
    assert q["coplanarityFreeSolveMm"]["2"] < 2.5
    assert q["reprojRmsPx"] < 2.6
    angles = {f["id"]: (f["measuredAngleDeg"], f["yawDeg"]) for s in doc["segments"] for f in s["facets"]}
    assert angles["0"][0] == pytest.approx(45.18, abs=0.1)
    assert angles["2"][1] == pytest.approx(89.44, abs=0.1)
    assert angles["5"][0] == pytest.approx(44.91, abs=0.1)
    # the kickboard's fold (4.97 deg) sits just under foldDeg 5.0: one facet, flagged as borderline
    [border] = q["checks"]["borderlineFacetDecisions"]
    assert (border["segment"], border["kind"], border["foldDeg"]) == (1, "splitRejected", 5.0)
    assert border["minPlaneAngleDeg"] == pytest.approx(4.97, abs=0.05)
    assert q["checks"]["warnings"] == [
        "kickboard: the facet decision is borderline. Its parts differ by 4.97° against the 5° fold "
        "threshold, so the solver kept it as one facet, but one or two more or different photos can tip it "
        "the other way."]
