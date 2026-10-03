"""The runner's command line (key handling, URL policy), its capabilities and its container health check."""
import os
import time

import pytest

from splatworker.gpurunner import __main__ as cli
from splatworker.gpurunner import alive, caps, health
from splatworker.gpurunner.client import check_server

KEY = "bwr_" + "a" * 64


def test_the_key_comes_from_the_environment_only_and_is_popped(monkeypatch):
    monkeypatch.delenv("BWR_KEY", raising=False)
    with pytest.raises(SystemExit, match="BWR_KEY"):
        cli.main(["--server", "http://localhost:1"])
    with pytest.raises(SystemExit, match="refusing --key"):
        cli.main(["--server", "http://localhost:1", "--key", KEY])
    monkeypatch.setenv("BWR_KEY", KEY)
    with pytest.raises(SystemExit, match="plain http"):
        cli.main(["--server", "http://blocwerk.app"])
    assert "BWR_KEY" not in os.environ  # popped before anything else runs: no child process inherits it


def test_https_only_unless_local():
    with pytest.raises(ValueError, match="plain http"):
        check_server("http://blocwerk.app")
    assert check_server("https://blocwerk.app/") == "https://blocwerk.app"
    assert check_server("http://127.0.0.1:5000") == "http://127.0.0.1:5000"
    assert check_server("http://10.0.0.2", insecure_http=True) == "http://10.0.0.2"
    with pytest.raises(ValueError):
        check_server("ftp://blocwerk.app")


def test_a_missing_trainer_exits_2(monkeypatch):
    monkeypatch.setenv("BWR_KEY", KEY)
    monkeypatch.setattr(caps.Capabilities, "__init__", lambda self: setattr(self, "version", None) or
                        setattr(self, "trainer", "gsplat"))
    assert cli.main(["--server", "https://blocwerk.app"]) == 2


def test_max_quality_ultra_only_with_gsplat_on_12_gb(monkeypatch):
    monkeypatch.delenv("RUNNER_MAX_QUALITY", raising=False)
    assert caps.max_quality("gsplat", 16376, 8000) == "ultra"
    assert caps.max_quality("gsplat", 12282, 8000) == "ultra"  # a "12 GB" card as nvidia-smi reports it
    assert caps.max_quality("gsplat", 11264, 64000) == "max"
    assert caps.max_quality("gsplat", 4096, 64000) == "high" and caps.max_quality("gsplat", None, 64000) == "draft"
    assert caps.max_quality("brush", 24000, 64000) == "max"  # Brush never trains ultra
    assert caps.max_quality("brush", None, 6000) == "high" and caps.max_quality("brush", None, 3000) == "draft"
    assert caps.max_quality("gsplat", 16376, 8000, "high") == "high"
    monkeypatch.setenv("RUNNER_MAX_QUALITY", "max")
    assert caps.max_quality("gsplat", 16376, 8000) == "max"


def test_capabilities_report_the_trainer_and_cuda(monkeypatch):
    monkeypatch.setattr(caps.trainers, "select", lambda: "gsplat")
    monkeypatch.setattr(caps.gsplat_trainer, "tool_version", lambda p: "gsplat 1.5.3, torch 2.4.1, RTX")
    monkeypatch.setattr(caps, "gpu_info", lambda: ("RTX", 16376))
    monkeypatch.setattr(caps, "budget_mb", lambda: 20000)
    doc = caps.Capabilities()()
    assert doc["trainer"] == "gsplat" and doc["cuda"] is True and doc["vramMb"] == 16376
    assert doc["maxQuality"] == "ultra" and "brushVersion" not in doc


def test_health_follows_the_alive_file_and_falls_back_to_the_worker(tmp_path, monkeypatch):
    monkeypatch.setenv("RUNNER_WORK_DIR", str(tmp_path))
    monkeypatch.setattr(health, "worker_healthy", lambda: False)
    assert health.main() == 1  # no runner here, no worker either
    monkeypatch.setattr(health, "worker_healthy", lambda: True)
    assert health.main() == 0
    a = alive.Alive(str(tmp_path))
    a.touch()
    monkeypatch.setattr(health, "worker_healthy", lambda: pytest.fail("a runner is not probed over HTTP"))
    assert health.main() == 0
    old = time.time() - alive.MAX_AGE_S - 5
    os.utime(alive.path(str(tmp_path)), (old, old))
    assert health.main() == 1
