"""The runner's failure paths against the fake server: a bundle that stays damaged fails the job for good, one
too large for the runner is handed back before any download, a 410 "over" drops the job's checkpoints while a
requeue keeps them, a job given up on is reported (not left to the lease), and a shutdown tells the server the
newest checkpoint it would resume from."""
import os

import pytest
from fake_server import FakeServer
from test_gpurunner import KEY, CAPS, brush_ok, fast, make_bundle, run_runner, server  # noqa: F401 - fixtures

from splatworker import brush, checkpoints, procs
from splatworker.gpurunner import client as http
from splatworker.gpurunner import job
from splatworker.gpurunner.client import Client
from splatworker.gpurunner.loop import Runner


def train_until_stopped(on_start=None):
    def train(bin_path, dataset, out, steps, edge, cache, log, report, *a, **k):
        report(0.1, "step 500/5000")
        if on_start:
            on_start()
        assert procs.current_stop.wait(5), "the training was never stopped"
        raise procs.ToolStopped("train", "Brush was stopped")

    return train


@pytest.fixture
def discarded(monkeypatch):
    dropped = []
    monkeypatch.setattr(checkpoints, "discard", lambda directory: dropped.append(directory))
    return dropped


def test_a_bundle_that_stays_damaged_fails_the_job_for_good(tmp_path, fast, server, monkeypatch):  # noqa: F811
    monkeypatch.setattr(brush, "train", lambda *a, **k: pytest.fail("trained a corrupt bundle"))
    srv = server(make_bundle(tmp_path))
    srv.job = lambda: {**FakeServer.job(srv), "bundleSha256": "0" * 64}
    runner, _ = run_runner(srv, tmp_path)
    assert runner.outcomes == ["failed"] and not srv.results
    assert srv.fails[0]["retryable"] is False
    assert f"checksum mismatch after {job.MAX_CORRUPT_DOWNLOADS} downloads" in srv.fails[0]["reason"]
    assert len([p for _, p, _, _ in srv.requests if p.endswith("/bundle")]) == job.MAX_CORRUPT_DOWNLOADS


def test_a_bundle_too_large_for_the_runner_goes_back_without_a_download(tmp_path, fast, server):  # noqa: F811
    srv = server(make_bundle(tmp_path))
    srv.job = lambda: {**FakeServer.job(srv), "bundleBytes": http.MAX_BUNDLE_BYTES + 1}
    runner, _ = run_runner(srv, tmp_path)
    assert runner.outcomes == ["failed"] and srv.fails[0]["retryable"] is True
    assert "larger than this runner downloads" in srv.fails[0]["reason"]
    assert not [p for _, p, _, _ in srv.requests if p.endswith("/bundle")]


@pytest.mark.parametrize("reason,outcome", [("over", "cancelled"), ("requeued", "gone"), (None, "gone")])
def test_a_410_over_drops_the_checkpoints_a_requeue_keeps_them(tmp_path, fast, server, monkeypatch, discarded,  # noqa: F811
                                                               reason, outcome):
    monkeypatch.setattr(brush, "train", train_until_stopped())
    srv = server(make_bundle(tmp_path))
    srv.gone_on_stage, srv.gone_reason = "train", reason
    runner, _ = run_runner(srv, tmp_path)
    assert runner.outcomes == [outcome] and not srv.fails
    assert (len(discarded) == 1) == (outcome == "cancelled")


def test_a_410_over_on_the_bundle_is_a_cancel(tmp_path, fast, server, monkeypatch, discarded):  # noqa: F811
    monkeypatch.setattr(brush, "train", lambda *a, **k: pytest.fail("no bundle, no training"))
    srv = server(make_bundle(tmp_path))
    srv.errors["bundle"] = [(410, {"title": "Gone", "reason": "over"}, {})]
    runner, _ = run_runner(srv, tmp_path)
    assert runner.outcomes == ["cancelled"] and len(discarded) == 1 and not srv.fails


def test_a_job_given_up_on_is_reported_as_a_retryable_failure(tmp_path, fast, server, monkeypatch):  # noqa: F811
    monkeypatch.setattr(brush, "train", brush_ok)
    monkeypatch.setattr(job, "UPLOAD_PATIENCE_S", -1)
    srv = server(make_bundle(tmp_path))
    srv.errors["result"] = [(503, {"title": "down"}, {})]
    runner, _ = run_runner(srv, tmp_path)
    assert runner.outcomes == ["abandoned"] and not srv.results
    assert srv.fails[0]["retryable"] is True and srv.fails[0]["shutdown"] is False
    assert srv.fails[0]["reason"].startswith("the runner gave up: upload")


def test_a_shutdown_tells_the_server_its_newest_checkpoint(tmp_path, fast, server, monkeypatch):  # noqa: F811
    srv = server(make_bundle(tmp_path))
    work = tmp_path / "w"
    runner = Runner(Client(srv.url, KEY), str(work), capabilities=lambda: dict(CAPS))
    ck = checkpoints.job_dir(str(work / "checkpoints"), srv.job_id, srv.job()["bundleSha256"])

    def save_and_stop():
        os.makedirs(ck, exist_ok=True)
        for step in (5000, 10000):
            open(checkpoints.paths(ck, step)[0], "wb").close()
        checkpoints.commit(ck, 10000, "sig")  # drops ckpt-5000
        runner.shutdown.set()  # SIGTERM

    monkeypatch.setattr(brush, "train", train_until_stopped(save_and_stop))
    assert runner.run() == 0 and runner.outcomes == ["shutdown"]
    assert srv.fails == [{"reason": "the runner was shut down", "retryable": True, "shutdown": True,
                          "checkpointStep": 10000}]
    assert os.path.exists(checkpoints.paths(ck, 10000)[0])  # kept to resume


def test_newest_step_needs_a_complete_checkpoint(tmp_path):
    d = str(tmp_path / "ck")
    assert checkpoints.newest_step(d) is None and checkpoints.newest_step(None) is None
    os.makedirs(d)
    open(checkpoints.paths(d, 5000)[0], "wb").close()
    assert checkpoints.newest_step(d) is None  # a .pt without its .json is torn
    checkpoints.commit(d, 5000, "sig")
    open(checkpoints.paths(d, 10000)[0], "wb").close()
    assert checkpoints.newest_step(d) == 5000
