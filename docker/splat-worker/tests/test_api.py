"""kind=splat upload/validation over HTTP (pipeline replaced by a no-op; protocol basics are covered
by docker/compute-jobs-py/tests)."""
import io
import json
import os
import time

import helpers
import pytest
from fastapi.testclient import TestClient
from PIL import Image
from test_ingest import assert_clean

from computejobs.settings import settings
from splatworker import main

KEY = "splat-test-key"
GEOMETRY = {"cameras": [{"image": "a", "R": [1, 0, 0, 0, 1, 0, 0, 0, 1], "t": [0, 0, 0]}], "segments": []}


@pytest.fixture(scope="module")
def client(tmp_path_factory):
    settings.work_dir = str(tmp_path_factory.mktemp("jobs"))
    settings.api_key = None
    svc = main.service
    svc.runner, svc.manager, svc.on_startup = helpers.noop_runner, None, None
    main.TOOLS.clear()
    main.TOOLS.update({"ok": True, "brush": {"version": "test"}, "colmap": {"version": "test"}})
    with TestClient(main.app) as c:
        yield c


def jpeg(color=(10, 200, 10), size=(40, 30)):
    buf = io.BytesIO()
    Image.new("RGB", size, color).save(buf, "JPEG")
    return buf.getvalue()


def form(photos, options=None, geometry=None):
    files = [("photos", (name, data, "image/jpeg")) for name, data in photos]
    if options is not None:
        files.append(("options", (None, json.dumps(options))))
    if geometry is not None:
        files.append(("geometry", ("g.json", json.dumps(geometry), "application/json")))
    return files


def three(prefix="p"):
    return [(f"{prefix}{i}.jpg", jpeg()) for i in range(3)]


def wait(client, job_id):
    for _ in range(100):
        st = client.get(f"/v1/jobs/{job_id}").json()
        if st["status"] in ("succeeded", "failed", "cancelled"):
            return st
        time.sleep(0.1)
    raise AssertionError("job did not finish")


def test_health_is_minimal_and_info_has_the_details(client, monkeypatch):
    h = client.get("/health").json()
    assert h["service"] == "splat-worker" and h["kinds"] == ["splat"] and h["protocol"] == "blocwerk-compute/1"
    assert set(h) == {"status", "service", "protocol", "version", "kinds"} and h["status"] == "ok"
    info = client.get("/v1/info").json()
    assert info["tools"]["ok"] is True and info["limits"]["maxPhotos"] == 400
    monkeypatch.setitem(main.TOOLS, "ok", False)
    assert client.get("/health").json()["status"] == "degraded"
    monkeypatch.setattr(settings, "api_key", KEY)
    assert client.get("/v1/info").status_code == 401 and client.get("/health").status_code == 200


def test_accepts_job_and_strips_metadata_on_arrival(client, gps_jpeg):
    photos = [("a.jpg", gps_jpeg), ("b.jpg", jpeg()), ("c.jpg", jpeg())]
    r = client.post("/v1/jobs/splat", files=form(photos, {"maxSteps": 5000}, GEOMETRY))
    assert r.status_code == 202, r.text
    job = r.json()["jobId"]
    assert wait(client, job)["status"] == "succeeded"
    d = os.path.join(settings.work_dir, job)
    stored = sorted(os.listdir(os.path.join(d, "arrived")))
    assert stored == ["a.jpg", "b.jpg", "c.jpg"]
    for name in stored:
        assert_clean(open(os.path.join(d, "arrived", name), "rb").read())
    inputs = json.load(open(os.path.join(d, "inputs.json")))
    assert inputs["options"] == {"quality": "high", "maxSteps": 5000, "maxImageEdge": None, "matcher": "auto",
                                 "cropMarginMm": 400.0, "spz": True, "colourMatch": True}
    assert inputs["photos"]["a"]["focal35"] == 14.0 and "TestPhone" not in json.dumps(inputs)
    assert json.load(open(os.path.join(d, "geometry.json"))) == GEOMETRY
    for root, _, files in os.walk(d):  # nothing anywhere in the job dir still holds the GPS tags
        for f in files:
            assert b"TestPhone" not in open(os.path.join(root, f), "rb").read()


