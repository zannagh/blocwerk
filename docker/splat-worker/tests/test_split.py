"""The split for 3D runners: splat-prepare's bundle, the runner-side unpack, and splat-finish."""
import io
import json
import os
import zipfile

import numpy as np
import pytest
from test_api import client, form, jpeg, three, wait  # noqa: F401 - the module-scoped client fixture

from computejobs.child import JobError
from computejobs.settings import settings
from splatworker import bundle, finish, main, profiles
from splatworker.options import SplatOptions
from splatworker.splatio import Splats, read_spz, write_slim_ply, write_spz


def make_dataset(root, photo):
    ds = root / "dataset"
    (ds / "images" / "geo_x").mkdir(parents=True)
    (ds / "sparse" / "0").mkdir(parents=True)
    (ds / "images" / "geo_x" / "p01.jpg").write_bytes(photo)
    (ds / "images" / "geo_x" / "p02.jpg").write_bytes(jpeg())
    for n in ("cameras.bin", "images.bin", "points3D.bin"):
        (ds / "sparse" / "0" / n).write_bytes(b"\x01" * 32)
    (ds / "stereo").mkdir()
    (ds / "run-colmap-geometric.sh").write_text("#")
    return ds


def test_bundle_has_only_the_layout_and_no_metadata(tmp_path, gps_jpeg):
    assert bundle.jpeg_has_metadata(gps_jpeg)
    ds = make_dataset(tmp_path, gps_jpeg)
    prof = profiles.resolve("high", 700, 1000)
    info = bundle.build_bundle(str(ds), str(tmp_path / "b.zip"), bundle.train_doc(prof), lambda *a: None)
    with zipfile.ZipFile(tmp_path / "b.zip") as z:
        names = sorted(z.namelist())
        assert names == ["dataset/images/geo_x/p01.jpg", "dataset/images/geo_x/p02.jpg",
                         "dataset/sparse/0/cameras.bin", "dataset/sparse/0/images.bin",
                         "dataset/sparse/0/points3D.bin", "train.json"]
        for n in names[:2]:
            data = z.read(n)
            assert not bundle.jpeg_has_metadata(data) and b"TestPhone" not in data and b"GPS" not in data
    assert info["images"] == 2
    dataset, back = bundle.extract_bundle(str(tmp_path / "b.zip"), str(tmp_path / "out"))
    assert back == prof and os.path.isfile(os.path.join(dataset, "sparse", "0", "points3D.bin"))


def test_strip_keeps_the_pixels(gps_jpeg):
    from PIL import Image
    clean = bundle.strip_jpeg_metadata(gps_jpeg)
    a, b = Image.open(io.BytesIO(gps_jpeg)), Image.open(io.BytesIO(clean))
    assert a.size == b.size and np.array_equal(np.asarray(a), np.asarray(b))
    assert not b.info.get("exif") and not b.info.get("icc_profile")


@pytest.mark.parametrize("name", ["../evil.txt", "/abs/x", "dataset/../../x", "dataset/images/..\\x",
                                  "other/file.bin", "dataset/extra.sh"])
def test_extract_refuses_unsafe_or_unexpected_entries(tmp_path, name):
    with zipfile.ZipFile(tmp_path / "b.zip", "w") as z:
        z.writestr("train.json", json.dumps(bundle.train_doc(profiles.PROFILES["draft"])))
        z.writestr(name, b"x")
    with pytest.raises(bundle.BundleError):
        bundle.extract_bundle(str(tmp_path / "b.zip"), str(tmp_path / "out"))
    assert not (tmp_path / "evil.txt").exists()


