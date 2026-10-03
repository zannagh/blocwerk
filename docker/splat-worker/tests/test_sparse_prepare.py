"""splat-prepare's sparse.zip (the distorted model rescaled to the stored photos) and anchor photos (matched
and mapped, their centres kept, removed before undistortion and the bundle)."""
import io
import json
import os
import zipfile

import numpy as np
import pytest
from PIL import Image
from sparse_fixture import FakeColmap, project, synthetic_model
from test_api import client, form, jpeg, three, wait  # noqa: F401 - the module-scoped client fixture

from computejobs.settings import settings
from splatworker import colmap, prepare, sfm, split_api
from splatworker.options import SplatOptions
from splatworker.sparse_export import export_sparse, read_cameras, read_images, read_points

NAMES = ["g_land/p00.jpg", "g_land/p01.jpg", "g_land/a00.jpg", "video/vf_0001.jpg"]
SMALL = {"g_land/p00.jpg": (400, 300), "g_land/p01.jpg": (400, 300), "g_land/a00.jpg": (400, 300),
         "video/vf_0001.jpg": (300, 400)}
STORED = {"p00": (1600, 1200), "p01": (1600, 1200), "a00": (1600, 1200), "vf_0001": (1080, 1440)}


def role(stem):
    return "anchor" if stem.startswith("a") else "frame" if stem.startswith("vf_") else "photo"


def unzip(path, out):
    with zipfile.ZipFile(path) as z:
        assert sorted(z.namelist()) == ["cameras.bin", "images.bin", "points3D.bin", "stems.json"]
        z.extractall(out)
    return (read_cameras(os.path.join(out, "cameras.bin")), read_images(os.path.join(out, "images.bin")),
            read_points(os.path.join(out, "points3D.bin")), json.load(open(os.path.join(out, "stems.json"))))


def test_sparse_zip_is_rescaled_to_the_stored_photos_and_compact(tmp_path):
    synthetic_model(str(tmp_path / "m"), NAMES, SMALL)
    info = export_sparse(str(tmp_path / "m"), str(tmp_path / "sparse.zip"), STORED, role, str(tmp_path / "w"))
    assert info["images"] == 4 and info["points"] == 30
    cams, images, points, stems = unzip(tmp_path / "sparse.zip", tmp_path / "out")
    assert sorted((w, h) for _, w, h, _ in cams.values()) == [(1080, 1440), (1600, 1200)]
    land = next(p for m, w, h, p in cams.values() if w == 1600)
    assert land == pytest.approx([0.9 * 400 * 4, 800, 600, 0.05, -0.01])  # focal + principal point x4, k unchanged
    xyz = {pid: np.array(x) for pid, x, _, _, _ in points}
    for im in images:
        _, w, h, params = cams[im["cam"]]
        assert (w, h) == STORED[os.path.splitext(os.path.basename(im["name"]))[0]]
        obs = im["obs"]
        assert len(obs) == 30 and (obs["p"] > 0).all()  # the 5 keypoints without a 3D point are gone
        want = project(params, np.array([xyz[p] for p in obs["p"]]) + np.array(im["t"]))
        assert np.allclose(np.c_[obs["x"], obs["y"]], want, atol=1e-6)  # the stored photo's pixels
    by_id = {im["id"]: im for im in images}
    for pid, _, _, _, track in points:  # every track element points at its own observation again
        assert len(track) == 4 and all(by_id[i]["obs"]["p"][j] == pid for i, j in track)
    assert stems["version"] == 1 and stems["images"]["g_land/a00.jpg"] == {
        "stem": "a00", "role": "anchor", "width": 1600, "height": 1200}
    assert stems["images"]["video/vf_0001.jpg"]["role"] == "frame"


def test_image_deleter_arguments(tmp_path, monkeypatch):
    runs = []
    cm = colmap.Colmap("colmap", str(tmp_path / "log"), str(tmp_path), options=set())
    monkeypatch.setattr(cm, "_run", lambda stage, args, *a, **k: runs.append((stage, args)))
    cm.delete_images("in", str(tmp_path / "out"), ["g/a00.jpg", "g/a01.jpg"])
    stage, args = runs[0]
    assert stage == "sfm-mapping" and args[:5] == ["image_deleter", "--input_path", "in", "--output_path",
                                                   str(tmp_path / "out")]
    assert open(args[args.index("--image_names_path") + 1]).read().split() == ["g/a00.jpg", "g/a01.jpg"]


def make_job(tmp_path, stems, anchors):
    (tmp_path / "arrived").mkdir()
    photos = {}
    for s in stems:
        Image.new("RGB", (80, 60), (40 + 10 * len(photos), 90, 30)).save(tmp_path / "arrived" / f"{s}.jpg")
        photos[s] = {"width": 80, "height": 60, "storedWidth": 1600, "storedHeight": 1200, "focal35": 24.0,
                     "lensKey": "k"}
    (tmp_path / "inputs.json").write_text(json.dumps({"photos": photos, "anchors": anchors,
                                                      "options": SplatOptions().to_dict()}))


