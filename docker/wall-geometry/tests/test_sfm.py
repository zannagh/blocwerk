"""solve-sfm on synthetic scenes (sfm_scene.py): which planes become facets, the gravity chain (device in all four
holdings -> declared angles -> floor -> camera prior), the scale chain (anchors -> measured distance -> anchor fit
-> estimate), anchors (similarity + plane-ICP, the gate), determinism and the document's shape."""
import copy

import numpy as np
import pytest
import sfm_scene as sc

from computejobs.geometry import check_geometry
from wallgeometry.sfm import solve_sfm_document
from wallgeometry.sfm import anchors as anchoring
from wallgeometry.sfm.gravity import device_up_cam

R0, T0 = sc.rot([0.3, -1, 0.5], 70), np.array([1200.0, -300.0, 800.0])  # true world -> COLMAP frame
NOISE_FLOOR_SIGMA, REFUSED_SIGMA = 16.0, 31.0  # anchor rms ~30 mm (accepted now) and ~45 mm (refused, measures)


@pytest.fixture(scope="module")
def scene(tmp_path_factory):
    rng = np.random.default_rng(11)
    X, holds = sc.surfaces(rng)
    cams = sc.cameras(rng)
    d = sc.write_model(str(tmp_path_factory.mktemp("scene")), cams, X, R0, T0, rng)
    photos = []
    for c in cams:
        if c["role"] != "photo":
            continue
        up = sc.rot(rng.normal(size=3), 0.3) @ (c["R"] @ np.array([0, 0, 1.0]))  # the sensor's own noise
        photos.append({"name": c["stem"], "deviceGravity": list(sc.accelerometer(up, c["holding"])),
                       "holds": sc.detections(c, holds, rng).round(1).tolist()})
    return {"dir": d, "cams": cams, "photos": photos, "rng": rng}


def request(scene, gravity=True, holds=True, **extra):
    photos = [{k: v for k, v in p.items() if (k != "deviceGravity" or gravity) and (k != "holds" or holds)}
              for p in scene["photos"]]
    return {"photos": photos, **extra}


def facets(doc):
    return {s["facets"][0]["id"]: s["facets"][0] for s in doc["segments"]}


def angle(a, b):
    return float(np.degrees(np.arccos(np.clip(abs(np.dot(a, b)), -1, 1))))


def by_tilt(doc):
    """The accepted facets as main / kickboard / side by their measured shape (the frame is the solver's)."""
    f = facets(doc)
    main = f["0"]
    rest = [x for k, x in f.items() if k != "0"]
    side = min(rest, key=lambda x: abs(angle(x["normal"], main["normal"]) - 90))
    kick = min(rest, key=lambda x: abs(angle(x["normal"], main["normal"]) - 45))
    return main, kick, side


@pytest.mark.parametrize("holding", sc.HOLDING)
@pytest.mark.parametrize("roll", [-8.0, 0.0, 11.0])
def test_device_convention_in_every_holding(holding, roll):
    R = sc.rot([1, 0, 0], 25) @ sc.rot([0, 0, 1], roll)  # pitched up at an overhang, a bit rolled
    up = R @ np.array([0, -1.0, 0])  # an upright stored image sees up towards -y
    assert up[1] < 0
    w, h = (3024, 4032) if holding.startswith("portrait") else (4032, 3024)
    assert np.allclose(device_up_cam(sc.accelerometer(up, holding), w, h), up / np.linalg.norm(up), atol=1e-12)


def test_wall_from_features_with_device_gravity(scene):
    doc, sol = solve_sfm_document(request(scene), scene["dir"])
    w = doc["world"]
    assert (w["frameSource"], w["gravitySource"], w["scaleSource"]) == ("features", "device", "estimate")
    assert w["gravityKnown"] and not w["scaleKnown"] and not w["anchored"] and doc["markers"] == []
    rows = doc["quality"]["sfm"]["planes"]
    assert len(doc["segments"]) == 3, [(r["reason"], r["tiltDeg"], r["areaM2"]) for r in rows]
    main, kick, side = by_tilt(doc)
    assert main["measuredAngleDeg"] == pytest.approx(45.0, abs=0.4)
    assert kick["measuredAngleDeg"] == pytest.approx(0.0, abs=0.6) and side["measuredAngleDeg"] == pytest.approx(0, abs=0.6)
    assert angle(main["normal"], side["normal"]) == pytest.approx(90, abs=0.5)
    reasons = {r["reason"] for r in rows if not r["accepted"]}
    assert {"horizontal", "no holds"} <= reasons  # the floor, the rafters
    assert any(r["reason"] in ("feature on a bigger facet", "small") and abs(r["tiltDeg"] - 45) < 3 for r in rows)
    assert 0.9 < sol["s"] / sc.S0 < 1.1  # median photo height 1.45 m = the prior
    ext = main["extentMm"]
    assert 3400 < ext["aMax"] < 4600 and 3000 < ext["bMax"] < 4000 and main["origin"] == [0, 0, 0]
    assert kick["extentMm"]["bMax"] < 450  # clipped at the fold with the main wall
    assert doc["quality"]["gravityDetail"]["device"]["photos"] == 24
    check_geometry(doc, textures=True)
    assert [c["image"] for c in doc["cameras"]] == sorted(p["name"] for p in scene["photos"])


