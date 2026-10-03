"""prepare -> bundle -> (runner) -> finish on a tiny synthetic wall, against the all-in-one job on the same
trained scene: the files must be identical (the wall-zone export cut, the refinement, the clean-up over
every camera). COLMAP is a stand-in (test_pipeline.FakeColmap) whose cameras sit where a similarity of the
geometry's cameras puts them."""
import json
import os
import shutil

import numpy as np
import pytest
from test_align import rot
from test_api import jpeg
from sparse_fixture import synthetic_model
from test_pipeline import FakeColmap

from splatworker import bundle, gpu, pipeline, sfm, trainers
from splatworker.cleanup_run import RAW_FILE
from splatworker.finish import run_finish
from splatworker.options import SplatOptions
from splatworker.prepare import run_prepare
from splatworker.settings import settings
from splatworker.slimply import write_slim_ply

S, R, T = 250.0, rot([1, 2, 3], 40), np.array([100.0, -50.0, 30.0])  # COLMAP -> world: S R x + T
CAMS = np.array([[x, -3000.0 + 300 * (x % 2), z] for x in (500, 1500, 2500, 3500) for z in (800, 2000)])
FACET = {"id": "0", "origin": [0, 0, 0], "u": [1, 0, 0], "v": [0, 0, 1], "normal": [0, -1, 0],
         "extentMm": {"aMin": 0, "aMax": 4000, "bMin": 0, "bMax": 3000}}


def to_colmap(p):
    return (np.asarray(p, float) - T) @ R / S  # R.T @ (p - T) / S, row-wise


def geometry():
    cams = []
    for i, c in enumerate(CAMS):
        Rc = rot([0.3, 1, 0.2], 7 * i)
        cams.append({"image": f"IMG_{i}", "R": Rc.flatten().tolist(), "t": (-Rc @ c).tolist()})
    return {"cameras": cams, "segments": [{"index": 0, "facets": [FACET]}]}


class WallColmap(FakeColmap):
    size = (80, 60)

    def match_pairs(self, *a, **kw):
        pass

    def best_model(self, sparse):
        imgs = {f"g/IMG_{i}.jpg": to_colmap(c) for i, c in enumerate(CAMS)}
        imgs["v/vf_0001.jpg"] = to_colmap([2000, -2500, 1500])
        path = os.path.join(sparse, "0") if sparse else "m"  # a real tiny model: prepare exports it (sparse.zip)
        if sparse:
            synthetic_model(path, sorted(imgs), {n: self.size for n in imgs})
        return (path, {"images": imgs, "points": 500, "meanReprojErrorPx": 0.6, "meanTrackLength": 4.2})

    def undistort(self, image_dir, model_dir, out_dir, max_size, report):
        WallColmap.undistort_edge = max_size
        os.makedirs(os.path.join(out_dir, "images", "g"))
        os.makedirs(os.path.join(out_dir, "sparse", "0"))
        photos = sorted(os.path.join(d, n) for d, _, names in os.walk(image_dir) for n in names if "IMG_" in n)
        for path in photos[:3]:
            shutil.copy(path, os.path.join(out_dir, "images", "g", os.path.basename(path)))
        for n in ("cameras.bin", "images.bin", "points3D.bin"):
            with open(os.path.join(out_dir, "sparse", "0", n), "wb") as fh:
                fh.write(b"\0" * 8)


def scene(seed=5):
    """World-mm splats: the wall surface, a clump in the surroundings, a post in the air, the room; in the
    COLMAP frame as a slim .ply's columns."""
    rng = np.random.default_rng(seed)
    wall = np.c_[rng.uniform(100, 3900, 4000), rng.normal(0, 2, 4000), rng.uniform(100, 2900, 4000)]
    around = np.c_[rng.uniform(-300, -50, 300), rng.uniform(-300, 300, 300), rng.uniform(0, 3000, 300)]
    post = np.c_[rng.normal(2000, 30, 200), rng.uniform(-900, -600, 200), rng.uniform(500, 2500, 200)]
    room = rng.uniform([-3000, -5000, -500], [7000, -1500, 4000], (300, 3))
    xyz = to_colmap(np.vstack([wall, around, post, room]))
    n = len(xyz)
    cols = {"x": xyz[:, 0], "y": xyz[:, 1], "z": xyz[:, 2], "opacity": rng.uniform(-1, 4, n),
            "rot_0": np.ones(n), "rot_1": rng.normal(0, 0.1, n), "rot_2": rng.normal(0, 0.1, n), "rot_3": np.zeros(n)}
    for i in range(3):
        cols[f"f_dc_{i}"] = rng.normal(0, 1, n)
        cols[f"scale_{i}"] = np.log(rng.uniform(3, 30, n) / S)
    return cols


def job_dir(root, name, options, geo=True, n_photos=len(CAMS)):
    d = root / name
    (d / "arrived").mkdir(parents=True)
    stems = [f"IMG_{i}" for i in range(n_photos)] + ["vf_0001"]
    for s in stems:
        (d / "arrived" / f"{s}.jpg").write_bytes(jpeg(size=WallColmap.size))
    (d / "inputs.json").write_text(json.dumps({"photos": {s: {} for s in stems}, "options": options}))
    if geo:
        (d / "geometry.json").write_text(json.dumps(geometry()))
    return d


class SfmStub:
    def __init__(self, stats):
        self.saved, self.retries = stats, []

    def stats(self):
        return dict(self.saved)


