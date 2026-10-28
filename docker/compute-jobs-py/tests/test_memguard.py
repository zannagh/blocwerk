"""The per-job memory guard (JOB_MAX_MEMORY_MB): a job that outgrows it ends as a resource failure, the service lives on."""
import os

import pytest
from test_protocol import client, svc, wait  # noqa: F401 - shared fixtures

from computejobs import memguard
from computejobs.settings import settings


@pytest.fixture
def limit():
    before = settings.job_max_memory_mb
    settings.job_max_memory_mb = 256
    yield 256
    settings.job_max_memory_mb = before


@pytest.mark.skipif(not os.path.exists("/proc/self/statm"), reason="the guard reads /proc (Linux, the images)")
def test_guard_stops_a_hog_as_a_resource_failure(client, limit):  # noqa: F811
    st = wait(client, client.post("/v1/jobs/echo", json={"mode": "hog", "mb": 2048}).json()["jobId"])
    assert st["status"] == "failed" and st["failureKind"] == "resource"
    assert f"({limit} MB)" in st["error"] and "more memory" in st["error"]
    # the service is alive and runs the next job normally
    ok = wait(client, client.post("/v1/jobs/echo", json={"echo": "after"}).json()["jobId"])
    assert ok["status"] == "succeeded" and "failureKind" not in ok and ok["result"]["echo"] == "after"


def test_a_job_the_kernel_kills_is_a_resource_failure(client, monkeypatch):  # noqa: F811
    kills = iter(range(100))
    monkeypatch.setattr(memguard, "oom_kills", lambda: next(kills))  # the cgroup's count goes up: the kernel did it
    st = wait(client, client.post("/v1/jobs/echo", json={"mode": "oom"}).json()["jobId"])
    assert st["status"] == "failed" and st["failureKind"] == "resource" and "more memory" in st["error"]


def test_other_failures_keep_no_failure_kind(client, monkeypatch):  # noqa: F811
    st = wait(client, client.post("/v1/jobs/echo", json={"mode": "fail"}).json()["jobId"])
    assert st["status"] == "failed" and "failureKind" not in st
    # a SIGKILL without an OOM kill in the cgroup (the job's own SIGTERM handler, someone else) stays a plain failure
    monkeypatch.setattr(memguard, "oom_kills", lambda: 0)
    st = wait(client, client.post("/v1/jobs/echo", json={"mode": "oom"}).json()["jobId"])
    assert st["status"] == "failed" and "failureKind" not in st and "worker process died" in st["error"]


def test_group_rss_counts_this_process():
    rss = memguard.group_rss_bytes(os.getpgrp())
    if rss is None:
        pytest.skip("no /proc")
    assert rss > 1 << 20 and memguard.over_limit(os.getpgrp(), 0) is None
    assert memguard.over_limit(os.getpgrp(), 1) is not None