def test_prepare_keeps_anchor_centres_and_never_trains_on_anchors(tmp_path, monkeypatch):
    monkeypatch.setattr(sfm, "Colmap", FakeColmap)
    FakeColmap.calls = []
    make_job(tmp_path, ["p00", "p01", "p02", "a00", "a01", "vf_0001"], ["a00", "a01"])
    res = prepare.run_prepare(str(tmp_path), lambda *a: None)
    assert res["files"] == ["bundle.zip", "prepared.json", "sparse.zip"]
    assert sorted(n for n in os.listdir(tmp_path) if not n.endswith(".log")) == [
        "bundle.zip", "prepared.json", "sparse.zip"]
    deletes = [c for c in FakeColmap.calls if isinstance(c, tuple)]
    assert len(deletes) == 1 and [os.path.basename(n) for n in deletes[0][1]] == ["a00.jpg", "a01.jpg"]
    assert FakeColmap.calls.index(deletes[0]) < FakeColmap.calls.index("undistort")  # removed BEFORE undistort
    state = json.load(open(tmp_path / "prepared.json"))
    assert sorted(state["anchorCentres"]) == ["a00", "a01"] and state["anchorStems"] == ["a00", "a01"]
    assert sorted(state["photoCentres"]) == ["p00", "p01", "p02"] and state["photoStems"] == ["p00", "p01", "p02"]
    assert res["stats"]["photos"] == 3 and res["stats"]["anchorsRegistered"] == 2
    with zipfile.ZipFile(tmp_path / "bundle.zip") as z:
        imgs = [n for n in z.namelist() if n.startswith("dataset/images/")]
        assert len(imgs) == 4 and not any(os.path.basename(n).startswith("a0") for n in imgs)
    _, images, _, stems = unzip(tmp_path / "sparse.zip", tmp_path / "unz")
    assert len(images) == 6 and sorted(v["stem"] for v in stems["images"].values() if v["role"] == "anchor") == \
        ["a00", "a01"]
    assert all((v["width"], v["height"]) == (1600, 1200) for v in stems["images"].values())
    split_api.check_prepared(state)  # the finish accepts it


def test_prepare_without_anchors_deletes_nothing(tmp_path, monkeypatch):
    monkeypatch.setattr(sfm, "Colmap", FakeColmap)
    FakeColmap.calls = []
    make_job(tmp_path, ["p00", "p01", "p02"], [])
    prepare.run_prepare(str(tmp_path), lambda *a: None)
    assert not any(isinstance(c, tuple) for c in FakeColmap.calls)
    assert json.load(open(tmp_path / "prepared.json"))["anchorCentres"] == {}


def test_check_prepared_validates_anchor_centres():
    from test_split import STATE
    split_api.check_prepared(STATE)  # older prepare jobs have no anchorCentres
    split_api.check_prepared({**STATE, "anchorCentres": {"a00": [0, 1, 2]}})
    with pytest.raises(Exception) as e:
        split_api.check_prepared({**STATE, "anchorCentres": {"a00": "x"}})
    assert getattr(e.value, "status_code", None) == 422


def test_anchor_photos_are_for_splat_prepare_only(client):  # noqa: F811
    photos = three() + [("a00.jpg", jpeg()), ("a01.jpg", jpeg())]
    r = client.post("/v1/jobs/splat", files=form(photos))
    assert r.status_code == 422 and "splat-prepare" in r.text
    r = client.post("/v1/jobs/splat-prepare", files=form(photos))
    assert r.status_code == 202, r.text
    job = r.json()["jobId"]
    inputs = json.load(open(os.path.join(settings.work_dir, job, "inputs.json")))
    assert inputs["anchors"] == ["a00", "a01"] and inputs["photos"]["a00"]["storedWidth"] == 40
    wait(client, job)
    # anchors never count towards the photo minimum
    r = client.post("/v1/jobs/splat-prepare", files=form(three()[:2] + [("a00.jpg", jpeg())]))
    assert r.status_code == 422 and "at least 3 photos" in r.text


def test_sanitize_reports_the_stored_size_before_the_ingest_downscale(gps_jpeg):
    from splatworker.ingest import sanitize
    buf = io.BytesIO()
    Image.new("RGB", (3000, 1000)).save(buf, "JPEG")
    _, facts = sanitize(buf.getvalue(), 1500)
    assert (facts["width"], facts["height"], facts["storedWidth"], facts["storedHeight"]) == (1500, 500, 3000, 1000)
    _, facts = sanitize(gps_jpeg, 4096)  # EXIF orientation 6: the stored size is the turned one
    assert (facts["storedWidth"], facts["storedHeight"]) == (32, 64)
