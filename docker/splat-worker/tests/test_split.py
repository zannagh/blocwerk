"""The split for 3D runners over HTTP and on disk: the bundle (layout, metadata, ultra, zones), the runner-side
unpack, the splat-finish upload, and the cpu-mode worker (no GPU, no trainer)."""
import io
import json
import os
import sys
import zipfile

import numpy as np
import pytest
from test_api import client, form, jpeg, three, wait  # noqa: F401 - the module-scoped client fixture

from computejobs.settings import settings
from splatworker import brush, bundle, colmap, finish, gpu, gsplat_trainer, main, profiles
from splatworker.options import SplatOptions
from splatworker.slimply import spz_columns, write_slim_ply
from splatworker.splatio import Splats, read_ply, write_spz

ZONES = {"version": 1, "facets": [{"id": "0", "o": [0, 0, 0], "u": [1, 0, 0], "v": [0, 0, 1], "n": [0, -1, 0],
                                   "ext": [0, 1, 0, 1]}], "floorMm": 0, "boxLo": [0, 0, 0], "boxHi": [1, 1, 1],
         "params": {}, "toWorldMm": np.eye(4).tolist()}


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
        doc = json.loads(z.read("train.json"))
    # the server's RunnerTrainOptions: known keys only, numbers or short strings
    assert set(doc) == {"version", *bundle.PROFILE_KEYS} and all(isinstance(v, (int, float, str)) for v in doc.values())
    assert info["images"] == 2 and info["zones"] is False
    dataset, back, zones = bundle.extract_bundle(str(tmp_path / "b.zip"), str(tmp_path / "out"))
    assert back == prof and zones is None and os.path.isfile(os.path.join(dataset, "sparse", "0", "points3D.bin"))


def test_an_ultra_bundle_with_zones_round_trips(tmp_path, gps_jpeg):
    ds = make_dataset(tmp_path, gps_jpeg)
    prof = profiles.PROFILES["ultra"]
    marked = {**ZONES, "params": {bundle.OPT_IN: True}}  # zone_run.write_zones: the job opted in
    info = bundle.build_bundle(str(ds), str(tmp_path / "b.zip"), bundle.train_doc(prof), lambda *a: None, marked)
    assert info["zones"] is True
    _, back, zones = bundle.extract_bundle(str(tmp_path / "b.zip"), str(tmp_path / "out"))
    assert back == prof and back.edge == 4096 and json.load(open(zones)) == marked


def test_a_runner_ignores_zones_the_job_did_not_opt_into(tmp_path, gps_jpeg, monkeypatch):
    ds = make_dataset(tmp_path, gps_jpeg)
    bundle.build_bundle(str(ds), str(tmp_path / "b.zip"), bundle.train_doc(profiles.PROFILES["ultra"]),
                        lambda *a: None, ZONES)  # an older prepare: zones.json with every wall geometry
    monkeypatch.setattr(settings, "wall_zones", False)
    assert bundle.extract_bundle(str(tmp_path / "b.zip"), str(tmp_path / "o1"))[2] is None  # plain by default
    monkeypatch.setattr(settings, "wall_zones", True)  # the runner's own SPLAT_WALL_ZONES=1
    assert bundle.extract_bundle(str(tmp_path / "b.zip"), str(tmp_path / "o2"))[2].endswith("zones.json")


def test_strip_keeps_the_pixels(gps_jpeg):
    from PIL import Image
    clean = bundle.strip_jpeg_metadata(gps_jpeg)
    a, b = Image.open(io.BytesIO(gps_jpeg)), Image.open(io.BytesIO(clean))
    assert a.size == b.size and np.array_equal(np.asarray(a), np.asarray(b))
    assert not b.info.get("exif") and not b.info.get("icc_profile")


@pytest.mark.parametrize("name", ["../evil.txt", "/abs/x", "dataset/../../x", "dataset/images/..\\x",
                                  "other/file.bin", "dataset/extra.sh", "zones.json/x"])
