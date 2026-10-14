"""frame.json videoCameras: the registered video frames' poses in the wall-geometry world (mm)."""
import json

import numpy as np
import pytest

from splatworker import finish
from splatworker.align import align
from splatworker.colmap_model import read_text_model
from splatworker.splatio import Splats
from splatworker.split_api import check_frame_cameras
from splatworker.options import OptionsError
from splatworker.video_cameras import frame_cameras, opencv_intrinsics, video_cameras
from test_align import geometry, rot
from test_split import STATE, cols

S_TRUE, R_TRUE, T_TRUE = 250.0, rot([1, 2, 3], 40), np.array([100.0, -50.0, 30.0])


def to_colmap(X):
    """World mm -> this run's COLMAP frame (X = S R Xc + T)."""
    return R_TRUE.T @ (np.asarray(X, float) - T_TRUE) / S_TRUE


def project(R, t, K, X):
    x = np.asarray(R, float).reshape(3, 3) @ X + np.asarray(t, float)
    return (np.asarray(K, float).reshape(3, 3) @ (x / x[2]))[:2]


def aligned_frame():
    rng = np.random.default_rng(3)
    C = rng.uniform([-500, -4000, 1000], [4500, -2000, 2000], size=(8, 3))
    return align({f"IMG_{i}": to_colmap(c) for i, c in enumerate(C)}, geometry(C), margin_mm=400)


def walk_frame(stem="vf_0007"):
    """A frame camera 2.5 m in front of the wall looking at it: its true world pose and its COLMAP pose."""
    R_w = rot([0.2, 1, 0.1], 25) @ np.array([[1.0, 0, 0], [0, 0, -1], [0, 1, 0]])
    t_w = -R_w @ np.array([1500.0, -2500, 1400])
    colmap = {"image": stem, "width": 1280, "height": 720, "K": [900.0, 0, 640, 0, 900, 360, 0, 0, 1],
              "dist": [0.01, 0.0, 0.0, 0.0], "R": (R_w @ R_TRUE).reshape(9).tolist(),
              "t": ((R_w @ T_TRUE + t_w) / S_TRUE).tolist()}
    return R_w, t_w, colmap


def test_the_text_model_keeps_the_poses_and_cameras(tmp_path):
    (tmp_path / "cameras.txt").write_text("# header\n1 RADIAL 1280 720 900 640 360 0.01 -0.002\n2 PINHOLE 4000 3000 1 2 3 4\n")
    (tmp_path / "images.txt").write_text(
        "# header\n1 1 0 0 0 0.5 0 0 2 single_p01/p01.jpg\n\n2 1 0 0 0 1 2 3 1 video_land_1280x720/vf_0001.jpg\n\n")
    (tmp_path / "points3D.txt").write_text("# header\n1 0 0 0 1 2 3 0.5 1 0 2 0\n")
    m = read_text_model(str(tmp_path))
    assert m["cameras"][1]["model"] == "RADIAL" and m["poses"]["single_p01/p01.jpg"]["camera"] == 2
    cams = frame_cameras(m)
    assert [c["image"] for c in cams] == ["vf_0001"]  # photos are reported by the geometry model, not here
    assert cams[0]["K"] == [900, 0, 640, 0, 900, 360, 0, 0, 1] and cams[0]["dist"] == [0.01, -0.002, 0, 0]
    assert cams[0]["t"] == [1, 2, 3] and (cams[0]["width"], cams[0]["height"]) == (1280, 720)


def test_a_text_model_without_cameras_reports_no_frame_cameras(tmp_path):
    (tmp_path / "images.txt").write_text("# header\n1 1 0 0 0 0 0 0 1 v/vf_0001.jpg\n\n")
    (tmp_path / "points3D.txt").write_text("# header\n")
    assert frame_cameras(read_text_model(str(tmp_path))) == []


def test_opencv_intrinsics_of_the_colmap_models():
    assert opencv_intrinsics({"model": "SIMPLE_RADIAL", "params": [5, 1, 2, 0.1]})[1] == [0.1, 0, 0, 0]
    assert opencv_intrinsics({"model": "OPENCV", "params": [5, 6, 1, 2, 0.1, 0.2, 0.3, 0.4]})[0][4] == 6
    assert opencv_intrinsics({"model": "OPENCV_FISHEYE", "params": [1] * 8}) is None


def test_a_frame_camera_lands_on_its_true_world_pose():
    frame = aligned_frame()
    R_w, t_w, colmap = walk_frame()
    (cam,) = video_cameras([colmap], frame)
    assert np.allclose(np.reshape(cam["R"], (3, 3)), R_w, atol=1e-5)
    assert np.allclose(cam["t"], t_w, atol=0.05)
    X = np.array([1800.0, 0, 1200])  # a point on the wall projects to the same pixel in both frames
    assert np.allclose(project(cam["R"], cam["t"], cam["K"], X), project(colmap["R"], colmap["t"], colmap["K"],
                                                                         to_colmap(X)), atol=0.01)


def test_an_applied_refinement_wins_and_an_unaligned_frame_reports_none():
    frame = aligned_frame()
    _, _, colmap = walk_frame()
    shifted = np.array(frame["toWorldMm"])
    shifted[0, 3] += 30
    frame["refinement"] = {"applied": True, "toWorldMm": shifted.tolist()}
    (cam,) = video_cameras([colmap], frame)
    centre = -np.reshape(cam["R"], (3, 3)).T @ np.array(cam["t"])
    assert centre[0] == pytest.approx(1530, abs=0.1)
    assert video_cameras([colmap], {"aligned": False, "toWorldMm": None}) is None
    assert video_cameras([], frame) is None


def test_finish_writes_the_saved_frame_cameras_into_frame_json(tmp_path):
    _, _, colmap = walk_frame()
    f = finish.FinishRun(str(tmp_path), lambda *a: None, {**STATE, "frameCameras": [colmap]}, {})
    s = Splats.from_ply(cols())
    res = f.export(s, s, None, aligned_frame())
    doc = json.load(open(tmp_path / "frame.json"))
    assert [c["image"] for c in doc["videoCameras"]] == ["vf_0007"] and "videoCameras" not in res["frame"]


def test_an_old_prepare_without_frame_cameras_writes_none(tmp_path):
    f = finish.FinishRun(str(tmp_path), lambda *a: None, STATE, {})
    s = Splats.from_ply(cols())
    f.export(s, s, None, aligned_frame())
    assert "videoCameras" not in json.load(open(tmp_path / "frame.json"))


def test_the_finish_upload_checks_frame_cameras():
    _, _, colmap = walk_frame()
    check_frame_cameras(None)
    check_frame_cameras([colmap])
    for bad in ([{**colmap, "K": [1, 2]}], [{**colmap, "width": 0}], "x", [{**colmap, "dist": ["a"]}]):
        with pytest.raises(OptionsError):
            check_frame_cameras(bad)