def test_extract_refuses_a_bad_profile_and_a_zip_bomb(tmp_path):
    with zipfile.ZipFile(tmp_path / "b.zip", "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("train.json", json.dumps({**bundle.train_doc(profiles.PROFILES["draft"]), "steps": 10 ** 9}))
        z.writestr("dataset/images/g/a.jpg", b"\0" * 5000)
        z.writestr("dataset/sparse/0/cameras.bin", b"x")
    with pytest.raises(bundle.BundleError, match="out of range"):
        bundle.extract_bundle(str(tmp_path / "b.zip"), str(tmp_path / "o1"))
    with pytest.raises(bundle.BundleError, match="too large"):
        bundle.extract_bundle(str(tmp_path / "b.zip"), str(tmp_path / "o2"), max_bytes=1000)


def cols(n=400, seed=3):
    rng = np.random.default_rng(seed)
    c = {k: rng.normal(size=n).astype("<f4") for k in ("x", "y", "z", "f_dc_0", "f_dc_1", "f_dc_2", "opacity",
                                                       "rot_0", "rot_1", "rot_2", "rot_3")}
    for i in range(3):
        c[f"scale_{i}"] = (rng.normal(size=n) - 4).astype("<f4")
    c["opacity"] = np.full(n, 3.0, "<f4")
    return c


STATE = {"version": 1, "options": SplatOptions().to_dict(), "geometry": None, "photoCentres": {},
         "registered": ["p01", "p02", "vf_0001"], "photoStems": ["p01", "p02", "p03"], "frameStems": ["vf_0001"],
         "points": 1200, "meanReprojErrorPx": 0.7, "matcher": "exhaustive", "cameraGroups": 1,
         "sfm": {"memoryBudgetMb": 6000, "memoryRetries": []}, "stageSeconds": {"ingest": 1.0}}


def test_finish_exports_like_the_all_in_one_job(tmp_path):
    write_slim_ply(cols(), str(tmp_path / "splat.ply"))
    stats_in = {"steps": 5000, "quality": "draft", "trainingSeconds": 42.0, "runnerGpu": "Apple M4",
                "retries": [{"stage": "train", "reason": "memory"}], "evil": {"x": 1}}
    res = finish.finish(str(tmp_path), lambda *a: None, STATE, str(tmp_path / "splat.ply"),
                        finish.clean_train_stats(stats_in))
    assert res["files"] == ["wall.splat", "wall.spz", "frame.json"]
    st = res["stats"]
    assert st["unregistered"] == ["p03"] and st["videoFramesRegistered"] == 1 and st["runnerGpu"] == "Apple M4"
    assert st["trainingSeconds"] == 42.0 and st["stageSeconds"]["train"] == 42.0 and "evil" not in st
    assert st["memoryRetries"] == [{"stage": "train", "reason": "memory"}] and st["splatsTrained"] == 400
    frame = json.load(open(tmp_path / "frame.json"))
    assert frame["aligned"] is False and frame["stats"]["splatCount"] == st["splatCount"] > 0
    assert read_spz(open(tmp_path / "wall.spz", "rb").read())["xyz"].shape[0] == st["splatCount"]


def test_finish_reads_an_spz_upload_and_refuses_garbage(tmp_path):
    s = Splats.from_ply(cols())
    write_spz(s, str(tmp_path / "splat.spz"))
    back = finish.load_splats(str(tmp_path / "splat.spz"))
    assert len(back) == 400 and np.allclose(back.xyz, s.xyz, atol=1e-3)
    assert np.allclose(np.linalg.norm(back.rot, axis=1), 1.0)
    (tmp_path / "junk.ply").write_bytes(b"<html>")
    with pytest.raises(JobError, match="neither"):
        finish.load_splats(str(tmp_path / "junk.ply"))


def finish_form(prepared, name="splat.ply", data=b"ply\n", stats=None):
    files = [("prepared", (None, json.dumps(prepared))), ("splat", (name, data, "application/octet-stream"))]
    if stats is not None:
        files.append(("trainStats", (None, json.dumps(stats))))
    return files


def test_finish_upload_stores_the_parts(client):  # noqa: F811
    r = client.post("/v1/jobs/splat-finish", files=finish_form(STATE, stats={"steps": 1}))
    assert r.status_code == 202, r.text
    job = r.json()["jobId"]
    assert wait(client, job)["status"] == "succeeded"
    d = os.path.join(settings.work_dir, job)
    assert open(os.path.join(d, "splat.ply"), "rb").read() == b"ply\n"
    assert json.load(open(os.path.join(d, "prepared.json")))["points"] == 1200
    assert json.load(open(os.path.join(d, "trainStats.json"))) == {"steps": 1}


def test_finish_upload_validation(client, monkeypatch):  # noqa: F811
    assert client.post("/v1/jobs/splat-finish", files=finish_form({"version": 2})).status_code == 422
    assert client.post("/v1/jobs/splat-finish", files=finish_form(STATE, name="x.exe")).status_code == 400
    assert client.post("/v1/jobs/splat-finish", files=[("prepared", (None, json.dumps(STATE)))]).status_code == 422
    monkeypatch.setattr(settings, "max_result_bytes", 10)
    assert client.post("/v1/jobs/splat-finish", files=finish_form(STATE, data=b"x" * 100)).status_code == 413


def test_cpu_mode_refuses_all_in_one_but_prepares(client, monkeypatch):  # noqa: F811
    monkeypatch.setattr(settings, "worker_mode", "cpu")
    monkeypatch.setitem(main.TOOLS, "brush", {"version": None})
    r = client.post("/v1/jobs/splat", files=form(three()))
    assert r.status_code == 503 and "cpu mode" in r.text
    r = client.post("/v1/jobs/splat-prepare", files=form(three()))
    assert r.status_code == 202, r.text


def test_run_prepare_leaves_only_the_bundle_and_the_state(tmp_path, monkeypatch):
    from test_pipeline import FakeColmap

    from splatworker import pipeline, sfm

    class Undistorting(FakeColmap):
        def undistort(self, image_dir, model_dir, out_dir, max_size, report):
            os.makedirs(os.path.join(out_dir, "images", "g"))
            os.makedirs(os.path.join(out_dir, "sparse", "0"))
            for i in range(3):
                with open(os.path.join(out_dir, "images", "g", f"IMG_{i}.jpg"), "wb") as fh:
                    fh.write(jpeg())
            with open(os.path.join(out_dir, "sparse", "0", "cameras.bin"), "wb") as fh:
                fh.write(b"\0" * 8)

    monkeypatch.setattr(sfm, "Colmap", Undistorting)
    (tmp_path / "arrived").mkdir()
    for i in range(3):
        (tmp_path / "arrived" / f"IMG_{i}.jpg").write_bytes(jpeg())
    (tmp_path / "inputs.json").write_text(json.dumps({
        "photos": {f"IMG_{i}": {} for i in range(3)}, "options": SplatOptions(quality="draft").to_dict()}))
    progress = []
    res = pipeline.run_prepare(str(tmp_path), lambda *a: progress.append(a))
    assert res["files"] == ["bundle.zip", "prepared.json"] and res["quality"] == "draft"
    assert sorted(os.listdir(tmp_path)) == ["bundle.zip", "prepared.json", "tools.log"]
    state = json.load(open(tmp_path / "prepared.json"))
    assert sorted(state["photoCentres"]) == ["IMG_0", "IMG_1", "IMG_2"] and state["geometry"] is None
    fr = [p[0] for p in progress]
    assert fr == sorted(fr) and fr[-1] <= 1.0 and progress[-1][1] == "bundle"
    _, prof = bundle.extract_bundle(str(tmp_path / "bundle.zip"), str(tmp_path / "x"))
    assert prof == profiles.PROFILES["draft"]
