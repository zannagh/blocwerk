"""A gsplat runner job's previews and checkpoints against the fake server: previews are uploaded while the (stub)
trainer runs, only when the claim offers them, never in the wrong frame; the job's checkpoints are handed to the
trainer, kept for a retry after a retryable failure and dropped once the job succeeded or failed for good."""
import os
import threading
import time

from computejobs.child import JobError
from test_gpurunner import CAPS, KEY, Parser, fast, full_ply, make_bundle, server  # noqa: F401 - fixtures

from splatworker import checkpoints, gpu, gsplat_trainer
from splatworker.gpurunner.client import Client
from splatworker.gpurunner.loop import Runner
from splatworker.gpurunner.previews import PreviewUploader
from splatworker.gpurunner.resume import ResumeSettings
from splatworker.settings import settings


def wait_for(cond, timeout=5.0):
    t0 = time.monotonic()
    while not cond() and time.monotonic() - t0 < timeout:
        time.sleep(0.02)
    return cond()


def gsplat_job(monkeypatch, seen, previews=(7000, 20000), fail=None, frame_ok=lambda step: True):
    """The trainer stub: writes the previews (waiting for each upload), then the result or `fail`."""
    def fake_train(python, dataset, out, plan, log, report, *a, zones=None, resume=None, **k):
        seen["resume"] = resume
        if resume.checkpoint_dir:
            os.makedirs(resume.checkpoint_dir, exist_ok=True)
            open(os.path.join(resume.checkpoint_dir, "ckpt-5000.pt"), "wb").close()
        for step in previews if resume.preview_dir else ():
            seen["step"] = step
            path = os.path.join(resume.preview_dir, checkpoints.preview_name(step, 50000))
            full_ply(path)
            assert wait_for(lambda: not os.path.exists(path))
        if fail:
            raise fail
        return full_ply(f"{out}/splat.ply"), Parser()

    def check(xyz, dataset):
        if not frame_ok(seen.get("step")):
            raise JobError("train", "the trained splats are not in the COLMAP frame")
        return {"spreadRatio": 1.0}

    monkeypatch.setattr(settings, "splat_trainer", "gsplat")
    monkeypatch.setattr(gpu, "vram", lambda: {"name": "RTX", "totalMb": 16376, "freeMb": 15000})
    monkeypatch.setattr(gsplat_trainer, "train", fake_train)
    monkeypatch.setattr(gsplat_trainer, "check_frame", check)
    monkeypatch.setattr("splatworker.gpurunner.previews.POLL_S", 0.02)


def run(srv, tmp_path, env=None):
    work = str(tmp_path / "work")
    runner = Runner(Client(srv.url, KEY), work, capabilities=lambda: dict(CAPS), max_jobs=1,
                    resume=ResumeSettings(work, env=env or {}))
    return runner, runner.run()


def test_previews_are_uploaded_while_training_and_the_checkpoints_go_once_the_job_succeeded(
        tmp_path, fast, server, monkeypatch):  # noqa: F811
    seen = {}
    gsplat_job(monkeypatch, seen)
    srv = server(make_bundle(tmp_path), key=KEY)
    srv.offer_previews = True
    runner, code = run(srv, tmp_path)
    assert code == 0 and runner.outcomes == ["succeeded"]
    assert [p["query"] for p in srv.previews] == ["step=7000&total=50000", "step=20000&total=50000"]
    assert all(p["headers"]["Content-Encoding"] == "gzip" and p["body"].startswith(b"ply") for p in srv.previews)
    assert '"previewStep":7000' in srv.previews[0]["headers"]["X-Blocwerk-Stats"]
    assert '"previewsUploaded":2' in srv.results[0]["headers"]["X-Blocwerk-Stats"]
    r = seen["resume"]
    assert r.checkpoint_every == 5000 and r.checkpoint_dir == checkpoints.job_dir(
        str(tmp_path / "work" / "checkpoints"), "j1", srv.job()["bundleSha256"])
    assert not os.path.exists(r.checkpoint_dir)  # succeeded: nothing to resume any more


def test_no_previews_unless_the_server_offers_them(tmp_path, fast, server, monkeypatch):  # noqa: F811
    seen = {}
    gsplat_job(monkeypatch, seen)
    srv = server(make_bundle(tmp_path), key=KEY)
    runner, _ = run(srv, tmp_path)
    assert runner.outcomes == ["succeeded"] and srv.previews == [] and seen["resume"].preview_dir is None


def test_a_preview_in_the_wrong_frame_is_not_uploaded(tmp_path, fast, server, monkeypatch):  # noqa: F811
    seen = {}
    gsplat_job(monkeypatch, seen, frame_ok=lambda step: step != 7000)
    srv = server(make_bundle(tmp_path), key=KEY)
    srv.offer_previews = True
    runner, _ = run(srv, tmp_path)
    assert runner.outcomes == ["succeeded"] and [p["query"] for p in srv.previews] == ["step=20000&total=50000"]