def test_extract_refuses_unsafe_or_unexpected_entries(tmp_path, name):
    with zipfile.ZipFile(tmp_path / "b.zip", "w") as z:
        z.writestr("train.json", json.dumps(bundle.train_doc(profiles.PROFILES["draft"])))
        z.writestr(name, b"x")
    with pytest.raises(bundle.BundleError):
        bundle.extract_bundle(str(tmp_path / "b.zip"), str(tmp_path / "out"))
    assert not (tmp_path / "evil.txt").exists()


def test_extract_refuses_a_bad_profile_bad_zones_and_a_zip_bomb(tmp_path):
    def write(path, doc, zones=None):
        with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
            z.writestr("train.json", json.dumps(doc))
            z.writestr("dataset/images/g/a.jpg", b"\0" * 5000)
            z.writestr("dataset/sparse/0/cameras.bin", b"x")
            if zones is not None:
                z.writestr("zones.json", zones)

    draft = bundle.train_doc(profiles.PROFILES["draft"])
    write(tmp_path / "a.zip", {**draft, "steps": 10 ** 9})
    with pytest.raises(bundle.BundleError, match="out of range"):
        bundle.extract_bundle(str(tmp_path / "a.zip"), str(tmp_path / "o1"))
    with pytest.raises(bundle.BundleError, match="too large"):
        bundle.extract_bundle(str(tmp_path / "a.zip"), str(tmp_path / "o2"), max_bytes=1000)
    write(tmp_path / "b.zip", draft, json.dumps({"facets": "x"}))
    with pytest.raises(bundle.BundleError, match="zones"):
        bundle.extract_bundle(str(tmp_path / "b.zip"), str(tmp_path / "o3"))


def cols(n=400, seed=3):
    rng = np.random.default_rng(seed)
    c = {k: rng.normal(size=n).astype("<f4") for k in ("x", "y", "z", "f_dc_0", "f_dc_1", "f_dc_2",
                                                       "rot_0", "rot_1", "rot_2", "rot_3")}
    for i in range(3):
        c[f"scale_{i}"] = (rng.normal(size=n) - 4).astype("<f4")
    c["opacity"] = np.full(n, 3.0, "<f4")
    return c


STATE = {"version": 2, "quality": "high", "options": SplatOptions().to_dict(), "geometry": None,
         "photoCentres": {"p01": [0, 0, 0], "p02": [1, 0, 0]}, "frameCentres": {"vf_0001": [0, 1, 0]},
         "photoStems": ["p01", "p02", "p03"], "frameStems": ["vf_0001"], "points": 1200, "meanReprojErrorPx": 0.7,
         "matcher": "exhaustive", "cameraGroups": 1, "sfm": {"memoryBudgetMb": 6000, "memoryRetries": []},
         "stageSeconds": {"ingest": 1.0}, "zones": None}


def test_finish_exports_like_the_all_in_one_job(tmp_path):
    write_slim_ply(cols(), str(tmp_path / "splat.ply"))
    (tmp_path / "prepared.json").write_text(json.dumps(STATE))
    (tmp_path / "trainStats.json").write_text(json.dumps({
        "steps": 5000, "quality": "high", "trainingSeconds": 42.0, "runnerGpu": "Apple M4", "evil": {"x": 1},
        "retries": [{"stage": "train", "reason": "memory"}]}))
    res = finish.run_finish(str(tmp_path), lambda *a: None)
    assert res["files"] == ["wall.splat", "wall.spz", "frame.json"]
    st = res["stats"]
    assert st["unregistered"] == ["p03"] and st["videoFramesRegistered"] == 1 and st["runnerGpu"] == "Apple M4"
    assert st["trainingSeconds"] == 42.0 and st["stageSeconds"]["train"] == 42.0 and "evil" not in st
    assert st["memoryRetries"] == [{"stage": "train", "reason": "memory"}] and st["splatsTrained"] == 400
    assert sorted(os.listdir(tmp_path)) == ["frame.json", "wall.splat", "wall.spz"]
    frame = json.load(open(tmp_path / "frame.json"))
    assert frame["aligned"] is False and frame["stats"]["splatCount"] == st["splatCount"] > 0


