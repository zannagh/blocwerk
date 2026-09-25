"""Trainer selection (SPLAT_TRAINER), the profile mapping (ultra, SPLAT_PROFILE_OVERRIDE) and gsplat's
VRAM plans. No CUDA here: the GPU is a dict and the trainer a monkeypatched function."""
import json
import os

import pytest

from splatworker import gsplat_trainer, pipeline, profiles, trainers
from splatworker.options import SplatOptions, parse_options
from splatworker.settings import settings

BIG, SMALL = {"name": "RTX 4070 Ti SUPER", "totalMb": 16376, "freeMb": 15300}, {"name": "8 GB", "totalMb": 8192,
                                                                                  "freeMb": 7600}
PHOTOS = [(3024, 4032)] * 53 + [(1080, 1920)] * 120  # 12 MP stills + walk-along frames, as ingested for ultra


@pytest.fixture
def gsplat(monkeypatch):
    monkeypatch.setattr(settings, "splat_trainer", "gsplat")
    monkeypatch.setattr(settings, "profile_override", "")


def test_select_defaults_to_brush_and_rejects_unknown(monkeypatch):
    monkeypatch.setattr(settings, "splat_trainer", "brush")
    assert trainers.select() == "brush" and trainers.select("GSPLAT") == "gsplat"
    monkeypatch.setattr(settings, "splat_trainer", "nerf")
    with pytest.raises(ValueError, match="SPLAT_TRAINER"):
        trainers.select()


def test_ultra_is_a_quality_and_needs_gsplat_and_12_gb(gsplat, monkeypatch):
    assert parse_options({"quality": "ultra"}).quality == "ultra"
    p, note = trainers.job_profile(SplatOptions(quality="ultra"), gpu_info=BIG)
    assert (p.name, p.edge, p.steps, p.max_splats, p.sh_degree, note) == ("ultra", 4096, 30000, 3_000_000, 0, None)
    p, note = trainers.job_profile(SplatOptions(quality="ultra"), gpu_info=SMALL)
    assert p.name == "max" and "12 GB" in note
    p, note = trainers.job_profile(SplatOptions(quality="ultra"), gpu_info={})  # no GPU visible
    assert p.name == "max"
    p, note = trainers.job_profile(SplatOptions(quality="ultra"), trainer="brush")
    assert p.name == "max" and "gsplat" in note
    # a "12 GB" card reports a little under 12288 MB
    assert trainers.job_profile(SplatOptions(quality="ultra"), gpu_info={"totalMb": 12282})[0].name == "ultra"


def test_profile_override_wins_and_request_overrides_still_apply(gsplat, monkeypatch):
    monkeypatch.setattr(settings, "profile_override", "ultra")
    p, _ = trainers.job_profile(SplatOptions(quality="high", maxSteps=3000), gpu_info=BIG)
    assert (p.name, p.steps, p.edge) == ("ultra", 3000, 4096)  # SPLATSERVICE__MAXSTEPS -> options.maxSteps
    monkeypatch.setattr(settings, "profile_override", "insane")
    with pytest.raises(ValueError, match="SPLAT_PROFILE_OVERRIDE"):
        trainers.job_profile(SplatOptions())


def test_brush_without_override_keeps_the_requested_profile(monkeypatch):
    monkeypatch.setattr(settings, "splat_trainer", "brush")
    monkeypatch.setattr(settings, "profile_override", "")
    for q in ("draft", "high", "max"):
        o = SplatOptions(quality=q, maxSteps=1234)
        assert trainers.job_profile(o) == (o.profile(), None)


@pytest.mark.parametrize("quality,steps,edge,cap,stop", [
    ("draft", 5000, 1800, 1_000_000, 0.5), ("high", 15000, 2400, 2_000_000, 0.6),
    ("max", 30000, 4032, 5_000_000, 0.5), ("ultra", 30000, 4096, 3_000_000, 0.5)])
