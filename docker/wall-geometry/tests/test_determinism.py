"""The solve must not depend on Python's per-process hash seed (set iteration order in the greedy chain)."""
import json
import os
import subprocess
import sys

from wallgeometry.freeba import _starts, root_order

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
SCRIPT = """
import json, sys
from wallgeometry.request import parse_request
from wallgeometry.solver import solve_structure
sol = solve_structure(parse_request(json.load(open(sys.argv[1]))))
print(json.dumps({"root": sol["prob"].root, "free": round(sol["rms_free"], 4), "facet": round(sol["rms_facet"], 4)}))
"""


def _solve_with_seed(seed):
    env = dict(os.environ, PYTHONHASHSEED=str(seed), PYTHONPATH=os.pathsep.join([ROOT, os.environ.get("PYTHONPATH", "")]))
    out = subprocess.run([sys.executable, "-c", SCRIPT, os.path.join(HERE, "fixtures", "capture1-request.json")],
                         env=env, capture_output=True, text=True, check=True)
    return json.loads(out.stdout.strip().splitlines()[-1])


def test_solve_is_the_same_for_every_hash_seed():
    assert _solve_with_seed(1) == _solve_with_seed(7)


def test_root_order_is_best_first_and_stable():
    obs = [{"image": img, "id": mid} for img, ids in {"b": [1, 2, 3], "a": [1, 2, 3], "c": [1]}.items() for mid in ids]
    assert root_order(obs) == ["b", "a", "c"]  # equal markers and links: the name decides (max first)


def test_more_starts_only_for_large_captures():
    assert [_starts(n) for n in (10, 40, 41, 80, 81, 142, 400)] == [1, 1, 2, 2, 3, 4, 4]
