"""A runner job's checkpoints and previews without a GPU: where the checkpoints live (job id + bundle sha), which
one a retry resumes (same signature, complete, newest), what is pruned, the preview schedule per profile, the plan
a resumed job trains, the trainer's options, and the parser's "resumed" line."""
import json
import os
import time

import pytest

from splatworker import checkpoints, gsplat_trainer, profiles
from splatworker.gpurunner.resume import ResumeSettings
from splatworker.parsers import GsplatParser

SHA = "ab" * 32


def fake_checkpoint(d, step, sig, meta=True):
    os.makedirs(d, exist_ok=True)
    pt, _ = checkpoints.paths(d, step)
    with open(pt, "wb") as fh:
        fh.write(b"state")
    if meta == "commit":
        checkpoints.commit(d, step, sig)
    elif meta:
        write_meta(d, step, sig)
    return pt


def write_meta(d, step, sig):
    with open(checkpoints.paths(d, step)[1], "w") as fh:
        json.dump({"step": step, "signature": sig}, fh)


def test_the_checkpoint_directory_is_keyed_by_job_and_bundle_and_needs_both(tmp_path):
    d = checkpoints.job_dir(str(tmp_path), "3f2a-11", SHA)
    assert d == os.path.join(str(tmp_path), f"3f2a-11-{SHA[:16]}")
    assert checkpoints.job_dir(str(tmp_path), "3f2a-11", "cd" * 32) != d  # another bundle: another directory
    assert checkpoints.job_dir(str(tmp_path), "../x", SHA).endswith(f"x-{SHA[:16]}")  # no path tricks
    assert checkpoints.job_dir(str(tmp_path), "j", None) is None
    assert checkpoints.job_dir(str(tmp_path), "", SHA) is None
    assert checkpoints.job_dir("", "j", SHA) is None


def test_the_signature_ignores_paths_and_the_cache_but_not_the_training():
    base = {"data": "/a", "out": "/a/s.ply", "steps": 50000, "cap": 6_000_000, "max_edge": 4096, "cache_mb": 4096,
            "zones": None, "checkpoint_dir": "/c", "preview_at": "7000"}
    same = {**base, "data": "/b", "out": "/b/s.ply", "cache_mb": 0, "checkpoint_dir": "/d", "preview_at": ""}
    assert checkpoints.signature(base) == checkpoints.signature(same)
    assert checkpoints.signature(base) != checkpoints.signature({**base, "cap": 3_600_000})
    assert checkpoints.signature(base) != checkpoints.signature({**base, "zones": "/job/b/zones.json"})
    assert checkpoints.signature({**base, "zones": "/x/zones.json"}) == checkpoints.signature({**base, "zones": "/y/z"})


def test_latest_resumes_the_newest_complete_checkpoint_of_the_same_training(tmp_path):
    d = str(tmp_path / "ck")
    assert checkpoints.latest(d, "s") is None
    fake_checkpoint(d, 5000, "s")
    fake_checkpoint(d, 10000, "s")
    fake_checkpoint(d, 15000, "s", meta=False)  # saved but never committed: incomplete
    fake_checkpoint(d, 20000, "other")  # another plan (an out-of-memory retry): not this one
    assert checkpoints.latest(d, "s") == (10000, checkpoints.paths(d, 10000)[0])
    assert checkpoints.latest(d, "other")[0] == 20000
    os.remove(checkpoints.paths(d, 10000)[0])  # a .json without its .pt
    assert checkpoints.latest(d, "s")[0] == 5000


def test_commit_keeps_only_the_checkpoint_just_written(tmp_path):
    d = str(tmp_path / "ck")
    fake_checkpoint(d, 5000, "s", meta="commit")
    fake_checkpoint(d, 45000, "old")
    fake_checkpoint(d, 10000, "s", meta="commit")
    assert sorted(os.listdir(d)) == ["ckpt-10000.json", "ckpt-10000.pt"]
    assert checkpoints.latest(d, "s")[0] == 10000


