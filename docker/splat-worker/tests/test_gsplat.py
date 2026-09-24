"""The gsplat trainer without CUDA: COLMAP model reading, the views (sizes, intrinsics, cache), the
COLMAP-frame guard, the progress parser, and the subprocess contract (a fake trainer Python)."""
import os
import stat

import numpy as np
import pytest
from colmap_bin import write_model
from helpers import fixture_lines

from splatworker import colmap_model, gpu, gsplat_trainer, profiles
from splatworker.gsplat_data import Views, scaled_size
from splatworker.parsers import GsplatParser

QUARTER_TURN = (np.cos(np.pi / 4), 0.0, np.sin(np.pi / 4), 0.0)  # 90 degrees about y


def make_dataset(root, model="PINHOLE", points=None):
    """Two cameras (a 400 x 300 photo camera, a 192 x 108 video-frame camera), three images, sparse points
    around (10, -5, 30): far from the origin and not unit-scaled, like a real COLMAP frame."""
    from PIL import Image
    ds = os.path.join(root, "dataset")
    for name, size in (("geo_a/IMG_1.jpg", (400, 300)), ("geo_a/IMG_2.jpg", (400, 300)),
                       ("video/vf_0001.jpg", (192, 108))):
        os.makedirs(os.path.dirname(os.path.join(ds, "images", name)), exist_ok=True)
        Image.new("RGB", size, (120, 80, 40)).save(os.path.join(ds, "images", name))
    params = [300.0, 310.0, 200.0, 150.0] if model == "PINHOLE" else [300.0, 200.0, 150.0, 0.1, 0.01]
    cams = {1: (model, 400, 300, params), 2: ("SIMPLE_PINHOLE", 192, 108, [150.0, 96.0, 54.0])}
    imgs = [(1, "geo_a/IMG_1.jpg", 1, (1.0, 0, 0, 0), (-10.0, 5.0, -25.0)),
            (2, "geo_a/IMG_2.jpg", 1, QUARTER_TURN, (1.0, 2.0, 3.0)),
            (3, "video/vf_0001.jpg", 2, (1.0, 0, 0, 0), (-11.0, 5.0, -25.0))]
    rng = np.random.default_rng(1)
    pts = points if points is not None else [((10 + x, -5 + y, 30 + z), (200, 100, 50))
                                              for x, y, z in rng.normal(0, 2, (200, 3))]
    write_model(os.path.join(ds, "sparse", "0"), cams, imgs, pts)
    return ds


def test_colmap_model_reads_poses_intrinsics_and_points(tmp_path):
    ds = make_dataset(str(tmp_path))
    cams, imgs, xyz, rgb = colmap_model.read_model(ds)
    assert [i["name"] for i in imgs] == ["geo_a/IMG_1.jpg", "geo_a/IMG_2.jpg", "video/vf_0001.jpg"]
    assert colmap_model.intrinsics(cams[1]) == (300.0, 310.0, 200.0, 150.0)
    assert colmap_model.intrinsics(cams[2]) == (150.0, 150.0, 96.0, 54.0)
    assert xyz.shape == (200, 3) and rgb[0].tolist() == [200, 100, 50]
    assert np.allclose(colmap_model.camera_centre(imgs[0]), [10, -5, 25])
    vm = colmap_model.viewmat(imgs[1])  # world -> camera, exactly COLMAP's R|t
    assert np.allclose(vm[:3, :3] @ vm[:3, :3].T, np.eye(3)) and np.allclose(vm[:3, 3], [1, 2, 3])


def test_distorted_cameras_are_refused_before_training(tmp_path):
    ds = make_dataset(str(tmp_path), model="RADIAL")
    with pytest.raises(ValueError, match="undistort"):
        Views(ds, 400)
    with pytest.raises(gsplat_trainer.JobError, match="RADIAL"):
        gsplat_trainer.check_camera_models(ds)


def test_views_scale_each_camera_and_stay_in_the_colmap_frame(tmp_path):
    ds = make_dataset(str(tmp_path))
    v = Views(ds, max_edge=200, cache_mb=1)
    photo, frame = v.views[0], v.views[2]
    assert photo["size"] == (200, 150) and np.allclose(photo["K"][0], [150, 0, 100]) and photo["K"][1, 1] == 155
    assert frame["size"] == scaled_size(192, 108, 200) == (192, 108) and frame["K"][0, 0] == 150  # frames: own camera
    img, K, vm = v.get(0)
    assert img.shape == (150, 200, 3) and img.dtype == np.uint8
    assert np.allclose(vm[:3, 3], [-10, 5, -25])  # no normalising transform applied to the poses
    assert np.allclose(v.points.mean(0), [10, -5, 30], atol=1.0)
    assert v.scene_scale > 1 and len(v.cache) >= 1
    assert len(Views(ds, 200, cache_mb=0).cache) == 0 and Views(ds, 200, 0).get(1)[0].shape == (150, 200, 3)


