"""HTTP API: protocol v1 happy paths, validation, auth, callback signing, limits."""
import json
import time

import cv2
import numpy as np
import pytest
import synthetic
from fastapi.testclient import TestClient

from service import main
from service.security import sign, verify
from service.settings import settings

KEY = "test-key-123"


@pytest.fixture(scope="module")
def client(tmp_path_factory):
    settings.work_dir = str(tmp_path_factory.mktemp("jobs"))
    settings.api_key = None
    main.service.manager = None
    with TestClient(main.app) as c:
        yield c


@pytest.fixture
def keyed():
    settings.api_key = KEY
    yield {"Authorization": f"Bearer {KEY}"}
    settings.api_key = None


def _wait(client, job_id, timeout=240, headers=None):
    t0 = time.time()
    while time.time() - t0 < timeout:
        st = client.get(f"/v1/jobs/{job_id}", headers=headers or {}).json()
        assert 0.0 <= st["progress"] <= 1.0
        if st["status"] in ("succeeded", "failed", "cancelled"):
            return st
        time.sleep(0.5)
    raise AssertionError("job did not finish")


def test_health(client):
    h = client.get("/health").json()
    assert h["status"] == "ok" and h["protocol"] == "blocwerk-compute/1"
    assert set(h["kinds"]) == {"solve", "textures"} and "version" in h
    # nothing operational on the unauthenticated endpoint; that is in /v1/info (auth)
    assert set(h) == {"status", "service", "protocol", "version", "kinds"}
    info = client.get("/v1/info").json()
    assert "gitSha" in info and info["limits"]["maxImageMegapixels"] == 100


def test_solve_happy_path(client, capture1_request):
    r = client.post("/v1/jobs/solve", json=capture1_request)
    assert r.status_code == 202, r.text
    job = r.json()["jobId"]
    st = _wait(client, job)
    assert st["status"] == "succeeded", st["error"]
    geo = st["result"]["geometry"]
    assert {s["index"] for s in geo["segments"]} == {0, 1, 2, 5}
    assert st["result"]["files"][0]["name"] == "wall-geometry.json"
    f = client.get(st["result"]["files"][0]["url"])
    assert f.status_code == 200 and json.loads(f.content)["version"] == 1
    for k in ("createdAt", "updatedAt", "stage", "message", "error"):
        assert k in st


def test_textures_happy_path(client):
    doc, photo = synthetic.scene()
    ok, jpg = cv2.imencode(".jpg", photo, [cv2.IMWRITE_JPEG_QUALITY, 95])
    r = client.post("/v1/jobs/textures", files=[
        ("geometry", ("geometry.json", json.dumps(doc), "application/json")),
        ("photos", ("SYN_1.jpg", jpg.tobytes(), "image/jpeg")),
        ("photos", ("NOT_A_CAMERA.jpg", jpg.tobytes(), "image/jpeg"))])
    assert r.status_code == 202, r.text
    st = _wait(client, r.json()["jobId"])
    assert st["status"] == "succeeded", st["error"]
    facet = st["result"]["facets"][0]
    assert facet["markerCheck"]["detected"] == 1 and abs(facet["markerCheck"]["markers"][0]["sideErrMm"]) < 1.5
    url = next(f["url"] for f in st["result"]["files"] if f["name"] == facet["file"])
    img = client.get(url)
    assert img.status_code == 200 and img.headers["content-type"] == "image/jpeg"
    # the coverage mask: same grid as the texture, 8-bit gray PNG
    murl = next(f["url"] for f in st["result"]["files"] if f["name"] == facet["maskFile"])
    m = client.get(murl)
    assert m.status_code == 200 and m.headers["content-type"] == "image/png"
    mask = cv2.imdecode(np.frombuffer(m.content, np.uint8), cv2.IMREAD_UNCHANGED)
    assert mask.dtype == np.uint8 and mask.shape == (facet["heightPx"], facet["widthPx"])
    # the source-view map: which photo painted each label cell
    surl = next(f["url"] for f in st["result"]["files"] if f["name"] == facet["sourceFile"])
    src = json.loads(client.get(surl).content)
    assert src["cameras"] == ["SYN_1"] and src["rows"] * src["cellMm"] >= facet["heightPx"] * facet["mmPerPx"]


@pytest.mark.parametrize("body, code", [
    ({"markerSizeMm": 125}, 422),
    ({"markerSizeMm": 125, "segments": [{"index": 0}], "photos": [], "callbackUrl": "ftp://x"}, 400),
])
def test_solve_validation_errors(client, body, code):
    r = client.post("/v1/jobs/solve", json=body)
    assert r.status_code == code, r.text


