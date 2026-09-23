"""Walk-along video frames (vf_*): the pair list that keeps matching cheap, the request parsing, and
the rule that frames are trained on but never aligned with nor counted towards the minimum."""
import json
import os

import numpy as np
import pytest
from test_api import GEOMETRY, client, form, jpeg, three, wait  # noqa: F401 - client is a fixture

from computejobs.child import JobError
from computejobs.settings import settings
from splatworker import pipeline
from splatworker.colmap import Colmap
from splatworker.frames import build_pairs, is_frame, pair_count, split
from splatworker.options import SplatOptions, resolve_matcher


def names(prefix, n):
    return [f"{prefix}{i:04d}.jpg" for i in range(1, n + 1)]


def test_pairs_photos_exhaustive_frames_sequential_and_every_stride_th_frame_to_all_photos():
    photos, frames = ["g/p01.jpg", "g/p02.jpg", "g/p03.jpg"], names("v/vf_", 9)
    pairs = build_pairs(photos, frames, neighbours=2, stride=4)
    as_set = set(pairs)
    assert len(pairs) == len(as_set) == pair_count(3, 9, 2, 4)
    assert all(a < b for a, b in pairs)
    assert {("g/p01.jpg", "g/p02.jpg"), ("g/p01.jpg", "g/p03.jpg"), ("g/p02.jpg", "g/p03.jpg")} <= as_set
    assert ("v/vf_0001.jpg", "v/vf_0003.jpg") in as_set and ("v/vf_0001.jpg", "v/vf_0004.jpg") not in as_set
    linked = {b for a, b in pairs if a.startswith("g/")} | {a for a, b in pairs if b.startswith("g/")}
    tied = sorted(f for f in linked if "vf_" in f)
    assert tied == ["v/vf_0001.jpg", "v/vf_0005.jpg", "v/vf_0009.jpg"]  # frames 1, 5, 9 -> every photo


def test_pair_list_for_a_full_capture_is_a_fraction_of_exhaustive():
    n = pair_count(40, 120)  # defaults: 6 neighbours, every 4th frame to all photos
    assert n == 780 + 699 + 30 * 40
    assert n < 160 * 159 // 2 / 4
    assert len(build_pairs(names("g/p", 40), names("v/vf_", 120))) == n


def test_split_and_prefix():
    assert split(["vf_0002", "p02", "vf_0001", "p01"]) == (["p01", "p02"], ["vf_0001", "vf_0002"])
    assert is_frame("vf_0001") and not is_frame("p01") and not is_frame("IMG_vf_1")


def test_auto_matcher_uses_the_pair_list_only_with_frames():
    assert resolve_matcher("auto", 160, 120) == "pairs"
    assert resolve_matcher("auto", 40, 0) == "exhaustive"
    assert resolve_matcher("exhaustive", 160, 120) == "exhaustive"


