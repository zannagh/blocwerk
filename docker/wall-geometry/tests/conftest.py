import copy
import json
import os
import sys

import pytest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
# the shared protocol package (installed in the image; from the source tree when run in place)
sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(HERE)), "compute-jobs-py"))
sys.path.insert(0, HERE)

FIXTURE = os.path.join(HERE, "fixtures", "capture1-request.json")


@pytest.fixture(scope="session")
def capture1_request():
    with open(FIXTURE) as fh:
        return json.load(fh)


@pytest.fixture(scope="session")
def capture1_structure(capture1_request):
    """Capture 1 solved once per session up to (not including) gravity (~20-30 s)."""
    from wallgeometry.request import parse_request
    from wallgeometry.solver import solve_structure
    return solve_structure(parse_request(capture1_request))


@pytest.fixture
def structure(capture1_structure):
    """A shallow copy tests may re-frame (apply_gravity / to_world only add keys)."""
    return copy.copy(capture1_structure)
