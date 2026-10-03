"""The geometry kernel's golden cases (test/geometry-golden/*.json), shared with the C# tests
(test/Blocwerk.Core.Tests/GeometryGoldenTests.cs): both languages must give the same answers on the same walls.

Every case is a small wall-geometry document plus the expected occluder region per facet (its seam cuts / fold
clips as unit half-planes) and queries: point in a facet's shape, line of sight blocked by one facet or by the
scene, and visible (facing and not blocked). See docs/geometry-kernel.md.
"""
import json
import os
from pathlib import Path

import numpy as np
import pytest

from wallgeometry import kernel, occlusion


def _golden_dir():
    here = Path(__file__).resolve().parent
    candidates = [os.environ.get("GEOMETRY_GOLDEN_DIR"), here / "geometry-golden"]  # the test image copies it here
    candidates += [p / "test" / "geometry-golden" for p in here.parents]  # the repository
    for c in candidates:
        if c and Path(c).is_dir() and any(Path(c).glob("*.json")):
            return Path(c)
    raise FileNotFoundError(f"no geometry golden cases in {[str(c) for c in candidates if c]}")


GOLDEN = _golden_dir()
CASES = {p.stem: json.loads(p.read_text()) for p in sorted(GOLDEN.glob("*.json"))}


def _facets(doc):
    return [f for s in doc["segments"] for f in s["facets"]]


def _occluders(case):
    facets = _facets(case["model"])
    occs = occlusion.occluders(facets, case["model"].get("markers", []))
    return {o.id: o for o in occs}, {f["id"]: f for f in facets}


def _hidden(occs, q, exclude):
    others = [o for fid, o in occs.items() if fid not in exclude]
    X = np.array([q["target"]], float)
    return bool(occlusion.hidden(np.array(q["camera"], float), others, X, inset=q["insetMm"])[0])


def _answer(q, occs, facets):
    kind = q["kind"]
    if kind == "inShape":
        a, b = q["ab"]
        return bool(occs[q["facet"]].contains(np.array([a], float), np.array([b], float), q["insetMm"])[0])
    if kind == "blockedBy":
        return _hidden({q["occluder"]: occs[q["occluder"]]}, q, ())
    if kind == "blocked":
        return _hidden(occs, q, (q["targetFacet"],))
    if kind == "visible":
        f = facets[q["targetFacet"]]
        facing = bool(kernel.facing(f["normal"], np.array([q["target"]], float), q["camera"])[0])
        return facing and not _hidden(occs, q, (q["targetFacet"],))
    raise ValueError(f"unknown query kind {kind}")


def test_there_is_a_golden_case_per_wall_kind():
    assert len(CASES) >= 8
    assert sum(len(c["queries"]) for c in CASES.values()) >= 40


@pytest.mark.parametrize("name", sorted(CASES))
def test_occluder_regions(name):
    case = CASES[name]
    occs, _ = _occluders(case)
    assert sorted(case["expect"]["cuts"]) == sorted(occs)
    for fid, expected in case["expect"]["cuts"].items():
        got = sorted(tuple(round(float(x), 3) for x in h) for h in occs[fid].halfplanes)
        want = sorted(tuple(round(float(x), 3) for x in h) for h in expected)
        assert len(got) == len(want), f"{fid}: {got} != {want}"
        for g, w in zip(got, want):
            assert np.allclose(g, w, atol=2e-3), f"{fid}: {got} != {want}"


@pytest.mark.parametrize("name", sorted(CASES))
def test_queries(name):
    case = CASES[name]
    occs, facets = _occluders(case)
    wrong = [f"{q['kind']} #{k}: expected {q['expected']} ({q['why']})"
             for k, q in enumerate(case["queries"]) if _answer(q, occs, facets) != q["expected"]]
    assert not wrong, "\n".join(wrong)
