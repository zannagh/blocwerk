"""The 3D runner loop against a fake Blocwerk server (Brush replaced by a stub writing a tiny .ply)."""
import json
import threading

import numpy as np
import pytest
from fake_server import FakeServer
from PIL import Image

from splatworker import brush, bundle, profiles
from splatworker.gpurunner import __main__ as cli
from splatworker.gpurunner import caps, heartbeat
from splatworker.gpurunner import client as http
from splatworker.gpurunner.client import Client, check_server
from splatworker.gpurunner.loop import EXIT_UNAUTHORIZED, Runner
from splatworker.procs import ToolStopped
from splatworker.splatio import read_ply

CAPS = {"runnerVersion": "t", "gpuName": "Test GPU", "vramMb": 8192, "maxQuality": "draft",
        "memoryBudgetMb": 4096, "platform": "test", "brushVersion": "v0"}
KEY = "bwr_" + "a" * 64


def full_ply(path, n=5):
    """A Brush-like export: SH rest columns the slim upload must drop."""
    names = ["x", "y", "z", "nx", "ny", "nz", "f_dc_0", "f_dc_1", "f_dc_2", *[f"f_rest_{i}" for i in range(9)],
             "opacity", "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3"]
    head = f"ply\nformat binary_little_endian 1.0\nelement vertex {n}\n"
    head += "".join(f"property float {k}\n" for k in names) + "end_header\n"
    data = np.random.default_rng(1).normal(size=(n, len(names))).astype("<f4")
    with open(path, "wb") as fh:
        fh.write(head.encode() + data.tobytes())


class Parser:
    step, splats, took = 5000, 5, "1s"


@pytest.fixture
def bundle_bytes(tmp_path):
    ds = tmp_path / "dataset"
    (ds / "images" / "g").mkdir(parents=True)
    (ds / "sparse" / "0").mkdir(parents=True)
    Image.new("RGB", (64, 48), (100, 20, 20)).save(ds / "images" / "g" / "p01.jpg")
    (ds / "sparse" / "0" / "cameras.bin").write_bytes(b"\x00" * 16)
    out = tmp_path / "bundle.zip"
    bundle.build_bundle(str(ds), str(out), bundle.train_doc(profiles.PROFILES["draft"]), lambda *a: None)
    return out.read_bytes()


@pytest.fixture
def fast(monkeypatch):
    waits = []
    monkeypatch.setattr(http, "pause", lambda event, s: waits.append(s))
    monkeypatch.setattr(heartbeat, "MIN_INTERVAL_S", 0.0)
    monkeypatch.setattr(heartbeat, "TICK_S", 0.05)
    monkeypatch.setattr(caps, "budget_mb", lambda: 0)
    return waits


def run_once(server, tmp_path, max_jobs=1):
    r = Runner(Client(server.url, KEY), str(tmp_path / "work"), capabilities=lambda: dict(CAPS), max_jobs=max_jobs)
    return r, r.run()


def test_happy_path_trains_and_uploads_a_slim_ply(tmp_path, bundle_bytes, fast, monkeypatch):
    def fake_train(bin_path, dataset, out, steps, edge, *a, stop=None):
        assert steps == 5000 and edge == 1800 and stop is not None
        a[2](0.5, "step 2500/5000")  # report
        __import__("time").sleep(0.3)  # let the heartbeat post it
        __import__("os").makedirs(out, exist_ok=True)
        full_ply(f"{out}/splat_5000.ply")
        return f"{out}/splat_5000.ply", Parser()

    monkeypatch.setattr(brush, "train", fake_train)
    server = FakeServer(bundle_bytes)
    try:
        runner, code = run_once(server, tmp_path)
    finally:
        server.close()
    assert code == 0 and runner.outcomes == ["succeeded"]
    body, headers = server.results[0]
    assert headers["X-Blocwerk-Format"] == "ply"
    stats = json.loads(headers["X-Blocwerk-Stats"])
    assert stats["quality"] == "draft" and stats["runnerGpu"] == "Test GPU" and stats["steps"] == 5000
    (tmp_path / "r.ply").write_bytes(body)
    cols = read_ply(str(tmp_path / "r.ply"))
    assert len(cols) == 14 and "f_rest_0" not in cols and len(cols["x"]) == 5
    auth = {h.get("Authorization") for _, _, h, _ in server.requests}
    assert auth == {f"Bearer {KEY}"}
    assert any(p.get("step") == 2500 and p["totalSteps"] == 5000 for p in server.progress)
    assert not list((tmp_path / "work").iterdir())  # the job dir is gone


