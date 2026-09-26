"""solve-sfm's geometric sanity (sanity.py) on synthetic scenes: a plane cutting through the wall, a floating
board with a few hold detections, a near-coplanar duplicate of the wall, the room wall behind a panel, and
anchored: a surface the reference model lacks stays only when it carries holds."""
import numpy as np
import pytest
import sfm_scene as sc
from test_sfm import R0, T0, angle, anchored, by_tilt, request

from wallgeometry.sfm import solve_sfm_document
from wallgeometry.sfm import sanity
from wallgeometry.sfm.score import describe


@pytest.fixture(scope="module")
def spurious_scene(tmp_path_factory):
    rng = np.random.default_rng(11)
    X, holds = sc.surfaces(rng)
    cams = sc.cameras(rng)
    Xs, extra = sc.spurious(rng)
    d = sc.write_model(str(tmp_path_factory.mktemp("spurious")), cams, np.vstack([X, Xs]), R0, T0, rng)
    all_holds = np.vstack([holds, extra["cut"], extra["board"]])
    photos = [{"name": c["stem"], "holds": sc.detections(c, all_holds, rng).round(1).tolist()}
              for c in cams if c["role"] == "photo"]
    return {"dir": d, "cams": cams, "photos": photos}


def true_centre(sol, pl):
    """A plane's centroid in the scene's true world (the model is written in X_col = R0^T (X - T0) / S0)."""
    return sol["pts"]["Q"][pl["inl"]].mean(0) * sc.S0 @ R0.T + T0


def plane_near(sol, point, normal, within=700.0):
    """The biggest plane within 10 deg of normal whose centre is within `within` mm of point (true world)."""
    near = [p for p in sol["planes"] if angle(R0 @ p["n"], normal) < 10
            and np.linalg.norm(true_centre(sol, p) - point) < within]
    return max(near, key=lambda p: p["npts"])


def test_a_cutting_plane_and_a_floating_board_are_rejected(spurious_scene):
    for with_holds in (True, False):
        photos = [p if with_holds else {"name": p["name"]} for p in spurious_scene["photos"]]
        doc, sol = solve_sfm_document({"photos": photos}, spurious_scene["dir"])
        cut = plane_near(sol, np.array([1750.0, -1200, 1850]), sc.KICK_N)
        board = plane_near(sol, sc.BOARD_O + np.array([600.0, 0, 500]), sc.KICK_N)
        assert cut["reason"] == "cuts through a bigger facet", cut["reason"]
        if with_holds:  # it clears the 1 % hold share with the main wall's holds behind its front part
            assert 0.01 <= cut["holdHitShare"] < sanity.STRONG_HOLDS and board["holdHitShare"] < sanity.CLEAR_HOLDS
        else:  # facing and area alone would take the board
            assert board["reason"].startswith("floating"), board["reason"]
        assert len(doc["segments"]) == 3, [(r["reason"], r["centreMm"]) for r in doc["quality"]["sfm"]["planes"]]
        main, kick, side = by_tilt(doc)
        assert main["measuredAngleDeg"] == pytest.approx(45.0, abs=0.5)


def test_anchored_a_surface_the_reference_lacks_needs_holds(tmp_path):
    rng = np.random.default_rng(11)
    X, holds = sc.surfaces(rng)
    cams = sc.cameras(rng)
    d = sc.write_model(str(tmp_path / "scene"), cams, X, R0, T0, rng)
    scene = {"dir": d, "cams": cams}
    for accepted in (True, False):  # with the detections (12 of 70 holds on the side panel), without any
        scene["photos"] = [{"name": c["stem"], "holds": sc.detections(c, holds, rng).round(1).tolist()
                            if accepted else None} for c in cams if c["role"] == "photo"]
        doc, sol = solve_sfm_document(anchored(scene), d)  # the reference has no side panel
        assert doc["world"]["anchored"]
        side = plane_near(sol, np.array([0.0, -1200, 700]), sc.SIDE_N)
        rows = {r["referenceFacet"] for r in doc["quality"]["sfm"]["planes"] if r["accepted"]}
        if accepted:
            assert side["accepted"] and side["refFacet"] is None and rows == {"0", "1", None}, (side["reason"], rows)
        else:
            assert side["reason"] == "matches no facet of the active model" and rows == {"0", "1"}


