"""The 3D runner against a fake Blocwerk server (fake_server.py); the trainers replaced by stubs writing a
tiny .ply. Covers the contract: hello caps, claim, resumable bundle download, progress, gzip upload, fail
(retryable / shutdown), 401 -> exit 3, 404/410 -> drop the job, 429 + Retry-After."""
import gzip
import json
import os
import threading
import time

import numpy as np
import pytest
from fake_server import FakeServer
from PIL import Image

from computejobs.child import JobError
from computejobs.settings import settings
from splatworker import brush, bundle, gpu, gsplat_trainer, procs, profiles
from splatworker.gpurunner import client as http
from splatworker.gpurunner import heartbeat, job, train
from splatworker.gpurunner.client import Client
from splatworker.gpurunner.loop import EXIT_UNAUTHORIZED, Runner
from splatworker.splatio import read_ply

CAPS = {"runnerVersion": "t", "gpuName": "Test GPU", "vramMb": 8192, "maxQuality": "high", "memoryBudgetMb": 4096,
        "platform": "test", "trainer": "brush", "cuda": False}
KEY = "bwr_" + "a" * 64
ZONES = {"version": 1, "facets": [{"id": "0", "o": [0, 0, 0], "u": [1, 0, 0], "v": [0, 0, 1], "n": [0, -1, 0],
                                   "ext": [0, 2000, 0, 1000]}], "floorMm": 0, "boxLo": [-400, -400, -150],
         "boxHi": [2400, 400, 1400], "params": {"slab_margin_mm": 100}, "toWorldMm": np.eye(4).tolist()}


def full_ply(path, n=5):
    """A trainer-like export with SH rest columns the slim upload must drop."""
    names = ["x", "y", "z", "nx", "ny", "nz", "f_dc_0", "f_dc_1", "f_dc_2", *[f"f_rest_{i}" for i in range(9)],
             "opacity", "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3"]
    head = f"ply\nformat binary_little_endian 1.0\nelement vertex {n}\n"
    head += "".join(f"property float {k}\n" for k in names) + "end_header\n"
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as fh:
        fh.write(head.encode() + np.random.default_rng(1).normal(size=(n, len(names))).astype("<f4").tobytes())
    return path


class Parser:
    step, splats, took, peak_vram_mb, eval, oom = 5000, 5, "1s", 9000, None, None
    zones = {"wall": 4, "surround": 1, "outside": 0}


def make_bundle(tmp_path, quality="draft", zones=None):
    ds = tmp_path / "src" / "dataset"
    (ds / "images" / "g").mkdir(parents=True, exist_ok=True)
    (ds / "sparse" / "0").mkdir(parents=True, exist_ok=True)
    Image.new("RGB", (64, 48), (100, 20, 20)).save(ds / "images" / "g" / "p01.jpg")
    (ds / "sparse" / "0" / "cameras.bin").write_bytes(b"\x00" * 16)
    out = tmp_path / "src" / "bundle.zip"
    bundle.build_bundle(str(ds), str(out), bundle.train_doc(profiles.PROFILES[quality]), lambda *a: None, zones)
    return out.read_bytes()


@pytest.fixture
def fast(monkeypatch):
    waits = []
    monkeypatch.setattr(http, "pause", lambda event, s: waits.append(s))
    monkeypatch.setattr(heartbeat, "MIN_INTERVAL_S", 0.0)
    monkeypatch.setattr(heartbeat, "TICK_S", 0.02)
    monkeypatch.setattr(train.RunnerSfm, "train_budget_mb", lambda self: setattr(self, "brush_budget_mb", 16000) or 16000)
    monkeypatch.setattr(settings, "splat_trainer", "brush")
    monkeypatch.setattr(settings, "profile_override", "")
    return waits


@pytest.fixture
def server():
    servers = []

    def make(data=b"", **kw):
        s = FakeServer(data, **kw)
        servers.append(s)
        return s

    yield make
    for s in servers:
        s.close()


def brush_ok(bin_path, dataset, out, steps, edge, cache, log, report, *a, **k):
    report(0.5, "step 2500/5000")
    time.sleep(0.15)  # let the heartbeat post it
    return full_ply(f"{out}/splat_{steps}.ply"), Parser()


def run_runner(srv, tmp_path, max_jobs=1, gzip_upload=True):
    r = Runner(Client(srv.url, KEY, gzip_upload), str(tmp_path / "work"), capabilities=lambda: dict(CAPS),
               max_jobs=max_jobs)
    return r, r.run()


