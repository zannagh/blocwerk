"""Protocol v1 via the shared package: health, auth, lifecycle, failure, cancel, limits, callbacks, bind."""
import json
import os
import signal
import threading
import time
from http.server import BaseHTTPRequestHandler, HTTPServer

import dummy
import pytest
from fastapi.testclient import TestClient

from computejobs.bind import check_bind
from computejobs.security import sign, verify
from computejobs.settings import settings

KEY = "k-123"


@pytest.fixture(scope="module")
def svc(tmp_path_factory):
    settings.work_dir = str(tmp_path_factory.mktemp("jobs"))
    settings.api_key = None
    s = dummy.make_service()
    with TestClient(s.app) as c:
        s.client = c
        yield s


@pytest.fixture
def client(svc):
    return svc.client


def wait(client, job_id, timeout=30):
    t0, last = time.time(), 0.0
    while time.time() - t0 < timeout:
        st = client.get(f"/v1/jobs/{job_id}").json()
        assert st["progress"] >= last  # monotone
        last = st["progress"]
        if st["status"] in ("succeeded", "failed", "cancelled"):
            return st
        time.sleep(0.2)
    raise AssertionError("job did not finish")


def test_health(client):
    h = client.get("/health").json()
    assert h["protocol"] == "blocwerk-compute/1" and h["service"] == "dummy"
    assert h["kinds"] == ["echo"] and set(h) == {"status", "service", "protocol", "version", "kinds"}
    info = client.get("/v1/info").json()
    assert info["auth"] is False and info["backend"] == "test" and "gitSha" in info
    assert info["jobs"] == {"queued": 0, "running": 0} or "queued" in info["jobs"]


def test_lifecycle_and_files(client):
    r = client.post("/v1/jobs/echo", json={"echo": "hi"})
    assert r.status_code == 202 and r.json()["status"] == "queued"
    st = wait(client, r.json()["jobId"])
    assert st["status"] == "succeeded" and st["progress"] == 1.0 and st["result"]["echo"] == "hi"
    for k in ("jobId", "kind", "stage", "stageDetail", "message", "error", "createdAt", "updatedAt"):
        assert k in st
    f = st["result"]["files"][0]
    assert f["name"] == "out.json"
    got = client.get(f["url"])
    assert got.status_code == 200 and got.headers["content-type"] == "application/json"
    assert json.loads(got.content) == {"echo": "hi"}
    assert client.get(f"/v1/jobs/{st['jobId']}/files/in.json").status_code == 404  # inputs never served
    assert client.get(f"/v1/jobs/{st['jobId']}/files/..%2Fx").status_code == 404


def test_failure_reports_stage_and_reason(client):
    st = wait(client, client.post("/v1/jobs/echo", json={"mode": "fail"}).json()["jobId"])
    assert st["status"] == "failed" and st["error"] == "sfm-mapping: only 3/14 images registered"
    st = wait(client, client.post("/v1/jobs/echo", json={"mode": "invalid"}).json()["jobId"])
    assert st["status"] == "failed" and st["error"].startswith("invalid input:")


def start_grandchild_job(client):
    job = client.post("/v1/jobs/echo", json={"mode": "grandchild"}).json()["jobId"]
    pid_file = os.path.join(settings.work_dir, job, "grandchild.pid")
    t0 = time.time()
    while not os.path.exists(pid_file) and time.time() - t0 < 20:
        time.sleep(0.1)
    time.sleep(0.2)
    grandchild, child = map(int, open(pid_file).read().split())
    return job, grandchild, child


def assert_dies(pid):
    t0 = time.time()
    while time.time() - t0 < 5:
        try:
            os.kill(pid, 0)
        except ProcessLookupError:
            return
        time.sleep(0.1)
    pytest.fail("the job's subprocess survived")


def test_cancel_kills_the_whole_process_group(client):
    job, grandchild, _ = start_grandchild_job(client)
    assert client.delete(f"/v1/jobs/{job}").status_code == 200
    assert wait(client, job)["status"] == "cancelled"
    assert_dies(grandchild)


def test_sigterm_to_the_job_process_takes_its_tools_down(client):
    """What multiprocessing does to daemonic children when the service shuts down."""
    job, grandchild, child = start_grandchild_job(client)
    os.kill(child, signal.SIGTERM)
    st = wait(client, job)
    assert st["status"] == "failed" and "worker process died" in st["error"]
    assert_dies(grandchild)