@pytest.mark.parametrize("photos, options, geometry, code, text", [
    ([("bad name;.jpg", b"x")], None, None, 400, "file name"),
    ([("a.gif", b"x")], None, None, 400, "file name"),
    ([("a.jpg", b"x"), ("a.jpg", b"y")], None, None, 400, "twice"),
    ([("a.jpg", b"not a jpeg")], None, None, 422, "photo a"),
    (None, {"maxStep": 10}, None, 422, "unknown option"),
    (None, {"maxSteps": 5}, None, 422, "maxSteps"),
    (None, {"quality": "ultra"}, None, 422, "quality"),
    (None, {"matcher": "vocab"}, None, 422, "matcher"),
    (None, {"maxSteps": 1e12}, None, 422, "maxSteps"),
    (None, {"maxSteps": -1}, None, 422, "maxSteps"),
    (None, {"maxImageEdge": 100000}, None, 422, "maxImageEdge"),
    (None, {"cropMarginMm": 1e9}, None, 422, "cropMarginMm"),
    (None, {"maxSteps": 150.5}, None, 422, "maxSteps"),
    (None, {"spz": "yes"}, None, 422, "spz"),
    (None, {"colourMatch": 1}, None, 422, "colourMatch"),
    (None, None, {"cameras": [{"image": "a", "R": [1] * 9, "t": [0, 0, 1e300]}], "segments": []}, 422, "t"),
    (None, None, {"cameras": [{"image": "a", "R": [1] * 9, "t": [0, 0, 0]}],
                  "segments": [{"facets": [{"origin": [0, 0, 0]}]}]}, 422, "facets"),
    (None, None, {"cameras": []}, 422, "geometry"),
    (None, None, {"cameras": [{"image": "zz", "R": [1] * 9, "t": [0, 0, 0]}], "segments": []}, 422, "matches"),
    ([("a.jpg", jpeg())], None, None, 422, "at least 3"),
])
def test_validation(client, photos, options, geometry, code, text):
    r = client.post("/v1/jobs/splat", files=form(photos if photos is not None else three(), options, geometry))
    assert r.status_code == code, r.text
    assert text in r.json()["detail"]


def test_not_multipart_and_other_kinds(client):
    assert client.post("/v1/jobs/splat", json={}).status_code == 400
    assert client.post("/v1/jobs/solve", json={}).status_code == 501
    assert client.post("/v1/jobs/nope", json={}).status_code == 404


def test_limits(client, monkeypatch):
    monkeypatch.setattr(settings, "max_photos", 2)
    assert client.post("/v1/jobs/splat", files=form(three())).status_code == 413
    monkeypatch.setattr(settings, "max_photos", 400)
    monkeypatch.setattr(settings, "max_photo_bytes", 100)
    r = client.post("/v1/jobs/splat", files=form(three()))
    assert r.status_code == 413 and "exceeds" in r.json()["detail"]


def test_failed_upload_leaves_no_job_behind(client):
    before = set(os.listdir(settings.work_dir))
    client.post("/v1/jobs/splat", files=form([("a.jpg", jpeg()), ("b.jpg", b"broken")]))
    assert set(os.listdir(settings.work_dir)) == before


def test_tools_missing_is_503(client, monkeypatch):
    monkeypatch.setitem(main.TOOLS, "ok", False)
    assert client.post("/v1/jobs/splat", files=form(three())).status_code == 503


def test_auth(client, monkeypatch):
    monkeypatch.setattr(settings, "api_key", KEY)
    assert client.post("/v1/jobs/splat", files=form(three())).status_code == 401
    r = client.post("/v1/jobs/splat", files=form(three()), headers={"Authorization": f"Bearer {KEY}"})
    assert r.status_code == 202


@pytest.mark.parametrize("raw", ['{"maxSteps": NaN}', '{"maxSteps": Infinity}', '{"cropMarginMm": -Infinity}'])
def test_non_finite_options_are_422_not_500(client, raw):
    files = form(three()) + [("options", (None, raw))]
    r = client.post("/v1/jobs/splat", files=files)
    assert r.status_code in (400, 422), r.text


def test_decompression_bomb_is_refused(client, monkeypatch):
    """A header claiming more than MAX_IMAGE_MEGAPIXELS is refused before it is decoded (Pillow alone
    would only warn between its limit and 2x)."""
    monkeypatch.setattr(settings, "max_image_pixels", 1000)
    photos = [("a.jpg", jpeg(size=(40, 30))), ("b.jpg", jpeg()), ("c.jpg", jpeg())]  # 1200 px > 1000
    r = client.post("/v1/jobs/splat", files=form(photos))
    assert r.status_code == 422 and "too large" in r.json()["detail"]


def test_callback_to_metadata_address_is_refused(client):
    files = form(three()) + [("callbackUrl", (None, "http://169.254.169.254/latest/meta-data/"))]
    r = client.post("/v1/jobs/splat", files=files)
    assert r.status_code == 400 and "callbackUrl" in r.json()["detail"]