def test_bundle_checksum_mismatch_is_not_trained(tmp_path, bundle_bytes, fast, monkeypatch):
    monkeypatch.setattr(brush, "train", lambda *a, **k: pytest.fail("trained a corrupt bundle"))
    monkeypatch.setattr("splatworker.gpurunner.job.NETWORK_PATIENCE_S", -1)
    server = FakeServer(bundle_bytes)
    server.job = lambda: {**FakeServer.job(server), "bundleSha256": "0" * 64}
    try:
        runner, _ = run_once(server, tmp_path)
    finally:
        server.close()
    assert runner.outcomes == ["abandoned"] and not server.results


def test_cancel_kills_training_and_uploads_nothing(tmp_path, bundle_bytes, fast, monkeypatch):
    killed = threading.Event()

    def fake_train(bin_path, dataset, out, steps, edge, *a, stop=None):
        a[2](0.1, "step 500/5000")
        assert stop.wait(5), "the cancel never reached the training"
        killed.set()
        raise ToolStopped("train", "Brush was stopped (cancelled)")

    monkeypatch.setattr(brush, "train", fake_train)
    server = FakeServer(bundle_bytes)
    server.cancel_on_train = True
    try:
        runner, code = run_once(server, tmp_path)
    finally:
        server.close()
    assert killed.is_set() and code == 0 and runner.outcomes == ["cancelled"]
    assert not server.results and not server.fails


def test_training_failure_is_reported_retryable(tmp_path, bundle_bytes, fast, monkeypatch):
    from computejobs.child import JobError

    def fake_train(*a, **k):
        raise JobError("train", "Brush exited without exporting a splat")

    monkeypatch.setattr(brush, "train", fake_train)
    server = FakeServer(bundle_bytes)
    try:
        runner, _ = run_once(server, tmp_path)
    finally:
        server.close()
    assert runner.outcomes == ["failed"] and server.fails[0]["retryable"] is True


def test_backoff_on_errors_then_success_and_retry_after(tmp_path, bundle_bytes, fast, monkeypatch):
    monkeypatch.setattr(brush, "train", lambda *a, **k: pytest.fail("no job expected"))
    server = FakeServer(bundle_bytes)
    server.jobs_left = 0
    server.hello_errors = [(503, {"e": 1}, {}), (429, {"e": 1}, {"Retry-After": "30"})]
    server.claim_errors = [(502, None, {})]
    calls = {"n": 0}
    real_claim = Client.claim

    def claim(self, q):
        calls["n"] += 1
        if calls["n"] >= 3:
            runner.shutdown.set()
        return real_claim(self, q)

    monkeypatch.setattr(Client, "claim", claim)
    runner = Runner(Client(server.url, KEY), str(tmp_path / "w"), capabilities=lambda: dict(CAPS))
    try:
        assert runner.run() == 0
    finally:
        server.close()
    assert fast[0] >= 0.8 and fast[1] >= 30  # first backoff ~1 s, then the server's Retry-After
    assert calls["n"] >= 3


def test_connection_refused_backs_off_growing(tmp_path, fast):
    runner = Runner(Client("http://127.0.0.1:9", KEY), str(tmp_path / "w"), capabilities=lambda: dict(CAPS))
    real = http.pause

    def pause(event, s):
        real(event, s)
        if len(fast) >= 4:
            runner.shutdown.set()

    http.pause = pause
    try:
        assert runner.run() == 0
    finally:
        http.pause = real
    assert fast[3] > fast[0] and max(fast) <= 72


def test_unauthorized_exits_3(tmp_path, bundle_bytes, fast):
    server = FakeServer(bundle_bytes)
    server.hello_errors = [(401, {"error": "revoked"}, {})]
    try:
        _, code = run_once(server, tmp_path)
    finally:
        server.close()
    assert code == EXIT_UNAUTHORIZED


def test_cli_takes_the_key_from_the_environment_only(monkeypatch):
    monkeypatch.delenv("BWR_KEY", raising=False)
    with pytest.raises(SystemExit, match="BWR_KEY"):
        cli.main(["--server", "http://localhost:1"])
    with pytest.raises(SystemExit, match="refusing --key"):
        cli.main(["--server", "http://localhost:1", "--key", KEY])
    with pytest.raises(ValueError, match="plain http"):
        check_server("http://blocwerk.app")
    assert check_server("https://blocwerk.app/") == "https://blocwerk.app"


def test_max_quality_follows_the_budget_and_the_cap(monkeypatch):
    monkeypatch.delenv("RUNNER_MAX_QUALITY", raising=False)
    assert caps.max_quality(3000) == "draft" and caps.max_quality(6000) == "high" and caps.max_quality(20000) == "max"
    assert caps.max_quality(20000, "high") == "high" and caps.max_quality(3000, "max") == "draft"