def test_a_refused_preview_is_skipped_and_training_goes_on(tmp_path, fast, server, monkeypatch):  # noqa: F811
    seen = {}
    gsplat_job(monkeypatch, seen)
    srv = server(make_bundle(tmp_path), key=KEY)
    srv.offer_previews = True
    srv.errors["preview"] = [(409, {"title": "busy"}, {}), (422, {"t": 1}, {}), (422, {"t": 1}, {})]
    runner, _ = run(srv, tmp_path)
    assert runner.outcomes == ["succeeded"] and srv.previews == [] and len(srv.results) == 1


def test_a_retryable_failure_keeps_the_checkpoints_and_a_fatal_one_drops_them(
        tmp_path, fast, server, monkeypatch):  # noqa: F811
    seen = {}
    gsplat_job(monkeypatch, seen, previews=(), fail=JobError("train", "CUDA driver error: device not ready"))
    srv = server(make_bundle(tmp_path), key=KEY)
    runner, _ = run(srv, tmp_path)
    assert runner.outcomes == ["failed"] and srv.fails[0]["retryable"] is True
    assert os.path.exists(os.path.join(seen["resume"].checkpoint_dir, "ckpt-5000.pt"))

    gsplat_job(monkeypatch, seen, previews=())
    srv2 = server(make_bundle(tmp_path), key=KEY)
    srv2.errors["result"] = [(413, {"title": "too large"}, {})]
    runner, _ = run(srv2, tmp_path)
    assert runner.outcomes == ["failed"] and srv2.fails[0]["retryable"] is False
    assert not os.path.exists(seen["resume"].checkpoint_dir)


def test_old_checkpoints_are_pruned_at_start(tmp_path, fast, server, monkeypatch):  # noqa: F811
    stale = tmp_path / "work" / "checkpoints" / "old-0123456789abcdef"
    stale.mkdir(parents=True)
    past = time.time() - 100 * 3600
    os.utime(stale, (past, past))
    srv = server(make_bundle(tmp_path), key=KEY)
    srv.jobs_left = 0
    work = str(tmp_path / "work")
    runner = Runner(Client(srv.url, KEY), work, capabilities=lambda: dict(CAPS), max_jobs=1,
                    resume=ResumeSettings(work, env={}))
    runner.shutdown.set()
    runner.run()
    assert not stale.exists()


def test_the_uploader_sends_only_the_newest_preview(tmp_path, monkeypatch):
    class Recorder:
        def __init__(self):
            self.sent = []

        def upload_preview(self, job_id, path, step, total, stats, stop=None):
            self.sent.append(step)
            return os.path.getsize(path)

    client = Recorder()
    d = tmp_path / "p"
    full_ply(str(d / checkpoints.preview_name(7000, 50000)))
    full_ply(str(d / checkpoints.preview_name(20000, 50000)))
    up = PreviewUploader(client, "j1", str(d), str(tmp_path), threading.Event())
    monkeypatch.setattr(gsplat_trainer, "check_frame", lambda xyz, ds: None)
    assert up.poll_once() == 20000 and up.poll_once() is None
    assert client.sent == [20000] and os.listdir(d) == []


def test_a_frame_check_failure_drops_the_checkpoints_and_keeps_the_splats_to_look_at(
        tmp_path, fast, server, monkeypatch):  # noqa: F811
    seen = {}
    gsplat_job(monkeypatch, seen, previews=(), frame_ok=lambda step: False)
    srv = server(make_bundle(tmp_path), key=KEY)
    runner, _ = run(srv, tmp_path)
    assert runner.outcomes == ["failed"] and srv.fails[0]["retryable"] is True
    ck = seen["resume"].checkpoint_dir
    assert not os.path.exists(ck)  # a retry would resume the finished training and fail the same way
    kept = os.path.join(os.path.dirname(ck), "frame-check-failed", os.path.basename(ck) + ".ply")
    assert os.path.getsize(kept) > 0


def test_old_checkpoints_are_pruned_after_every_job(tmp_path, fast, server, monkeypatch):  # noqa: F811
    seen = {}
    gsplat_job(monkeypatch, seen, previews=())
    stale = tmp_path / "work" / "checkpoints" / "old-0123456789abcdef"
    inner = gsplat_trainer.train

    def train_and_age(*a, **k):
        stale.mkdir(parents=True)
        past = time.time() - 100 * 3600
        os.utime(stale, (past, past))
        return inner(*a, **k)

    monkeypatch.setattr(gsplat_trainer, "train", train_and_age)
    srv = server(make_bundle(tmp_path), key=KEY)
    runner, _ = run(srv, tmp_path)
    assert runner.outcomes == ["succeeded"] and not stale.exists()