def test_happy_path_trains_through_the_worker_path_and_uploads_a_gzipped_slim_ply(tmp_path, fast, server,
                                                                                    monkeypatch):
    monkeypatch.setattr(brush, "train", brush_ok)
    srv = server(make_bundle(tmp_path), key=KEY)
    runner, code = run_runner(srv, tmp_path)
    assert code == 0 and runner.outcomes == ["succeeded"]
    res = srv.results[0]
    assert res["headers"]["Content-Encoding"] == "gzip" and int(res["headers"]["Content-Length"]) == len(res["raw"])
    stats = json.loads(res["headers"]["X-Blocwerk-Stats"])
    assert stats["quality"] == "draft" and stats["runnerGpu"] == "Test GPU" and stats["steps"] == 5000
    assert stats["trainer"] == "brush" and stats["zoned"] is False and stats["trainMemoryBudgetMb"] == 16000
    assert all(not isinstance(v, dict) for v in stats.values())  # the server drops non-flat stats
    (tmp_path / "r.ply").write_bytes(res["body"])
    cols = read_ply(str(tmp_path / "r.ply"))
    assert len(cols) == 14 and "f_rest_0" not in cols and len(cols["x"]) == 5
    assert srv.claims[0] == {"maxQuality": "high"} and srv.hellos[0]["trainer"] == "brush"
    assert any(p.get("step") == 2500 and p["totalSteps"] == 5000 and p["stage"] == "train" for p in srv.progress)
    assert [p.name for p in (tmp_path / "work").iterdir()] == []  # job dir and alive file are gone


@pytest.mark.parametrize("vram,trained", [(16376, "ultra"), (8192, "max")])
def test_gsplat_trains_with_the_bundles_zones_and_the_vram_gate(tmp_path, fast, server, monkeypatch, vram, trained):
    seen = {}

    def fake_train(python, dataset, out, plan, log, report, *a, zones=None, **k):
        seen.update(zones=zones, plan=plan, stop=procs.current_stop)
        return full_ply(f"{out}/splat.ply"), Parser()

    monkeypatch.setattr(settings, "splat_trainer", "gsplat")
    monkeypatch.setattr(gpu, "vram", lambda: {"name": "RTX", "totalMb": vram, "freeMb": vram - 500})
    monkeypatch.setattr(gsplat_trainer, "train", fake_train)
    monkeypatch.setattr(gsplat_trainer, "check_frame", lambda *a: {"spreadRatio": 1.1, "splatMedian": [0, 0, 0]})
    srv = server(make_bundle(tmp_path, "ultra", ZONES), quality="ultra")
    runner, code = run_runner(srv, tmp_path)
    assert runner.outcomes == ["succeeded"]
    assert seen["zones"].endswith("zones.json") and seen["plan"].profile.name == trained
    assert seen["stop"] is not None  # a cancel / shutdown kills gsplat (procs.current_stop)
    stats = json.loads(srv.results[0]["headers"]["X-Blocwerk-Stats"])
    assert stats["trainer"] == "gsplat" and stats["zoned"] is True and stats["zonesWall"] == 4
    assert stats["quality"] == trained and stats["qualityRequested"] == "ultra"
    assert stats["frameCheckSpreadRatio"] == 1.1 and "frameCheck" not in stats and "zones" not in stats
    assert (stats["profileNote"] is None) == (trained == "ultra")


def test_uncompressed_upload_and_the_415_fallback(tmp_path, fast, server, monkeypatch):
    monkeypatch.setattr(brush, "train", brush_ok)
    srv = server(make_bundle(tmp_path))
    run_runner(srv, tmp_path, gzip_upload=False)
    assert "Content-Encoding" not in srv.results[0]["headers"] and srv.results[0]["body"].startswith(b"ply\n")
    srv2 = server(make_bundle(tmp_path))
    srv2.errors["result"] = [(415, {"title": "Unsupported"}, {})]
    runner, _ = run_runner(srv2, tmp_path)
    assert runner.outcomes == ["succeeded"] and "Content-Encoding" not in srv2.results[0]["headers"]


def test_an_interrupted_download_resumes_with_a_range_request(tmp_path, fast, server, monkeypatch):
    monkeypatch.setattr(brush, "train", brush_ok)
    data = make_bundle(tmp_path)
    srv = server(data)
    srv.drop_bundle_after = 100
    runner, _ = run_runner(srv, tmp_path)
    assert runner.outcomes == ["succeeded"]
    ranges = [h.get("Range") for m, p, h, _ in srv.requests if p.endswith("/bundle")]
    assert ranges == [None, "bytes=100-"]


def test_bundle_checksum_mismatch_is_not_trained(tmp_path, fast, server, monkeypatch):
    monkeypatch.setattr(brush, "train", lambda *a, **k: pytest.fail("trained a corrupt bundle"))
    monkeypatch.setattr(job, "NETWORK_PATIENCE_S", -1)
    srv = server(make_bundle(tmp_path))
    srv.job = lambda: {**FakeServer.job(srv), "bundleSha256": "0" * 64}
    runner, _ = run_runner(srv, tmp_path)
    assert runner.outcomes == ["abandoned"] and not srv.results


def test_410_while_training_drops_the_job_and_the_loop_goes_on(tmp_path, fast, server, monkeypatch):
    killed = threading.Event()

    def slow_train(bin_path, dataset, out, steps, edge, cache, log, report, *a, **k):
        report(0.1, "step 500/5000")
        assert procs.current_stop.wait(5), "the 410 never reached the training"
        killed.set()
        raise procs.ToolStopped("train", "Brush was stopped")

    monkeypatch.setattr(brush, "train", slow_train)
    srv = server(make_bundle(tmp_path))
    srv.gone_on_stage = "train"
    srv.jobs_left = 2
    runner, code = run_runner(srv, tmp_path, max_jobs=2)
    assert killed.is_set() and code == 0 and runner.outcomes == ["gone", "gone"]
    assert not srv.results and not srv.fails


