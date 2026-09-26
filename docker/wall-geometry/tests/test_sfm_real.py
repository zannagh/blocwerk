"""Regression on the real Attic: solve-sfm on the 353-image sparse model (53 photos + 300 video frames) against the
marker model. Needs the owner's data folder (BLOCWERK_DATA_DIR, default ~/blocwerk-data: tune/work/sfm/sparse/0 and
markerless/{active_model.json, device_gravity.json, holds.csv, anchors/anchor_list.txt}, run3/); skipped without it.
Nothing of it is committed. Hold detections are the placed holds projected into the photos (8 px noise), as in
Phase 0."""
import json
import os

import numpy as np
import pytest

from wallgeometry.sfm import solve_sfm_document
from wallgeometry.sfm.anchors import robust_umeyama
from wallgeometry.sfm.colmap_io import read_images

DATA = os.environ.get("BLOCWERK_DATA_DIR") or os.path.expanduser("~/blocwerk-data")
SPARSE = os.path.join(DATA, "tune", "work", "sfm", "sparse", "0")
P53 = os.path.join(DATA, "markerless", "anchors", "photos53", "sparse", "0")  # photos only, self-calibrated
ML = os.path.join(DATA, "markerless")
pytestmark = pytest.mark.skipif(not (os.path.isfile(os.path.join(SPARSE, "points3D.bin"))
                                     and os.path.isfile(os.path.join(ML, "device_gravity.json"))),
                                reason="the owner's Attic data is not here")


@pytest.fixture(scope="module")
def attic():
    import cv2
    g = json.load(open(os.path.join(ML, "active_model.json"), encoding="utf-8"))
    facets = {f["id"]: f for s in g["segments"] for f in s["facets"]}
    photos = {}
    for e in json.load(open(os.path.join(ML, "device_gravity.json"))):
        stem = "p%02d" % (int(e["SourceFile"].split("IMG_")[1][:4]) - 2786)  # p01 = IMG_2787
        photos[stem] = {"name": stem, "deviceGravity": [float(x) for x in e["AccelerationVector"].split()]}
    holds = []
    for line in open(os.path.join(ML, "holds.csv")):
        f, a, b, _, _ = line.strip().split(",")
        F = facets[f]
        holds.append(np.array(F["origin"]) + float(a) * np.array(F["u"]) + float(b) * np.array(F["v"]))
    holds, rng = np.array(holds), np.random.default_rng(3)
    for c in g["cameras"]:
        R, t = np.array(c["R"]).reshape(3, 3), np.array(c["t"])
        front = holds @ R.T + t
        front = front[:, 2] > 300
        px = cv2.projectPoints(holds[front], cv2.Rodrigues(R)[0], t, np.array(c["K"]).reshape(3, 3),
                               np.array(c["dist"]))[0].reshape(-1, 2) + rng.normal(0, 8, (front.sum(), 2))
        ok = (px[:, 0] > 0) & (px[:, 0] < c["width"]) & (px[:, 1] > 0) & (px[:, 1] < c["height"])
        if c["image"] in photos:
            photos[c["image"]]["holds"] = px[ok].round(1).tolist()
    return {"model": g, "facets": facets, "photos": list(photos.values()),
            "anchors": open(os.path.join(ML, "anchors", "anchor_list.txt")).read().split()}


def stems(anchors=(), sparse=SPARSE):
    out = {}
    for im in read_images(os.path.join(sparse, "images.bin")):
        stem = os.path.splitext(os.path.basename(im["name"]))[0]
        out[im["name"]] = {"stem": stem, "role": "frame" if stem.startswith("vf_") else
                           "anchor" if stem in anchors else "photo"}
    return out


def centre(c):
    return -np.array(c["R"]).reshape(3, 3).T @ np.array(c["t"])


def angle(a, b):
    return float(np.degrees(np.arccos(np.clip(abs(np.dot(a, b)), -1, 1))))


def test_attic_main_wall_from_features_with_device_gravity(attic):
    doc, _ = solve_sfm_document({"photos": attic["photos"]}, SPARSE, stems=stems())
    main = doc["segments"][0]["facets"][0]
    assert doc["world"]["gravitySource"] == "device" and len(doc["segments"]) == 3
    assert main["measuredAngleDeg"] == pytest.approx(45.2, abs=1.0)  # marker model: 45.18
    marker = {c["image"]: centre(c) for c in attic["model"]["cameras"]}
    ours = {c["image"]: centre(c) for c in doc["cameras"]}
    common = sorted(set(marker) & set(ours))
    _, R, _, _, _ = robust_umeyama(np.array([ours[p] for p in common]), np.array([marker[p] for p in common]))
    assert angle(R @ np.array(main["normal"]), attic["facets"]["0"]["normal"]) < 0.5  # Phase 0: 0.10 deg
    side = min(doc["segments"][1:], key=lambda s: angle(R @ np.array(s["facets"][0]["normal"]),
                                                         attic["facets"]["2"]["normal"]))["facets"][0]
    assert angle(R @ np.array(side["normal"]), attic["facets"]["2"]["normal"]) < 0.5  # Phase 0: 0.17 deg
    assert 0.85 < doc["quality"]["sfm"]["scale"]["mmPerUnit"] / 498.3 < 1.15  # camera-height estimate