def test_prune_and_discard(tmp_path):
    old, fresh = tmp_path / "old-1", tmp_path / "fresh-2"
    old.mkdir()
    fresh.mkdir()
    past = time.time() - 4 * 86400
    os.utime(old, (past, past))
    assert checkpoints.prune(str(tmp_path), 72 * 3600) == 1
    assert [p.name for p in tmp_path.iterdir()] == ["fresh-2"]
    checkpoints.discard(str(fresh))
    checkpoints.discard(None)
    assert list(tmp_path.iterdir()) == []


def test_preview_steps_scale_with_the_profile():
    f = checkpoints.parse_fractions(checkpoints.DEFAULT_PREVIEWS)
    assert checkpoints.preview_steps(profiles.PROFILES["ultra"].steps, f) == [7000, 20000]
    assert checkpoints.preview_steps(profiles.zoned(profiles.PROFILES["ultra"]).steps, f) == [4000, 12000]
    assert checkpoints.preview_steps(profiles.PROFILES["max"].steps, f) == [4000, 12000]
    assert checkpoints.preview_steps(profiles.PROFILES["high"].steps, f) == [6000]  # 2000 is too early
    assert checkpoints.preview_steps(profiles.PROFILES["draft"].steps, f) == []
    assert checkpoints.preview_steps(50000, (0.1, 0.1, 0.99)) == [5000, 49500]


def test_parse_fractions():
    assert checkpoints.parse_fractions("0.4, 0.14,x,2,0") == (0.14, 0.4)
    for off in ("", "0", "off", "none", None):
        assert checkpoints.parse_fractions(off) == ()


def test_newest_preview_ignores_partial_files(tmp_path):
    assert checkpoints.newest_preview(str(tmp_path)) is None
    for name in ("preview-7000-of-50000.ply", "preview-20000-of-50000.ply.part", "upload-7000.ply"):
        (tmp_path / name).write_bytes(b"x")
    assert checkpoints.newest_preview(str(tmp_path))[:2] == (7000, 50000)


def test_runner_settings_from_the_environment(tmp_path):
    s = ResumeSettings(str(tmp_path), env={})
    assert (s.every, s.root, s.ttl_s, s.previews) == (5000, str(tmp_path / "checkpoints"), 72 * 3600, (0.14, 0.4))
    job = {"jobId": "j1", "bundleSha256": SHA, "previews": True}
    r = s.for_job(job, str(tmp_path / "job-x"))
    assert r.checkpoint_dir == checkpoints.job_dir(s.root, "j1", SHA) and r.checkpoint_every == 5000
    assert r.preview_dir == str(tmp_path / "job-x" / "previews")
    assert s.for_job({**job, "previews": False}, "x").preview_dir is None  # the server did not offer them
    off = ResumeSettings(str(tmp_path), env={"RUNNER_CHECKPOINT_EVERY": "0", "RUNNER_PREVIEWS": "0",
                                             "RUNNER_CHECKPOINT_DIR": "/vol/ck", "RUNNER_CHECKPOINT_TTL_H": "5"})
    assert off.root == "/vol/ck" and off.ttl_s == 5 * 3600
    r = off.for_job(job, "x")
    assert r.checkpoint_dir is None and r.checkpoint_every == 0 and r.preview_dir is None


def test_the_trainer_options_of_a_runner_job():
    r = checkpoints.TrainResume("/ck", 5000, "/p", (0.14, 0.4))
    assert gsplat_trainer.resume_args(r, 50000) == ["--checkpoint-dir", "/ck", "--checkpoint-every", "5000",
                                                    "--preview-dir", "/p", "--preview-at", "7000,20000"]
    assert gsplat_trainer.resume_args(checkpoints.TrainResume(), 50000) == []
    assert gsplat_trainer.resume_args(None, 50000) == []
    assert gsplat_trainer.resume_args(checkpoints.TrainResume("/ck", 5000, "/p", (0.14, 0.4)), 5000) == \
        ["--checkpoint-dir", "/ck", "--checkpoint-every", "5000"]


