"""A feature-frame (SfM) facet keeps its fold clip: the clipped polygon is exported as `outlineMm`, so the occlusion
cuts a triangle panel to its real half although the model has no markers (review F3, docs/geometry-kernel.md)."""
import numpy as np

from wallgeometry import occlusion
from wallgeometry.sfm.export import _facet_doc
from wallgeometry.sfm.world import extents

R = 1 / np.sqrt(2)
ANG = {"tiltDeg": 0.0, "yawDeg": 0.0, "angleToReferenceDeg": 0.0}


def _plane(P, n, u, v):
    c = P.mean(0)
    return {"P": P, "n": np.array(n, float), "u": np.array(u, float), "v": np.array(v, float), "c": c,
            "ab": np.stack([(P - c) @ u, (P - c) @ v], 1)}


def _scene():
    xs, ts = np.meshgrid(np.linspace(0, 3000, 61), np.linspace(0, 1500, 31))
    overhang = np.stack([xs.ravel(), -ts.ravel(), ts.ravel()], 1)  # hinged at the floor, 45 deg towards -y
    ys, zs = np.meshgrid(np.linspace(-1500, 0, 61), np.linspace(0, 1500, 61))
    keep = zs.ravel() <= -ys.ravel()  # the side wall under it: a right triangle
    side = np.stack([np.zeros(keep.sum()), ys.ravel()[keep], zs.ravel()[keep]], 1)
    return [_plane(overhang, [0, -R, -R], [1, 0, 0], [0, -R, R]), _plane(side, [1, 0, 0], [0, 1, 0], [0, 0, 1])]


def _doc(F, fid):
    F["id"] = fid
    return _facet_doc(F, ANG, True)


def test_the_fold_clipped_triangle_is_exported_as_its_outline():
    overhang, side = extents(_scene())
    assert "outline" not in overhang  # the clip with the side wall cut nothing off its box
    doc = _doc(side, "2")
    e = doc["extentMm"]
    assert e["aMin"] == 0 and e["bMin"] == 0 and 1300 < e["aMax"] < 1500 and 1300 < e["bMax"] < 1500
    # the 1-99 % box with its far corner cut off along the hypotenuse (a + b = const)
    off_box = [p for p in doc["outlineMm"] if min(abs(p[0]), abs(p[0] - e["aMax"])) > 1
               and min(abs(p[1]), abs(p[1] - e["bMax"])) > 1]
    assert len(doc["outlineMm"]) >= 3 and not off_box
    assert not any(p[0] > e["aMax"] - 1 and p[1] > e["bMax"] - 1 for p in doc["outlineMm"])
    assert all(e["aMin"] - 1 <= a <= e["aMax"] + 1 and e["bMin"] - 1 <= b <= e["bMax"] + 1 for a, b in doc["outlineMm"])


def test_without_markers_the_outline_cuts_the_occluder_to_the_real_half():
    overhang, side = extents(_scene())
    facets = [_doc(overhang, "0"), _doc(side, "2")]
    occ = {o.id: o for o in occlusion.occluders(facets, [])}["2"]
    assert len(occ.halfplanes) == 1  # the hypotenuse; the box's own sides are not repeated
    a, b = np.array([300.0, 1200.0]), np.array([300.0, 1200.0])
    assert occ.contains(a, b).tolist() == [True, False]
    # a camera looking through the cut-away corner at the far side is not blocked by the side wall
    assert not occlusion.hidden(np.array([500.0, -300, 1100]), [occ], np.array([[-500.0, -300, 1100]]))[0]
    assert occlusion.hidden(np.array([500.0, -1200, 200]), [occ], np.array([[-500.0, -1200, 200]]))[0]