def test_attic_anchored_to_the_marker_model(attic):
    anchors = attic["anchors"]
    req = {"photos": attic["photos"], "anchors": {a: a for a in anchors}, "reference": attic["model"]}
    doc, _ = solve_sfm_document(req, SPARSE, stems=stems(anchors))
    a = doc["quality"]["sfm"]["anchors"]
    assert a["ok"] and a["inliers"] >= 12 and a["rmsMm"] <= 25 and set(a["residualsMm"]) == set(anchors)
    assert a["icp"]["applied"] and a["icp"]["rotationDeg"] < 3 and a["icp"]["shiftMm"] < 80
    main = doc["segments"][0]["facets"][0]
    assert angle(main["normal"], attic["facets"]["0"]["normal"]) < 0.5  # in the marker model's own frame
    assert main["measuredAngleDeg"] == pytest.approx(45.2, abs=1.0)


def facet_set(doc, attic):
    """The marker model's facet each accepted facet lies on (the nearest normal; unanchored documents are turned
    into the marker frame by their camera centres)."""
    R = np.eye(3)
    if not doc["world"]["anchored"]:
        marker = {c["image"]: centre(c) for c in attic["model"]["cameras"]}
        ours = {c["image"]: centre(c) for c in doc["cameras"]}
        common = sorted(set(marker) & set(ours))
        _, R, _, _, _ = robust_umeyama(np.array([ours[p] for p in common]), np.array([marker[p] for p in common]))
    out = []
    for s in doc["segments"]:
        n = R @ np.array(s["facets"][0]["normal"])
        out.append(min(attic["facets"], key=lambda k: angle(n, attic["facets"][k]["normal"])))
    return sorted(out)


@pytest.mark.parametrize("sparse", [SPARSE, P53], ids=["T353", "P53"])
@pytest.mark.parametrize("anchored", [True, False], ids=["anchored", "free"])
def test_attic_facets_without_hold_detections_are_the_big_three(attic, sparse, anchored):
    """No detections (the first real markerless run): main wall, side panel and kickboard, nothing spurious (P53's
    room wall 300 mm behind the side panel came through as a 4th facet before the sanity rules)."""
    if not os.path.isfile(os.path.join(sparse, "points3D.bin")):
        pytest.skip("no such model here")
    anchors = attic["anchors"] if anchored else ()
    req = {"photos": [{"name": p["name"]} for p in attic["photos"]]}
    if anchored:
        req.update(anchors={a: a for a in anchors}, reference=attic["model"])
    doc, _ = solve_sfm_document(req, sparse, stems=stems(anchors, sparse))
    assert doc["world"]["anchored"] == anchored
    rows = [(r["reason"], r["centreMm"]) for r in doc["quality"]["sfm"]["planes"]]
    assert facet_set(doc, attic) == ["0", "1b", "2"], rows


RUN3 = os.path.join(ML, "run3")  # the third real markerless capture: its sparse.zip and the app's solve request


@pytest.mark.parametrize("holds", [True, False], ids=["yolo-holds", "no-holds"])
def test_real_markerless_capture_gives_the_big_three(tmp_path, holds):
    """The copy wall's markerless capture (53 photos + frames + 15 anchors, the app's tiled-YOLO detections): main
    wall, side panel and kickboard on their reference facets. Without detections the old rules added two spurious
    near-vertical slabs in front of the main wall (the first real run's "Surface 5 / 6")."""
    import zipfile
    if not os.path.isfile(os.path.join(RUN3, "sparse.zip")):
        pytest.skip("no run-3 capture here")
    zipfile.ZipFile(os.path.join(RUN3, "sparse.zip")).extractall(tmp_path)
    model = next(r for r, _, f in os.walk(tmp_path) if "points3D.bin" in f)
    req = json.load(open(os.path.join(RUN3, "request.json"), encoding="utf-8"))
    if not holds:
        for p in req["photos"]:
            p.pop("holds", None)
    doc, _ = solve_sfm_document(req, model)
    rows = doc["quality"]["sfm"]["planes"]
    assert doc["world"]["anchored"] and doc["world"]["scaleSource"] == "anchors"
    assert sorted(r["referenceFacet"] for r in rows if r["accepted"]) == ["0", "1b", "2"], rows
    assert doc["segments"][0]["facets"][0]["measuredAngleDeg"] == pytest.approx(45.18, abs=0.3)
    if holds:
        assert max(r["holdHitShare"] for r in rows) > 0.5  # the main wall carries most detections