def test_profile_maps_to_gsplat_arguments_on_a_16_gb_card(quality, steps, edge, cap, stop):
    p = profiles.resolve(quality)
    plan = gsplat_trainer.plans(BIG["freeMb"], PHOTOS, p, 16384)[0]
    args = gsplat_trainer.args_for(plan)
    got = {args[i]: args[i + 1] for i in range(0, len(args), 2)}
    assert (int(got["--steps"]), int(got["--max-edge"]), int(got["--cap"]), float(got["--refine-stop"])) == \
        (steps, edge, cap, stop)
    assert plan.fits and plan.estimate_mb <= BIG["freeMb"] * gsplat_trainer.HEADROOM


def test_small_vram_lowers_the_cap_then_the_profile_and_the_retry_is_smaller():
    ultra = profiles.resolve("ultra")
    plan, retry = gsplat_trainer.plans(2000, PHOTOS, ultra, 16384)
    assert plan.profile.name != "ultra" and plan.estimate_mb <= 2000 * gsplat_trainer.HEADROOM
    assert retry.max_splats < plan.max_splats and retry.edge < plan.edge and retry.cache_mb == 0
    nothing = gsplat_trainer.plans(500, PHOTOS, ultra, 16384)[0]
    assert not nothing.fits and nothing.profile.name == "draft"
    unknown = gsplat_trainer.plans(0, PHOTOS, ultra, 0)[0]  # no nvidia-smi: as asked
    assert (unknown.profile.name, unknown.edge, unknown.max_splats) == ("ultra", 4096, 3_000_000)


def test_image_cache_follows_the_host_budget():
    p = profiles.resolve("high")
    assert gsplat_trainer.plans(15000, PHOTOS, p, 16384)[0].cache_mb == int(16384 * 0.9) - gsplat_trainer.HOST_BASE_MB
    assert gsplat_trainer.plans(15000, PHOTOS, p, 3072)[0].cache_mb == 0  # tiny budget: decode per step


def _run(tmp_path):
    img = tmp_path / "dataset" / "images"
    img.mkdir(parents=True)
    from PIL import Image
    for i in range(3):
        Image.new("RGB", (400, 300)).save(img / f"IMG_{i}.jpg")
    (tmp_path / "inputs.json").write_text(json.dumps({
        "photos": {f"IMG_{i}": {} for i in range(3)}, "options": SplatOptions(quality="ultra").to_dict()}))
    return pipeline.Run(str(tmp_path), lambda *a: None)


class FakeSfm:
    def __init__(self):
        self.retries = []

    def train_budget_mb(self):
        return 8192


def test_pipeline_dispatches_to_gsplat_and_retries_once_after_cuda_oom(tmp_path, gsplat, monkeypatch):
    monkeypatch.setattr(settings, "gsplat_eval_every", 8)
    monkeypatch.setattr(trainers.gpu, "vram", lambda: BIG)
    r = _run(tmp_path)
    assert r.profile.name == "ultra"
    r.sfm_run = FakeSfm()
    calls, checked = [], []

    class Parser:
        step, splats, took, peak_vram_mb = 50000, 6_000_000, "3600.0s", 9000
        eval = {"psnr": 27.5, "ssim": 0.86, "views": 7}
        zones = None

    def fake_train(python, dataset, out, plan, log, report, budget, swap, eval_every=0, zones=None):
        assert eval_every == 8
        calls.append(plan)
        if len(calls) == 1:
            raise gsplat_trainer.CudaOomError("train", "gsplat ran out of GPU memory")
        return "x.ply", Parser()

    monkeypatch.setattr(gsplat_trainer, "train", fake_train)
    monkeypatch.setattr(trainers, "read_ply", lambda p: {"x": [0.0], "y": [0.0], "z": [0.0]})
    monkeypatch.setattr(gsplat_trainer, "check_frame", lambda xyz, d: checked.append(d))
    assert r.train(str(tmp_path / "dataset")) == "x.ply"
    assert len(calls) == 2 and calls[1].max_splats < calls[0].max_splats and checked
    assert r.sfm_run.retries[0]["reason"] == "vram"
    assert r.brush_stats["trainer"] == "gsplat" and r.brush_stats["peakVramMb"] == 9000
    assert r.brush_stats["evalPsnr"] == 27.5 and r.brush_stats["evalViews"] == 7 and r.brush_stats["evalEvery"] == 8
    assert "retrying as" in open(os.path.join(tmp_path, "tools.log")).read()