EYE = np.array([2000.0, -4000, 1500])  # the one photo of the hand-made planes


def slab(rng, o, u, v, n, a, b, density=0.002, noise=5.0):
    k = int(density * a * b)
    ab = np.c_[rng.uniform(0, a, k), rng.uniform(0, b, k)]
    return o + ab[:, :1] * u + ab[:, 1:] * v + rng.normal(0, noise, (k, 1)) * n


def planes_of(parts, n_list):
    """Plane dicts (as solve builds them) of point parts, each fitted exactly with its given normal."""
    Y = np.vstack(parts)
    planes, start = [], 0
    for P, n in zip(parts, n_list):
        inl = np.zeros(len(Y), bool)
        inl[start:start + len(P)] = True
        start += len(P)
        planes.append({"c": P.mean(0), "n": np.asarray(n, float), "inl": inl, "accepted": True,
                       "reason": None, "holdHitShare": None})
    describe(planes, Y, [{"C": EYE, "R": sc.look_at(EYE, np.array([2000.0, 0, 1500]))}])
    for p in planes:  # the side facing the photo
        p["n"] = p["n"] if p["n"] @ (EYE - p["c"]) > 0 else -p["n"]
    return Y, planes


def test_the_rules_on_hand_made_planes():
    rng = np.random.default_rng(2)
    wall = slab(rng, np.zeros(3), sc.MAIN_U, np.array([0.0, 0, 1]), sc.KICK_N, 4000, 3000)
    layer = slab(rng, np.array([0.0, -25, 0]), sc.MAIN_U, np.array([0.0, 0, 1]), sc.KICK_N, 3900, 2900, 0.0015)
    cutter = slab(rng, np.array([2000.0, -700, 500]), np.array([0.0, 1, 0]), np.array([0.0, 0, 1]), sc.SIDE_N,
                  1400, 2000)
    fold = slab(rng, np.array([0.0, 0, 3000]), sc.MAIN_U, np.array([0.0, -1, 0]), np.array([0, 0, -1.0]), 4000, 800)
    room = slab(rng, np.array([500.0, 900, 0]), sc.MAIN_U, np.array([0.0, 0, 1]), sc.KICK_N, 3000, 2500)
    normals = [sc.KICK_N, sc.KICK_N, sc.SIDE_N, [0, 0, -1.0], sc.KICK_N]
    Y, (w, lay, cut, fo, rm) = planes_of([wall, layer, cutter, fold, room], normals)
    assert lay["npts"] * 3 > w["npts"]  # too big for score._lies_on's 1/3 rule
    assert sanity.duplicate_of(lay, w, Y) and not sanity.duplicate_of(rm, w, Y)
    assert sanity.cuts(cut, w, Y) and not sanity.cuts(fo, w, Y)  # a fold at the top edge is no cut
    assert sanity.behind(rm, w, Y) and not sanity.behind(lay, w, Y)
    cut["holdHitShare"], cut["holdHits"] = 0.2, 40  # a plane that carries holds stays
    assert not sanity.holds_ok(rm, sanity.CLEAR_HOLDS) and sanity.holds_ok(cut, sanity.STRONG_HOLDS)
    sanity.check([w, lay, cut, fo, rm], Y, np.array([0, 0, 1.0]), EYE[None], 4000.0)
    assert [p["reason"] for p in (w, lay, cut, fo, rm)] == [None, "feature on a bigger facet", None, None,
                                                           "behind a bigger facet"]