def test_solve_rejects_non_json(client):
    assert client.post("/v1/jobs/solve", content=b"not json").status_code == 400


def test_textures_validation_errors(client):
    assert client.post("/v1/jobs/textures", json={}).status_code == 400
    doc, _ = synthetic.scene()
    r = client.post("/v1/jobs/textures", files=[
        ("geometry", ("g.json", json.dumps(doc), "application/json")),
        ("photos", ("OTHER.jpg", b"xx", "image/jpeg"))])
    assert r.status_code == 422
    r = client.post("/v1/jobs/textures", files=[
        ("geometry", ("g.json", json.dumps(doc), "application/json")),
        ("photos", ("../evil.jpg", b"xx", "image/jpeg"))])
    assert r.status_code in (400, 422)


def test_unknown_kind_and_splat(client):
    assert client.post("/v1/jobs/nope", json={}).status_code == 404
    r = client.post("/v1/jobs/splat", json={})
    assert r.status_code == 501 and "splat-worker" in r.json()["detail"]


def test_unknown_job_and_file(client):
    assert client.get("/v1/jobs/deadbeef").status_code == 404
    assert client.get("/v1/jobs/deadbeef/files/x.jpg").status_code == 404
    assert client.delete("/v1/jobs/deadbeef").status_code == 404


def test_cancel(client, capture1_request):
    job = client.post("/v1/jobs/solve", json=capture1_request).json()["jobId"]
    time.sleep(1.0)
    assert client.delete(f"/v1/jobs/{job}").status_code == 200
    assert _wait(client, job, timeout=30)["status"] == "cancelled"


def test_request_too_large(client, monkeypatch):
    monkeypatch.setattr(settings, "max_request_bytes", 1024)
    r = client.post("/v1/jobs/solve", content=b"x" * 4096, headers={"content-type": "application/json"})
    assert r.status_code == 413


def test_auth_required_when_key_set(client, keyed):
    assert client.get("/v1/jobs/deadbeef").status_code == 401
    assert client.get("/v1/jobs/deadbeef", headers={"Authorization": "Bearer wrong"}).status_code == 401
    assert client.get("/v1/jobs/deadbeef", headers={"Authorization": KEY}).status_code == 401
    assert client.post("/v1/jobs/solve", json={}).status_code == 401
    assert client.get("/v1/jobs/deadbeef", headers=keyed).status_code == 404  # authorised, just unknown
    assert client.post("/v1/jobs/solve", json={}, headers=keyed).status_code == 422
    assert client.get("/health").status_code == 200  # health stays open
    assert client.get("/v1/info").status_code == 401
    assert client.get("/v1/info", headers=keyed).json()["auth"] is True


def test_callback_signature_fixed_vector():
    body = b'{"jobId":"abc","status":"succeeded"}'
    sig = sign(body, "s3cret")
    # printf '%s' '{"jobId":"abc","status":"succeeded"}' | openssl dgst -sha256 -hmac s3cret
    assert sig == "sha256=8498b6c0d99f7e79edbb127d423ecbc4aeec6cf6696247b164d40e527b298e60"
    assert verify(body, "s3cret", sig)
    assert not verify(body + b" ", "s3cret", sig)
    assert not verify(body, "other", sig)


def test_callback_is_posted_signed(client, capture1_request, monkeypatch):
    """A job with callbackUrl POSTs signed status JSON on each status change (best effort)."""
    import threading
    from http.server import BaseHTTPRequestHandler, HTTPServer

    got = []

    class H(BaseHTTPRequestHandler):
        def do_POST(self):
            body = self.rfile.read(int(self.headers["Content-Length"]))
            got.append((body, self.headers["X-Blocwerk-Signature"]))
            self.send_response(204)
            self.end_headers()

        def log_message(self, *a):
            pass

    srv = HTTPServer(("127.0.0.1", 0), H)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    monkeypatch.setattr(settings, "callback_secret", "cb-secret")
    monkeypatch.setattr(settings, "callback_allow_private", True)  # the receiver is on loopback
    body = {**capture1_request, "callbackUrl": f"http://127.0.0.1:{srv.server_port}/cb"}
    job = client.post("/v1/jobs/solve", json=body).json()["jobId"]
    client.delete(f"/v1/jobs/{job}")  # queued or running -> cancelled; still two status changes
    t0 = time.time()
    while time.time() - t0 < 30 and not any(json.loads(b)["status"] == "cancelled" for b, _ in got):
        time.sleep(0.2)
    srv.shutdown()
    statuses = [json.loads(b)["status"] for b, _ in got]
    assert statuses[0] == "queued" and "cancelled" in statuses
    assert all(verify(b, "cb-secret", s) for b, s in got)