def test_a_second_cuda_oom_fails_with_a_clear_error(tmp_path, gsplat, monkeypatch):
    monkeypatch.setattr(trainers.gpu, "vram", lambda: BIG)
    r = _run(tmp_path)
    r.sfm_run = FakeSfm()

    def oom(*a, **kw):
        raise gsplat_trainer.CudaOomError("train", "gsplat ran out of GPU memory")

    monkeypatch.setattr(gsplat_trainer, "train", oom)
    with pytest.raises(trainers.JobError, match="smaller retry"):
        r.train(str(tmp_path / "dataset"))


def test_brush_stays_the_default_path(tmp_path, monkeypatch):
    monkeypatch.setattr(settings, "splat_trainer", "brush")
    monkeypatch.setattr(settings, "profile_override", "")
    r = _run(tmp_path)
    assert r.profile.name == "max"  # ultra requested, Brush: max
    monkeypatch.setattr(trainers, "train_gsplat", lambda *a: pytest.fail("gsplat must not run"))
    monkeypatch.setattr(pipeline.brush, "train", lambda *a: ("b.ply", type("P", (), {"step": 1, "splats": 1,
                                                                                         "took": "1s"})()))

    class Sfm(FakeSfm):
        def train_budget_mb(self):
            return 0

    r.sfm_run = Sfm()
    assert r.train(str(tmp_path / "dataset")) == "b.ply"


def test_a_job_with_a_wall_geometry_trains_with_zones(tmp_path, gsplat, monkeypatch):
    import numpy as np
    from test_zones import wall_doc
    monkeypatch.setattr(trainers.gpu, "vram", lambda: BIG)
    r = _run(tmp_path)
    r.sfm_run = FakeSfm()
    cams = {"IMG_0": (0, -2000, 500), "IMG_1": (2000, -2000, 500), "IMG_2": (1000, -2500, 1500)}
    r.geometry = {**wall_doc(), "cameras": [{"image": k, "R": np.eye(3).flatten().tolist(),
                                             "t": (-np.array(c, float)).tolist()} for k, c in cams.items()]}
    r.model = {"images": {f"g/{k}.jpg": np.array(c, float) / 1000 for k, c in cams.items()}}
    seen = {}

    class Parser:
        step, splats, took, peak_vram_mb, eval = 100, 10, "1s", 100, None
        zones = {"wall": 9, "surround": 1, "outside": 0}

    def fake_train(python, dataset, out, plan, log, report, budget, swap, eval_every=0, zones=None):
        seen["zones"] = zones
        seen["args"] = gsplat_trainer.args_for(plan, 0, zones)
        return "x.ply", Parser()

    monkeypatch.setattr(gsplat_trainer, "train", fake_train)
    monkeypatch.setattr(trainers, "read_ply", lambda p: {"x": [0.0], "y": [0.0], "z": [0.0]})
    monkeypatch.setattr(gsplat_trainer, "check_frame", lambda xyz, d: None)
    r.train(str(tmp_path / "dataset"))
    assert seen["zones"] == os.path.join(str(tmp_path), "zones.json") and os.path.exists(seen["zones"])
    args = seen["args"]
    assert args[args.index("--zones") + 1] == seen["zones"] and "--aniso-reg" in args
    assert args[args.index("--pose-steps") + 1] == "5000" and gsplat_trainer.pose_steps(5000) == 833
    assert r.zones["boxLo"] == [-400, -400, -150] and r.brush_stats["zones"] == Parser.zones
    r.geometry = None  # no geometry (or Brush): no zones, the plain crop box
    r.train(str(tmp_path / "dataset"))
    assert seen["zones"] is None and r.zones is None
