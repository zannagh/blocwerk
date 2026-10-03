"""Pipeline decisions without the heavy tools: camera grouping/priors and clear failure reasons."""
import json

import numpy as np
import pytest

from computejobs.child import JobError
from splatworker import pipeline, sfm
from splatworker.options import SplatOptions, resolve_matcher


def test_camera_group_prefers_solver_intrinsics_scaled_to_the_image():
    cam = {"group": "iPhone 16 Pro|ultra-wide", "width": 4032, "height": 3024,
           "K": [1600.0, 0, 2016, 0, 1600.0, 1512, 0, 0, 1], "dist": [-0.01, 0.002, 0, 0, 0]}
    key, p = pipeline.camera_group("IMG_1", {"focal35": 14.0}, (1800, 1350), cam)
    assert key.startswith("geo_iPhone_16_Pro_ultra_wide_") and key.endswith("_land_1800x1350")
    assert p == pytest.approx([1600 * 1800 / 4032, 2016 * 1800 / 4032, 1512 * 1800 / 4032, -0.01, 0.002])
    # rotated relative to the calibration: fall back to EXIF
    key, p = pipeline.camera_group("IMG_1", {"focal35": 14.0, "lensKey": "abc"}, (1350, 1800), cam)
    assert key == "exif_abc_14_port_1350x1800" and p[0] == pytest.approx(14 / 36 * 1800)
    assert pipeline.camera_group("IMG_1", {}, (1350, 1800), None) == ("single", None)


def test_camera_group_keeps_two_lenses_apart_when_their_names_share_the_truncated_prefix():
    # Metadata-free photos (the app strips EXIF) group by the solver's camera group alone; the main
    # and the ultra-wide lens of one phone differ only after the 40-character readable prefix.
    wide = {"group": "Apple iPhone 16 Pro|iPhone 16 Pro back triple camera 2.22mm f/2.2|3024x4032",
            "width": 3024, "height": 4032, "K": [1618.9, 0, 1512, 0, 1618.9, 2016, 0, 0, 1]}
    main = {"group": "Apple iPhone 16 Pro|iPhone 16 Pro back triple camera 6.765mm f/1.78|4284x5712",
            "width": 4284, "height": 5712, "K": [5018.2, 0, 2142, 0, 5018.2, 2856, 0, 0, 1]}
    k_wide, _ = pipeline.camera_group("p04", {}, (1350, 1800), wide)
    k_main, _ = pipeline.camera_group("p03", {}, (1350, 1800), main)
    assert k_wide != k_main
    assert pipeline.camera_group("p05", {}, (1350, 1800), dict(wide))[0] == k_wide


def test_matcher_auto():
    assert resolve_matcher("auto", 150) == "exhaustive" and resolve_matcher("auto", 151) == "sequential"
    assert resolve_matcher("exhaustive", 400) == "exhaustive"


class FakeColmap:
    registered = 3
    gpu_extraction = False

    def __init__(self, *a, caps=None, **kw):
        self.caps = dict(caps or {})

    def extract(self, *a, **kw):
        pass

    def match(self, *a, **kw):
        pass

    def feature_counts(self, db):
        return [9000] * 14

    def map(self, *a, **kw):
        pass

    def triangulate(self, *a):
        self.triangulated = True

    def to_text(self, model_dir, out_dir):
        return self.best_model(model_dir)[1]

    def best_model(self, sparse):
        imgs = {f"g/IMG_{i}.jpg": np.zeros(3) for i in range(self.registered)}
        return ("m", {"images": imgs, "points": 10, "meanReprojErrorPx": 0.5})


def make_run(tmp_path, n=14):
    (tmp_path / "inputs.json").write_text(json.dumps({
        "photos": {f"IMG_{i}": {"width": 10, "height": 10} for i in range(n)},
        "options": SplatOptions().to_dict()}))
    progress = []
    r = pipeline.Run(str(tmp_path), lambda *a: progress.append(a))
    r.groups = {"single": {"names": [], "params": None}}
    return r, progress


def test_too_few_registered_fails_in_mapping_with_advice(tmp_path, monkeypatch):
    monkeypatch.setattr(sfm, "Colmap", FakeColmap)
    r, progress = make_run(tmp_path)
    with pytest.raises(JobError) as e:
        r.sfm(str(tmp_path))
    assert e.value.stage == "sfm-mapping"
    assert str(e.value).startswith("sfm-mapping: only 3/14 images registered (need 7): shoot more overlap")
    stages = list(dict.fromkeys(p[1] for p in progress))
    assert stages == ["sfm-features", "sfm-matching", "sfm-mapping"]
    assert any("memory budget" in (p[2] or "") for p in progress if len(p) > 2)
    fr = [p[0] for p in progress]
    assert fr == sorted(fr)


def test_crop_failure_is_reported(tmp_path, monkeypatch):
    r, _ = make_run(tmp_path)
    r.geometry, r.model = None, {"images": {}}
    monkeypatch.setattr(pipeline, "read_ply", lambda p: None)

    class S:
        xyz = np.zeros((5, 3))

    monkeypatch.setattr(pipeline.Splats, "from_ply", classmethod(lambda cls, c: S()))
    monkeypatch.setattr(pipeline, "crop_mask", lambda *a: np.zeros(5, bool))
    with pytest.raises(JobError, match="^crop: no splat inside the crop box"):
        r.frame_and_crop("x.ply")


def test_feature_budget_takes_colmap_max_features_whatever_the_image_count():
    from splatworker.tuning import feature_budget
    # default (8192, the M4-safe cap): as before, 8192 on both sides of 60 images
    assert feature_budget(14, 8192) == 8192 and feature_budget(180, 8192) == 8192
    # raised: no longer cut to 8192 once video frames push the count past 60
    assert feature_budget(14, 16384) == 16384 and feature_budget(180, 16384) == 16384
    # lowered: the ceiling for small jobs too
    assert feature_budget(14, 4096) == 4096 and feature_budget(180, 4096) == 4096
    # 0: the job's own count by image number
    assert feature_budget(60, 0) == 16384 and feature_budget(61, 0) == 8192


@pytest.mark.parametrize("configured,n,expected", [(8192, 14, 8192), (8192, 180, 8192), (16384, 180, 16384),
                                                   (0, 14, 16384), (0, 180, 8192)])
def test_sfm_extracts_with_the_feature_budget(tmp_path, monkeypatch, configured, n, expected):
    seen = []

    class Capture(FakeColmap):
        def extract(self, db, img_dir, batches, report, max_features):
            seen.append(max_features)

    monkeypatch.setattr(sfm, "Colmap", Capture)
    monkeypatch.setattr(pipeline.settings, "colmap_max_features", configured)
    r, _ = make_run(tmp_path, n)
    with pytest.raises(JobError):  # 3 registered: fails in mapping, after the extraction that matters here
        r.sfm(str(tmp_path))
    assert seen == [expected]