def test_same_input_same_document(scene):
    a, _ = solve_sfm_document(request(scene), scene["dir"])
    b, _ = solve_sfm_document(copy.deepcopy(request(scene)), scene["dir"])
    assert a == b


def test_declared_angles_give_gravity_without_the_sensor(scene):
    segs = [{"index": 0, "name": "main", "declaredAngleDeg": 45}, {"index": 1, "declaredAngleDeg": 0,
                                                                     "verticalReference": True}]
    doc, _ = solve_sfm_document(request(scene, gravity=False, segments=segs), scene["dir"])
    assert doc["world"]["gravitySource"] == "declared"
    main, kick, side = by_tilt(doc)
    assert main["measuredAngleDeg"] == pytest.approx(45.0, abs=0.5) and side["measuredAngleDeg"] == pytest.approx(0, abs=0.7)


def test_floor_then_camera_prior(scene, tmp_path):
    doc, _ = solve_sfm_document(request(scene, gravity=False), scene["dir"])
    assert doc["world"]["gravitySource"] == "floor" and doc["world"]["gravityKnown"]
    assert by_tilt(doc)[0]["measuredAngleDeg"] == pytest.approx(45.0, abs=1.5)
    rng = np.random.default_rng(5)
    X, _ = sc.surfaces(rng, floor=False)
    d = sc.write_model(str(tmp_path / "nofloor"), scene["cams"], X, R0, T0, rng)
    doc, sol = solve_sfm_document(request(scene, gravity=False), d)
    w = doc["world"]
    assert (w["gravitySource"], w["gravityKnown"], w["scaleSource"]) == ("cameras", False, "estimate")
    assert all(f["measuredAngleDeg"] is None for f in facets(doc).values())
    assert sol["scale"]["method"] == "nearest camera distance", sol["scale"]
    assert any("gravity unknown" in x for x in doc["quality"]["checks"]["warnings"])


def test_a_measured_distance_sets_the_scale(scene):
    A, B = sc.MAIN_O + 1200 * sc.MAIN_U + 900 * sc.MAIN_V, sc.MAIN_O + 2600 * sc.MAIN_U + 1700 * sc.MAIN_V
    cam = next(c for c in scene["cams"] if c["role"] == "photo" and len(sc.detections(c, np.array([A, B]), scene["rng"])) == 2)
    px = sc.detections(cam, np.array([A, B]), scene["rng"], sigma=0.0)
    m = {"photo": cam["stem"], "a": px[0].tolist(), "b": px[1].tolist(), "mm": float(np.linalg.norm(A - B))}
    doc, sol = solve_sfm_document(request(scene, measuredDistance=m), scene["dir"])
    assert doc["world"]["scaleSource"] == "measured" and doc["world"]["scaleKnown"]
    assert sol["s"] == pytest.approx(sc.S0, rel=0.01)


def anchored(scene, anchors=None, shift=None):
    ref = sc.reference(scene["cams"])
    if shift:
        for c in ref["cameras"]:
            if c["image"] == shift:
                R = np.array(c["R"]).reshape(3, 3)
                c["t"] = list(np.array(c["t"]) - R @ np.array([0.0, 400, 0]))  # this anchor 400 mm off
    amap = {c["stem"]: f"p9{c['stem'][1:]}" for c in scene["cams"] if c["role"] == "anchor"}
    if anchors is not None:
        amap = dict(list(amap.items())[:anchors])
    return request(scene, anchors=amap, reference=ref)