def test_frame_guard_accepts_colmap_frame_and_rejects_normalised_splats(tmp_path):
    ds = make_dataset(str(tmp_path))
    pts, _ = colmap_model.read_points(os.path.join(ds, "sparse", "0", "points3D.bin"))
    splats = pts + np.random.default_rng(2).normal(0, 0.3, pts.shape)
    assert 0.5 < gsplat_trainer.check_frame(splats, ds)["spreadRatio"] < 2  # passes
    normalised = (splats - splats.mean(0)) / np.abs(splats - splats.mean(0)).max()  # what normalize_world_space does
    with pytest.raises(gsplat_trainer.JobError, match="COLMAP frame"):
        gsplat_trainer.check_frame(normalised, ds)
    with pytest.raises(gsplat_trainer.JobError, match="COLMAP frame"):
        gsplat_trainer.check_frame(splats * 50, ds)  # rescaled


def test_parser_reports_loading_steps_and_the_end():
    p = GsplatParser()
    out = [r for r in map(p, fixture_lines("gsplat-train.log")) if r]
    fractions = [f for f, _ in out]
    assert fractions == sorted(fractions) and fractions[0] < GsplatParser.LOAD_SHARE and fractions[-1] == 1.0
    assert any(d.startswith("loading images") for _, d in out) and any("splats" in d for _, d in out)
    assert p.step == p.total and p.splats > 0 and p.peak_vram_mb > 0 and p.took and p.oom is None
    assert GsplatParser()("step 10/0 splats 5") is None


def fake_python(tmp_path, body):
    """A stand-in for GSPLAT_PYTHON: a shell script that gets `-m splatworker.gsplat_train --data .. --out ..`."""
    path = tmp_path / "fake-python"
    path.write_text("#!/bin/sh\nwhile [ $# -gt 0 ]; do [ \"$1\" = --out ] && OUT=$2; shift; done\n" + body)
    path.chmod(path.stat().st_mode | stat.S_IEXEC)
    return str(path)


@pytest.mark.skipif(os.name != "posix", reason="shell stand-in")
def test_train_runs_the_script_and_turns_cuda_oom_into_a_retryable_error(tmp_path):
    ds = make_dataset(str(tmp_path))
    plan = gsplat_trainer.plans(0, [(400, 300)], profiles.resolve("draft"))[0]
    reports, log = [], str(tmp_path / "train.log")
    ok = fake_python(tmp_path, "echo 'loaded 3/3 images'; echo 'step 5000/5000 splats 42 loss 0.1'; "
                               "echo 'Training took 1.0s'; : > \"$OUT\"; echo 'wrote 42 splats; peak VRAM 900 MB'\n")
    ply, parser = gsplat_trainer.train(ok, ds, str(tmp_path / "out"), plan, log, lambda *a: reports.append(a))
    assert os.path.exists(ply) and parser.splats == 42 and parser.peak_vram_mb == 900 and reports[-1][0] == 1.0
    oom = fake_python(tmp_path, "echo 'GSPLAT_OOM CUDA out of memory. Tried to allocate 2.00 GiB'; exit 75\n")
    with pytest.raises(gsplat_trainer.CudaOomError, match="GPU memory"):
        gsplat_trainer.train(oom, ds, str(tmp_path / "out"), plan, log, lambda *a: None)
    crash = fake_python(tmp_path, "echo 'error: no CUDA device'; exit 2\n")
    with pytest.raises(gsplat_trainer.JobError, match="no CUDA device") as e:
        gsplat_trainer.train(crash, ds, str(tmp_path / "out"), plan, log, lambda *a: None)
    assert not isinstance(e.value, gsplat_trainer.CudaOomError)


def test_nvidia_smi_parsing_and_override(monkeypatch):
    assert gpu.parse_nvidia_smi("NVIDIA GeForce RTX 4070 Ti SUPER, 16376, 15321\n") == \
        [{"name": "NVIDIA GeForce RTX 4070 Ti SUPER", "totalMb": 16376, "freeMb": 15321}]
    assert gpu.parse_nvidia_smi("No devices were found") == []
    monkeypatch.setattr(gpu.settings, "vram_mb", 12000)
    assert gpu.vram()["totalMb"] == 12000