def test_a_resumed_job_trains_the_plan_its_checkpoints_were_made_with(tmp_path):
    sizes = [(4032, 3024)] * 10
    ultra = profiles.PROFILES["ultra"]
    planned = gsplat_trainer.plans(16000, sizes, ultra)
    r = checkpoints.TrainResume(str(tmp_path / "ck"), 5000)
    assert gsplat_trainer.resumed_plans(planned, r, sizes) == planned  # nothing remembered yet
    stored = gsplat_trainer.GsplatPlan(ultra, 3000, 4_000_000, 0, 0)
    gsplat_trainer.remember_plan(r, stored)
    plans = gsplat_trainer.resumed_plans(planned, r, sizes)
    assert (plans[0].edge, plans[0].max_splats, plans[0].cache_mb) == (3000, 4_000_000, planned[0].cache_mb)
    assert plans[1].max_splats < plans[0].max_splats and plans[1].cache_mb == 0
    other = gsplat_trainer.plans(16000, sizes, profiles.PROFILES["max"])
    assert gsplat_trainer.resumed_plans(other, r, sizes) == other  # another profile: planned afresh
    gsplat_trainer.remember_plan(checkpoints.TrainResume(str(tmp_path / "off"), 0), stored)
    assert not (tmp_path / "off").exists()


def test_the_parser_reads_a_resume():
    p = GsplatParser()
    fraction, detail = p("resumed from step 20000/50000 (5000000 splats)")
    assert p.resumed == p.step == 20000 and p.total == 50000 and 0.4 < fraction < 0.45 and "resumed" in detail
    assert p("step 20250/50000 splats 5000000 loss 0.05")[0] > fraction


def test_an_unloadable_checkpoint_is_dropped_and_training_starts_afresh(tmp_path):
    d = str(tmp_path / "ck")
    fake_checkpoint(d, 20000, "s", meta="commit")
    (tmp_path / "ck" / gsplat_trainer.PLAN_FILE).write_text("{}")
    logged = []

    def torn(pt):
        raise EOFError("Ran out of input")

    assert checkpoints.resume_or_drop(d, "s", torn, logged.append) is None
    assert "unusable" in logged[0] and "starting from step 0" in logged[0]
    assert sorted(os.listdir(d)) == [gsplat_trainer.PLAN_FILE]  # the checkpoint went, the plan stays
    fake_checkpoint(d, 5000, "s", meta="commit")
    seen = []
    assert checkpoints.resume_or_drop(d, "s", seen.append) == 5000 and seen == [checkpoints.paths(d, 5000)[0]]
    assert checkpoints.resume_or_drop(str(tmp_path / "none"), "s", seen.append) is None and len(seen) == 1


def test_checkpoints_and_previews_take_their_host_copy_from_the_image_cache():
    ultra = profiles.PROFILES["ultra"]
    planned = gsplat_trainer.plans(16000, [(4032, 3024)] * 10, ultra, host_budget_mb=24000)
    saving = gsplat_trainer.with_checkpoint_headroom(planned, checkpoints.TrainResume("/ck", 5000))
    assert planned[0].cache_mb - saving[0].cache_mb == gsplat_trainer.checkpoint_mb(planned[0].max_splats)
    assert gsplat_trainer.checkpoint_mb(6_000_000) > 1000 and saving[1].cache_mb == 0
    assert gsplat_trainer.with_checkpoint_headroom(planned, checkpoints.TrainResume()) == planned
    assert gsplat_trainer.with_checkpoint_headroom(planned, None) == planned
    assert gsplat_trainer.with_checkpoint_headroom(planned, checkpoints.TrainResume(preview_dir="/p"))[0].cache_mb \
        < planned[0].cache_mb


def test_a_checkpoint_resumes_only_in_the_frame_it_was_trained_in():
    pytest.importorskip("torch")
    from splatworker.gsplat_checkpoint import frame_doc, same_frame
    unit = frame_doc(([10.0, -5.0, 30.0], 0.15))
    assert same_frame(unit, frame_doc(([10.0, -5.0, 30.0], 0.15))) and same_frame(None, frame_doc(None))
    assert not same_frame(unit, None) and not same_frame(None, unit)  # a world-frame checkpoint never resumes a unit one
    assert not same_frame(unit, frame_doc(([10.0, -5.0, 30.5], 0.15)))
    assert not same_frame(unit, frame_doc(([10.0, -5.0, 30.0], 0.2)))