def test_timeout(client, svc, monkeypatch):
    monkeypatch.setattr(svc.manager, "timeout_for", lambda kind: 1)
    st = wait(client, client.post("/v1/jobs/echo", json={"mode": "sleep", "seconds": 30}).json()["jobId"])
    assert st["status"] == "failed" and "timed out after 1 s" in st["error"]


def test_unknown_kind_elsewhere_and_unknown_job(client):
    assert client.post("/v1/jobs/nope", json={}).status_code == 404
    r = client.post("/v1/jobs/splat", json={})
    assert r.status_code == 501 and "splat-worker" in r.json()["detail"]
    assert client.get("/v1/jobs/deadbeef").status_code == 404
    assert client.delete("/v1/jobs/deadbeef").status_code == 404
    assert client.get("/v1/jobs/deadbeef/files/x.json").status_code == 404


def test_request_too_large(client, monkeypatch):
    monkeypatch.setattr(settings, "max_request_bytes", 1024)
    r = client.post("/v1/jobs/echo", content=b"x" * 4096, headers={"content-type": "application/json"})
    assert r.status_code == 413


def test_queue_full(client, monkeypatch):
    monkeypatch.setattr(settings, "max_queued", 0)
    assert client.post("/v1/jobs/echo", json={}).status_code == 429


def test_auth(client, monkeypatch):
    monkeypatch.setattr(settings, "api_key", KEY)
    assert client.get("/v1/jobs/deadbeef").status_code == 401
    assert client.get("/v1/jobs/deadbeef", headers={"Authorization": "Bearer nope"}).status_code == 401
    r = client.get("/v1/jobs/deadbeef", headers={"Authorization": KEY})
    assert r.status_code == 401 and r.headers["www-authenticate"] == "Bearer"
    assert client.post("/v1/jobs/echo", json={}).status_code == 401
    assert client.get("/v1/jobs/deadbeef", headers={"Authorization": f"Bearer {KEY}"}).status_code == 404
    assert client.get("/health").status_code == 200 and client.get("/v1/info", headers={"Authorization": f"Bearer {KEY}"}).json()["auth"] is True


def test_signature_vector():
    body = b'{"jobId":"abc","status":"succeeded"}'
    sig = sign(body, "s3cret")
    assert sig == "sha256=8498b6c0d99f7e79edbb127d423ecbc4aeec6cf6696247b164d40e527b298e60"
    assert verify(body, "s3cret", sig) and not verify(body + b" ", "s3cret", sig)


def test_callbacks_signed(client, monkeypatch):
    got = []

    class H(BaseHTTPRequestHandler):
        def do_POST(self):
            got.append((self.rfile.read(int(self.headers["Content-Length"])), self.headers["X-Blocwerk-Signature"]))
            self.send_response(204)
            self.end_headers()

        def log_message(self, *a):
            pass

    srv = HTTPServer(("127.0.0.1", 0), H)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    monkeypatch.setattr(settings, "callback_secret", "cb")
    monkeypatch.setattr(settings, "callback_allow_private", True)  # the receiver is on loopback
    job = client.post("/v1/jobs/echo", json={"callbackUrl": f"http://127.0.0.1:{srv.server_port}/cb"}).json()["jobId"]
    wait(client, job)
    t0 = time.time()
    while time.time() - t0 < 10 and not any(json.loads(b)["status"] == "succeeded" for b, _ in got):
        time.sleep(0.1)
    srv.shutdown()
    statuses = [json.loads(b)["status"] for b, _ in got]
    assert statuses[0] == "queued" and "running" in statuses and "succeeded" in statuses
    assert all(verify(b, "cb", s) for b, s in got)


@pytest.mark.parametrize("host, key, allow, ok", [
    ("127.0.0.1", None, False, True), ("::1", None, False, True), ("localhost", None, False, True),
    ("0.0.0.0", None, False, False), ("192.168.1.5", None, False, False),
    ("0.0.0.0", "k", False, True), ("0.0.0.0", None, True, True)])
def test_bind_guard(host, key, allow, ok):
    assert (check_bind(host, key, allow) is None) == ok