def all_in_one(root, prepared, ply, train_stats):
    """The all-in-one job's frame_and_crop + export on the same scene, with its own gsplat zones."""
    options = prepared["options"]
    d = job_dir(root, "local", options)
    r = pipeline.Run(str(d), lambda *a: None)
    _, r.model = WallColmap().best_model(None)
    r.matcher, r.groups = prepared["matcher"], dict.fromkeys(range(prepared["cameraGroups"]))
    r.sfm_run = SfmStub({**prepared["sfm"], "memoryRetries": [], "trainMemoryBudgetMb": None})
    trainers.write_zones(r)
    r.brush_stats = train_stats
    r.timings = {**prepared["stageSeconds"], "train": train_stats["trainingSeconds"]}
    all_splats, kept, raw, frame = r.frame_and_crop(ply)
    return d, r.export(all_splats, kept, raw, frame)


@pytest.fixture
def colmap(monkeypatch):
    monkeypatch.setattr(sfm, "Colmap", WallColmap)
    monkeypatch.setattr(settings, "colmap_use_gpu", False)


def test_prepare_then_finish_equals_the_all_in_one_job(tmp_path, colmap):
    options = {**SplatOptions().to_dict(), "cleanup": True, "quality": "max", "wallZones": True}  # zones opted in
    prep = job_dir(tmp_path, "prep", options)
    res = run_prepare(str(prep), lambda *a: None)
    assert res["files"] == ["bundle.zip", "prepared.json", "sparse.zip"] and res["bundle"]["zones"] is True
    assert sorted(os.listdir(prep)) == ["bundle.zip", "prepared.json", "sparse.zip", "tools.log"]
    prepared = json.load(open(prep / "prepared.json"))
    assert prepared["version"] == 2 and sorted(prepared["frameCentres"]) == ["vf_0001"]
    assert len(prepared["photoCentres"]) == len(CAMS) and prepared["zones"]["facets"][0]["id"] == "0"

    _, profile, zones = bundle.extract_bundle(str(prep / "bundle.zip"), str(tmp_path / "runner"))
    assert profile.name == "max" and json.load(open(zones)) == prepared["zones"]  # what the runner trains with

    fin = tmp_path / "finish"
    fin.mkdir()
    write_slim_ply(scene(), str(fin / "splat.ply"))
    ply_copy = str(tmp_path / "scene.ply")
    shutil.copy(fin / "splat.ply", ply_copy)
    stats = {"trainer": "gsplat", "zoned": True, "quality": "max", "steps": 3000, "trainingSeconds": 12.5}
    (fin / "prepared.json").write_text(json.dumps(prepared))
    (fin / "trainStats.json").write_text(json.dumps(stats))
    got = run_finish(str(fin), lambda *a: None)

    local_dir, want = all_in_one(tmp_path, prepared, ply_copy, {k: v for k, v in stats.items() if k != "zoned"})
    assert got["files"] == want["files"] == ["wall.splat", "wall.spz", RAW_FILE, "frame.json"]
    for name in ("wall.splat", "wall.spz", RAW_FILE):
        assert (fin / name).read_bytes() == (local_dir / name).read_bytes(), name
    assert got["frame"] == want["frame"] and got["frame"]["zones"]["kept"] > 0  # the zone cut ran on both
    assert got["frame"]["cleanup"]["cameras"] == len(CAMS) + 1  # the video frame looks through the air too
    g, w = got["stats"], want["stats"]
    for key in ("splatCount", "splatsTrained", "splatsBeforeCleanup", "registeredImages", "videoFramesRegistered",
                "unregistered", "alignmentResidualMm", "fileBytes", "trainingSeconds"):
        assert g[key] == w[key], key


def test_unzoned_runner_scene_gets_the_crop_box(tmp_path, colmap):
    prep = job_dir(tmp_path, "prep", {**SplatOptions().to_dict(), "quality": "draft"})
    res = run_prepare(str(prep), lambda *a: None)
    assert res["bundle"]["zones"] is False  # a wall geometry, but no opt-in: no zones.json, the runner trains plain
    fin = tmp_path / "finish"
    fin.mkdir()
    write_slim_ply(scene(), str(fin / "splat.ply"))
    shutil.copy(prep / "prepared.json", fin / "prepared.json")
    (fin / "trainStats.json").write_text(json.dumps({"trainer": "brush"}))  # Brush trains without zones
    got = run_finish(str(fin), lambda *a: None)
    assert "zones" not in got["frame"] and got["stats"]["splatCount"] > 0


def test_cpu_prepare_keeps_ultra_at_4096_px_whatever_the_gpu(tmp_path, colmap, monkeypatch):
    monkeypatch.setattr(settings, "splat_trainer", "gsplat")
    monkeypatch.setattr(gpu, "vram", lambda: {"name": "small", "totalMb": 4096, "freeMb": 4096})  # or none at all
    monkeypatch.setattr(WallColmap, "size", (4300, 60))
    prep = job_dir(tmp_path, "prep", {**SplatOptions().to_dict(), "quality": "ultra"}, geo=False)
    res = run_prepare(str(prep), lambda *a: None)
    assert res["quality"] == "ultra" and WallColmap.undistort_edge == 4096 and res["bundle"]["zones"] is False
    _, profile, _ = bundle.extract_bundle(str(prep / "bundle.zip"), str(tmp_path / "out"))
    assert profile.name == "ultra" and profile.edge == 4096
    from PIL import Image
    imgs = os.listdir(tmp_path / "out" / "dataset" / "images" / "g")
    with Image.open(tmp_path / "out" / "dataset" / "images" / "g" / imgs[0]) as im:
        assert im.size[0] == 4096