def test_match_pairs_runs_matches_importer_in_chunks_with_the_caps(tmp_path):
    cm = Colmap("colmap", str(tmp_path / "t.log"), str(tmp_path), caps={"max_matches": 8192, "threads": 4},
                options={"FeatureMatching.use_gpu", "FeatureMatching.max_num_matches", "FeatureMatching.num_threads",
                         "FeatureExtraction.use_gpu"})
    runs, reports = [], []

    def fake_run(stage, args, *a):
        runs.append((stage, args, open(args[args.index("--match_list_path") + 1]).read().splitlines()))

    cm._run = fake_run
    pairs = build_pairs(names("g/p", 4), names("v/vf_", 20))
    cm.match_pairs("db", pairs, lambda f, d: reports.append((f, d)), chunk=25)
    assert [len(r[2]) for r in runs] == [25] * (len(pairs) // 25) + ([len(pairs) % 25] if len(pairs) % 25 else [])
    stage, args, lines = runs[0]
    assert stage == "sfm-matching" and args[0] == "matches_importer" and "pairs" in args
    assert args[args.index("--FeatureMatching.max_num_matches") + 1] == "8192"
    assert args[args.index("--FeatureMatching.num_threads") + 1] == "4"
    assert args[args.index("--FeatureMatching.guided_matching") + 1] == "0"  # guided matching is the memory hog
    assert lines[0].count(" ") == 1 and reports[-1] == (1.0, f"pairs {len(pairs)}/{len(pairs)}")


def make_run(tmp_path, photos=4, frames=6, geometry=None):
    stems = [f"p{i:02d}" for i in range(1, photos + 1)] + [f"vf_{i:04d}" for i in range(1, frames + 1)]
    (tmp_path / "inputs.json").write_text(json.dumps({
        "photos": {s: {"width": 10, "height": 10} for s in stems}, "options": SplatOptions().to_dict()}))
    if geometry:
        (tmp_path / "geometry.json").write_text(json.dumps(geometry))
    progress = []
    return pipeline.Run(str(tmp_path), lambda *a: progress.append(a)), progress


def test_registration_minimum_counts_photos_and_reports_frames(tmp_path):
    r, progress = make_run(tmp_path, photos=4, frames=6)
    r.begin("sfm-mapping")
    model = {"images": {f"g/p0{i}.jpg": 0 for i in (1, 2)} | {f"v/vf_000{i}.jpg": 0 for i in range(1, 7)}}
    with pytest.raises(JobError, match=r"only 2/4 images registered \(need 3\)"):
        r.check_registered(model)  # 8 registered images, but only 2 photos: frames don't count
    model["images"]["g/p03.jpg"] = 0
    del model["images"]["v/vf_0006.jpg"]
    r.check_registered(model)
    assert progress[-1][2] == "5/6 video frames registered"
    r.begin("train")
    r.report(0.5, "step 10/20")
    assert progress[-1][2] == "step 10/20; 5/6 video frames registered"


def test_frames_share_one_camera_and_the_pair_list_uses_colmap_names(tmp_path, monkeypatch):
    r, _ = make_run(tmp_path, photos=3, frames=5)
    assert pipeline.video_group((1920, 1080)) == ("video_land_1920x1080", None)
    r.groups = {"exif_x": {"names": [f"exif_x/p0{i}.jpg" for i in (1, 2, 3)], "params": [1, 2, 3, 0, 0]},
                "video_land_1920x1080": {"names": [f"video_land_1920x1080/vf_000{i}.jpg" for i in range(1, 6)],
                                         "params": None}}
    pairs = r.pair_list()
    assert ("exif_x/p01.jpg", "video_land_1920x1080/vf_0001.jpg") in pairs
    assert len(pairs) == pair_count(3, 5, settings.frame_neighbours, settings.frame_photo_stride)


def test_alignment_uses_the_photos_only(tmp_path, monkeypatch):
    r, _ = make_run(tmp_path, geometry=GEOMETRY)
    r.model = {"images": {"g/p01.jpg": np.zeros(3), "g/p02.jpg": np.ones(3), "v/vf_0001.jpg": np.ones(3) * 5}}
    seen = {}

    def fake_align(centres, doc, margin):
        seen.update(centres)
        return {"toViewer": np.eye(4).tolist(), "crop": None, "alignment": None}

    class S:
        xyz = np.zeros((2, 3))

        def subset(self, keep):
            return self

    monkeypatch.setattr(pipeline, "read_ply", lambda p: None)
    monkeypatch.setattr(pipeline.Splats, "from_ply", classmethod(lambda cls, c: S()))
    monkeypatch.setattr(pipeline, "crop_mask", lambda *a: np.ones(2, bool))
    monkeypatch.setattr(pipeline, "align", fake_align)
    r.frame_and_crop("x.ply")
    assert sorted(seen) == ["p01", "p02"]


def test_request_with_frames_is_accepted_and_frames_never_satisfy_the_minimum(client):  # noqa: F811
    frames = [(f"vf_{i:04d}.jpg", jpeg(color=(i * 20, 10, 10))) for i in range(1, 6)]
    r = client.post("/v1/jobs/splat", files=form([("a.jpg", jpeg()), *three(), *frames], None, GEOMETRY))
    assert r.status_code == 202, r.text
    job = r.json()["jobId"]
    assert wait(client, job)["status"] == "succeeded"
    inputs = json.load(open(os.path.join(settings.work_dir, job, "inputs.json")))
    assert sorted(s for s in inputs["photos"] if is_frame(s)) == [f"vf_{i:04d}" for i in range(1, 6)]

    r = client.post("/v1/jobs/splat", files=form([("a.jpg", jpeg()), ("b.jpg", jpeg()), *frames]))
    assert r.status_code == 422 and "got 2 and 5 video frames" in r.text
    only_frame = {**GEOMETRY, "cameras": [{**GEOMETRY["cameras"][0], "image": "vf_0001"}]}
    r = client.post("/v1/jobs/splat", files=form([*three(), *frames], None, only_frame))
    assert r.status_code == 422 and "no photo file name matches" in r.text