def test_finish_reads_an_spz_upload(tmp_path):
    s = Splats.from_ply(cols())
    write_spz(s, str(tmp_path / "splat.spz"))
    back = spz_columns(open(tmp_path / "splat.spz", "rb").read())
    write_slim_ply(back, str(tmp_path / "b.ply"))
    b = Splats.from_ply(read_ply(str(tmp_path / "b.ply")))
    assert len(b) == 400 and np.allclose(b.xyz, s.xyz, atol=1e-3)
    assert np.allclose(b.alpha, s.alpha, atol=0.01) and np.allclose(np.abs((b.rot * s.rot).sum(1)), 1, atol=0.02)
    (tmp_path / "prepared.json").write_text(json.dumps(STATE))
    assert finish.run_finish(str(tmp_path), lambda *a: None)["stats"]["splatsTrained"] == 400


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
    for bad in ({**STATE, "version": 1}, {k: v for k, v in STATE.items() if k != "frameCentres"},
                {**STATE, "photoCentres": {"p01": "x"}}):
        assert client.post("/v1/jobs/splat-finish", files=finish_form(bad)).status_code == 422
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


def test_probe_tools_in_cpu_mode_needs_colmap_only_and_no_gpu(client, monkeypatch):  # noqa: F811
    def never(*a, **k):
        raise AssertionError("cpu mode must not probe a trainer or a GPU")

    monkeypatch.setattr(settings, "worker_mode", "cpu")
    monkeypatch.setattr(settings, "splat_trainer", "gsplat")  # the CUDA image's default: still not probed
    monkeypatch.setattr(settings, "colmap_bin", sys.executable)
    monkeypatch.setattr(colmap, "tool_version", lambda path: "4.2")
    for mod, name in ((gpu, "vram"), (gsplat_trainer, "tool_version"), (brush, "tool_version")):
        monkeypatch.setattr(mod, name, never)
    saved = dict(main.TOOLS)
    main.TOOLS.clear()
    try:
        main._probe_tools()
        assert main.TOOLS["ok"] is True and main.TOOLS["trainer"] is None and main.TOOLS["mode"] == "cpu"
        h = client.get("/health").json()
        assert h["status"] == "ok" and "splat-prepare" in h["kinds"] and "maxQuality" not in h
        monkeypatch.setattr(colmap, "tool_version", lambda path: None)
        main.TOOLS.clear()
        main._probe_tools()
        assert client.get("/health").json()["status"] == "degraded"
    finally:
        main.TOOLS.clear()
        main.TOOLS.update(saved)


def test_probe_tools_reports_ultra_for_gsplat_on_a_12_gb_gpu(monkeypatch):
    monkeypatch.setattr(settings, "worker_mode", "all")
    monkeypatch.setattr(settings, "splat_trainer", "gsplat")
    monkeypatch.setattr(settings, "gsplat_python", sys.executable)
    monkeypatch.setattr(settings, "colmap_bin", sys.executable)
    monkeypatch.setattr(colmap, "tool_version", lambda path: "4.2")
    monkeypatch.setattr(gsplat_trainer, "tool_version", lambda path: "gsplat 1.5.3")
    saved = dict(main.TOOLS)
    try:
        for total, want in ((12282, "ultra"), (8192, "max")):
            monkeypatch.setattr(gpu, "vram", lambda t=total: {"name": "RTX", "totalMb": t, "freeMb": t})
            main.TOOLS.clear()
            main._probe_tools()
            assert main._health_extra() == {"maxQuality": want}
    finally:
        main.TOOLS.clear()
        main.TOOLS.update(saved)


def test_clean_train_stats_keeps_known_flat_keys():
    out = finish.clean_train_stats({"zoned": True, "trainer": "gsplat", "zonesWall": 5, "x": 1, "gpu": "a" * 300,
                                    "retries": [{"stage": "train"}, "junk"]})
    assert out == {"zoned": True, "trainer": "gsplat", "zonesWall": 5, "retries": [{"stage": "train"}]}