def test_anchors_carry_the_reference_frame(scene):
    doc, sol = solve_sfm_document(anchored(scene, shift="p903"), scene["dir"])
    w, a = doc["world"], doc["quality"]["sfm"]["anchors"]
    assert w["anchored"], str(a)
    assert w["anchored"] and (w["gravitySource"], w["scaleSource"]) == ("anchors", "anchors") and w["scaleKnown"]
    assert a["ok"] and a["outliers"] == ["a03"] and a["residualsMm"]["a03"] > 300 and a["rmsMm"] < 5
    assert a["icp"]["shiftMm"] < 10 and sol["s"] == pytest.approx(sc.S0, rel=0.002)
    main, kick, side = by_tilt(doc)
    assert angle(main["normal"], sc.MAIN_N) < 0.3 and angle(side["normal"], sc.SIDE_N) < 0.5
    o = np.array(main["origin"])  # the facet in the reference world: on the true plane
    assert abs((o - sc.MAIN_O) @ sc.MAIN_N) < 10


def test_too_few_anchors_fall_back_to_features(scene):
    doc, _ = solve_sfm_document(anchored(scene, anchors=4), scene["dir"])
    a = doc["quality"]["sfm"]["anchors"]
    assert not a["ok"] and "need 6" in a["reason"] and not doc["world"]["anchored"]
    assert doc["world"]["gravitySource"] == "device" and doc["world"]["scaleSource"] == "estimate"
    assert any("anchors not used" in x for x in doc["quality"]["checks"]["warnings"])


def noisy(scene, sigma, seed):
    """Anchors whose reference camera centres disagree with the model by N(0, sigma) mm per axis."""
    req = anchored(scene)
    rng = np.random.default_rng(seed)
    for c in req["reference"]["cameras"]:
        R = np.array(c["R"]).reshape(3, 3)
        c["t"] = list(np.array(c["t"]) - R @ rng.normal(0, sigma, 3))
    return req


def test_the_gate_is_35_mm_rms_and_80_mm_each():
    assert (anchoring.MAX_RMS_MM, anchoring.MAX_SINGLE_MM, anchoring.SCALE_RMS_MM) == (35.0, 80.0, 60.0)


def test_anchors_at_the_solver_noise_floor_are_accepted(scene):
    doc, sol = solve_sfm_document(noisy(scene, NOISE_FLOOR_SIGMA, seed=3), scene["dir"])
    a = doc["quality"]["sfm"]["anchors"]
    assert 25 < a["rmsMm"] <= 35 and a["maxMm"] <= 80, str(a)  # refused by the old 25 / 60 mm gate
    assert a["ok"] and doc["world"]["anchored"] and doc["world"]["scaleSource"] == "anchors"


def test_a_refused_anchor_fit_still_gives_scale_and_gravity(scene):
    doc, sol = solve_sfm_document(noisy(scene, REFUSED_SIGMA, seed=3), scene["dir"])
    w, a = doc["world"], doc["quality"]["sfm"]["anchors"]
    assert 35 < a["rmsMm"] <= 60 and not a["ok"] and a["usedFor"] == "scale and gravity", str(a)
    assert not w["anchored"] and (w["scaleSource"], w["gravitySource"]) == ("anchor-fit", "anchor-fit")
    assert w["scaleKnown"] and w["gravityKnown"] and doc["quality"]["sfm"]["scale"]["rmsMm"] == a["rmsMm"]
    assert sol["s"] == pytest.approx(sc.S0, rel=0.02)
    assert facets(doc)["0"]["measuredAngleDeg"] == pytest.approx(45.0, abs=0.6)
    assert any("anchors not used for the frame" in x for x in doc["quality"]["checks"]["warnings"])


def test_an_anchor_fit_beyond_60_mm_falls_back_to_the_estimate(scene):
    doc, _ = solve_sfm_document(noisy(scene, 70.0, seed=3), scene["dir"])
    a = doc["quality"]["sfm"]["anchors"]
    assert a["rmsMm"] > 60 and not a.get("scaleOk") and "usedFor" not in a, str(a)
    assert (doc["world"]["scaleSource"], doc["world"]["gravitySource"]) == ("estimate", "device")


def test_without_hold_detections_facing_and_area_decide(scene):
    doc, _ = solve_sfm_document(request(scene, holds=False), scene["dir"])
    main = facets(doc)["0"]
    assert main["measuredAngleDeg"] == pytest.approx(45.0, abs=0.4)
    assert all(r["holdHitShare"] is None for r in doc["quality"]["sfm"]["planes"])
