"""Quality profiles (profiles.py), the Brush memory model + plans (tuning.py) and the train step-down."""
import json
import os

import pytest

from splatworker import brush, pipeline, profiles, sfm, tuning
from splatworker.options import OptionsError, SplatOptions, parse_options
from splatworker.procs import MemoryLimitError

# the new capture: 53 main-lens stills (3024 x 4032) + 120 walk-along frames (1080 x 1920)
PHOTOS, FRAMES = [(3024, 4032)] * 53, [(1080, 1920)] * 120


def sizes_at(profile):
    """What ingest leaves for the new capture under this profile."""
    def fit(w, h, edge):
        s = min(1.0, edge / max(w, h))
        return round(w * s), round(h * s)
    return [fit(w, h, profile.edge) for w, h in PHOTOS] + [fit(w, h, profile.frame_edge) for w, h in FRAMES]


def test_default_is_high_and_overrides_apply():
    o = parse_options({})
    assert o.quality == "high" and o.profile().name == "high" and o.profile().steps == 15000
    assert parse_options({"quality": "draft"}).profile().steps == 5000
    p = parse_options({"quality": "max", "maxSteps": 2000, "maxImageEdge": 1000}).profile()
    assert (p.name, p.steps, p.edge, p.frame_edge, p.min_edge) == ("max", 2000, 1000, 1000, 1000)
    with pytest.raises(OptionsError, match="quality"):
        parse_options({"quality": "extreme"})


def test_profiles_get_sharper_and_longer():
    d, h, m, u = (profiles.PROFILES[q] for q in profiles.QUALITIES)
    assert d.edge < h.edge < m.edge and d.steps < h.steps < m.steps and d.max_splats < h.max_splats < m.max_splats
    assert m.edge < u.edge and m.steps <= u.steps and u.sh_degree == 0  # ultra: sharpest photos, a wall-focused cap
    assert d.sh_degree == 3 and h.sh_degree == 0 and m.sh_degree == 0  # the exports keep only the DC colour
    assert d.mb_per_ksplat == 1.0 and h.mb_per_ksplat == 2.0  # measured on the M4 (profiles.py)
    assert h.frame_edge < h.edge  # frames stay small: memory without detail


def test_ladder_steps_down_without_growing_the_request():
    chain = profiles.ladder(profiles.resolve("max", max_steps=8000))
    assert [p.name for p in chain] == ["max", "high", "draft"]
    assert [p.steps for p in chain] == [8000, 8000, 5000]
    assert [p.name for p in profiles.ladder(profiles.resolve("draft"))] == ["draft"]


def test_brush_args():
    h = profiles.PROFILES["high"]
    args = h.brush_args(15000, 1_200_000)
    assert args[:4] == ["--max-splats", "1200000", "--sh-degree", "0"]
    assert "--growth-stop-iter" in args and args[args.index("--growth-stop-iter") + 1] == "9000"
    assert "--growth-stop-iter" not in profiles.PROFILES["draft"].brush_args(5000, 500_000)


def test_memory_model_matches_the_measured_runs():
    assert abs(tuning.brush_mb(tuning.dataset_mpx([(1350, 1800)] * 14, 1800), 118_000) - 2200) < 150
    mixed = [(960, 1280)] * 53 + [(720, 1280)] * 120
    assert abs(tuning.brush_mb(tuning.dataset_mpx(mixed, 1280), 534_000) - 3381) < 150
    assert tuning.dataset_mpx([(3024, 4032)], 2016) == pytest.approx(1512 * 2016 / 1e6)


def test_high_fits_a_7_gb_budget_with_a_fitted_cap_and_keeps_draft_as_fallback():
    high = profiles.resolve("high")
    plans = tuning.train_plans(7168, sizes_at(high), high)
    assert [p.profile.name for p in plans] == ["high", "draft"]
    assert plans[0].edge == 2400 and high.min_splats <= plans[0].max_splats < high.max_splats
    assert all(p.estimate_mb <= 7168 * tuning.HEADROOM + 1 for p in plans)
    # 5 GB (the first M4 run was killed there): high no longer fits, draft does
    assert [p.profile.name for p in tuning.train_plans(5120, sizes_at(high), high)] == ["draft"]


