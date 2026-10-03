"""Per-marker measured side length (markers[].measuredSideMm): triangulated from the photos with the
solved cameras, so it reflects the PRINTED size even when the request declares another one."""
import copy

import numpy as np
import pytest

from wallgeometry import solve_document


@pytest.fixture(scope="module")
def misdeclared_doc(capture1_request):
    """Capture 1 (all markers printed at 125 mm) with marker 4 declared as 100 mm."""
    req = copy.deepcopy(capture1_request)
    req["markerSizeOverridesMm"] = {"4": 100}
    doc, _ = solve_document(req)
    return {m["id"]: m for m in doc["markers"]}


def test_misdeclared_marker_measures_its_printed_size(misdeclared_doc):
    m = misdeclared_doc[4]
    assert m["sizeMm"] == 100
    assert m["measuredSidePhotos"] >= 2
    assert m["measuredSideMm"] == pytest.approx(125, abs=2.5)


def test_other_markers_measure_125mm(misdeclared_doc):
    sides = np.array([m["measuredSideMm"] for i, m in misdeclared_doc.items()
                      if i != 4 and m["measuredSideMm"] is not None])
    assert len(sides) >= 15
    assert sides.mean() == pytest.approx(125, abs=1.0)
    assert np.median(np.abs(sides - 125)) < 1.5


def test_single_view_markers_have_no_measured_side(misdeclared_doc):
    single = [m for m in misdeclared_doc.values() if m["observations"] == 1]
    assert single
    for m in single:
        assert m["measuredSideMm"] is None
        assert m["measuredSidePhotos"] is None