def test_410_on_the_bundle_drops_the_job(tmp_path, fast, server, monkeypatch):
    monkeypatch.setattr(brush, "train", lambda *a, **k: pytest.fail("no bundle, no training"))
    srv = server(make_bundle(tmp_path))
    srv.errors["bundle"] = [(410, {"title": "Gone"}, {})]
    runner, _ = run_runner(srv, tmp_path)
    assert runner.outcomes == ["gone"] and not srv.fails


@pytest.mark.parametrize("route", ["hello", "claim"])
def test_401_exits_3(tmp_path, fast, server, route):
    srv = server(make_bundle(tmp_path))
    srv.errors[route] = [(401, {"title": "Unauthorized"}, {})]
    _, code = run_runner(srv, tmp_path)
    assert code == EXIT_UNAUTHORIZED


def test_wrong_key_is_refused_with_exit_3(tmp_path, fast, server):
    srv = server(make_bundle(tmp_path), key="bwr_" + "b" * 64)
    _, code = run_runner(srv, tmp_path)
    assert code == EXIT_UNAUTHORIZED


def test_backoff_honours_retry_after(tmp_path, fast, server, monkeypatch):
    srv = server(make_bundle(tmp_path))
    srv.jobs_left = 0
    srv.errors["hello"] = [(503, {"e": 1}, {})]
    srv.errors["claim"] = [(429, {"e": 1}, {"Retry-After": "30"})]
    runner = Runner(Client(srv.url, KEY), str(tmp_path / "w"), capabilities=lambda: dict(CAPS))
    real_claim = Client.claim

    def claim(self, q):
        if len(srv.claims) >= 1:
            runner.shutdown.set()
        return real_claim(self, q)

    monkeypatch.setattr(Client, "claim", claim)
    assert runner.run() == 0
    assert 0.8 <= fast[0] <= 1.2 and fast[1] >= 30


def test_shutdown_hands_the_job_back_without_using_an_attempt(tmp_path, fast, server, monkeypatch):
    def train_until_stopped(bin_path, dataset, out, steps, edge, cache, log, report, *a, **k):
        report(0.2, "step 1000/5000")
        runner.shutdown.set()  # SIGTERM
        assert procs.current_stop.wait(5)
        raise procs.ToolStopped("train", "Brush was stopped")

    monkeypatch.setattr(brush, "train", train_until_stopped)
    srv = server(make_bundle(tmp_path))
    runner = Runner(Client(srv.url, KEY), str(tmp_path / "w"), capabilities=lambda: dict(CAPS))
    assert runner.run() == 0 and runner.outcomes == ["shutdown"]
    assert srv.fails == [{"reason": "the runner was shut down", "retryable": True, "shutdown": True}]


def test_training_failure_is_reported_retryable(tmp_path, fast, server, monkeypatch):
    def broken(*a, **k):
        raise JobError("train", "Brush exited without exporting a splat")

    monkeypatch.setattr(brush, "train", broken)
    srv = server(make_bundle(tmp_path))
    runner, _ = run_runner(srv, tmp_path)
    assert runner.outcomes == ["failed"] and srv.fails[0]["retryable"] is True and srv.fails[0]["shutdown"] is False


def test_rejected_result_fails_for_good_and_a_busy_server_is_retried(tmp_path, fast, server, monkeypatch):
    monkeypatch.setattr(brush, "train", brush_ok)
    srv = server(make_bundle(tmp_path))
    srv.errors["result"] = [(413, {"title": "too large"}, {})]
    runner, _ = run_runner(srv, tmp_path)
    assert runner.outcomes == ["failed"] and srv.fails[0]["retryable"] is False
    srv2 = server(make_bundle(tmp_path))
    srv2.errors["result"] = [(429, {"t": 1}, {"Retry-After": "10"}), (507, {"t": 1}, {})]
    runner, _ = run_runner(srv2, tmp_path)
    assert runner.outcomes == ["succeeded"] and len(srv2.results) == 1 and 10 in fast


def test_trim_stats_keeps_a_flat_object_under_8_kb():
    doc = job.trim_stats({"quality": "max", "x": {"a": 1}, "retries": [{"stage": "train", "r": "m" * 500}] * 40})
    assert "x" not in doc and len(json.dumps(doc, separators=(",", ":"))) <= job.MAX_STATS_BYTES
    assert doc["quality"] == "max" and len(doc["retries"]) < 40


def test_gzip_file_round_trip(tmp_path):
    (tmp_path / "a").write_bytes(b"ply\n" + os.urandom(1000) + b"\0" * 100000)
    n = http.gzip_file(str(tmp_path / "a"), str(tmp_path / "a.gz"))
    assert n < 100000 and gzip.decompress((tmp_path / "a.gz").read_bytes()) == (tmp_path / "a").read_bytes()