def test_small_budget_falls_back_to_draft_at_a_lower_edge():
    high = profiles.resolve("high")
    plans = tuning.train_plans(3643, sizes_at(high), high)  # what the busy M4 had
    assert [p.profile.name for p in plans] == ["draft"]
    assert 960 <= plans[0].edge < 1800 and plans[0].max_splats >= profiles.PROFILES["draft"].min_splats
    assert plans[0].fits and plans[0].estimate_mb <= 3643 * tuning.HEADROOM + 1  # int rounding


def test_nothing_fits_trains_the_lowest_at_its_floor_and_unknown_budget_trains_as_asked():
    high = profiles.resolve("high")
    plans = tuning.train_plans(1000, sizes_at(high), high)
    assert len(plans) == 1 and not plans[0].fits and plans[0].profile.name == "draft" and plans[0].edge == 960
    unknown = tuning.train_plans(0, sizes_at(high), high)
    assert [(p.profile.name, p.edge) for p in unknown] == [("high", 2400), ("draft", 1800)]


def test_max_steps_down_within_the_profile_before_leaving_it():
    mx = profiles.resolve("max")
    plans = tuning.train_plans(12000, sizes_at(mx), mx)
    assert plans[0].profile.name == "max" and mx.min_edge <= plans[0].edge <= mx.edge


def test_train_retries_the_next_profile_after_a_memory_kill(tmp_path, monkeypatch):
    img = tmp_path / "dataset" / "images"
    img.mkdir(parents=True)
    from PIL import Image
    for i in range(3):
        Image.new("RGB", (400, 300)).save(img / f"IMG_{i}.jpg")
    (tmp_path / "inputs.json").write_text(json.dumps({
        "photos": {f"IMG_{i}": {} for i in range(3)}, "options": SplatOptions(quality="high").to_dict()}))
    r = pipeline.Run(str(tmp_path), lambda *a: None)

    class FakeSfm:
        retries = []

        def train_budget_mb(self):
            return 0  # unknown: every profile of the ladder is a plan

    r.sfm_run = FakeSfm()
    calls = []

    class Parser:
        step, splats, took = 5000, 1234, "1m"

    def fake_train(bin_path, dataset, out, steps, edge, cache, log, report, budget, swap, args, checkpoints):
        calls.append((steps, edge, args[1]))
        if len(calls) == 1:
            raise MemoryLimitError("train", "Brush used 7 GB", "memory")
        return "x.ply", Parser()

    monkeypatch.setattr(brush, "train", fake_train)
    assert r.train(str(tmp_path / "dataset")) == "x.ply"
    assert [c[0] for c in calls] == [15000, 5000]
    assert r.brush_stats["quality"] == "draft" and r.brush_stats["qualityRequested"] == "high"
    assert FakeSfm.retries[0]["stage"] == "train" and FakeSfm.retries[0]["reason"] == "memory"
    assert "retrying as draft" in open(os.path.join(tmp_path, "tools.log")).read()


def test_brush_picks_the_last_checkpoint_by_iteration():
    assert brush._export_iter("/x/splat_15000.ply") > brush._export_iter("/x/splat_5000.ply")
    assert brush._export_iter("/x/other.ply") == -1


def test_min_memory_floor_raises_only_brushs_budget(monkeypatch):
    from splatworker import resources
    from splatworker.settings import settings
    info = {"totalMb": 16384, "availableMb": 3600}
    assert resources.memory_budget(info)[0] == 3600
    assert resources.memory_budget(info, 0, 7168)[0] == 7168
    assert resources.memory_budget(info, 0, 12000)[0] == int(16384 * resources.BUDGET_FRACTION)  # never above 60 %
    monkeypatch.setattr(settings, "min_memory_mb", 7168)
    monkeypatch.setattr(sfm, "system_memory", lambda: info)

    class R:
        log, dir = os.devnull, "."

    s = sfm.Sfm(R(), info)
    assert s.budget_mb == 3600 and s.train_budget_mb() == 7168  # COLMAP keeps the plain budget
