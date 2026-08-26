"""Contract-shape tests for every endpoint. No pipeline, no OpenCV."""
from __future__ import annotations

import json
import time
import uuid

import pytest

from conftest import AUTH, TOKEN, photo_bytes, photo_part, wait_for

OPTIONS = {"curve": "gentle", "wallWidthM": 5.5, "wallHeightM": 2.5,
           "transferHolds": False, "holds": []}


def post_job(client, photos=2, options=None, old_photo=None, size=(64, 48)):
    files = [photo_part(f"{i}.jpeg", width=size[0], height=size[1]) for i in range(1, photos + 1)]
    files.append(("options", ("options", json.dumps(options or OPTIONS), "application/json")))
    if old_photo is not None:
        files.append(("oldPhoto", ("old.jpg", old_photo, "image/jpeg")))
    return client.post("/jobs", files=files, headers=AUTH)


# ---- auth -------------------------------------------------------------------

@pytest.mark.parametrize("headers", [
    {}, {"Authorization": "Bearer wrong-token-value"}, {"Authorization": TOKEN},
    {"Authorization": "Basic " + TOKEN},
])
def test_every_job_endpoint_rejects_bad_credentials(client, headers):
    assert client.get("/jobs/anything", headers=headers).status_code == 401
    assert client.delete("/jobs/anything", headers=headers).status_code == 401
    assert client.get("/jobs/x/artifacts/flat.jpg", headers=headers).status_code == 401
    assert client.post("/jobs", files=[photo_part(), photo_part("2.jpeg")],
                       headers=headers).status_code == 401


def test_healthz_needs_no_credentials(client):
    response = client.get("/healthz")
    assert response.status_code in (200, 503)
    body = response.json()
    assert set(body["checks"]) == {"pipelineImports", "dataDirWritable"}
    assert body["checks"]["dataDirWritable"] is True


# ---- validation -------------------------------------------------------------

def test_one_photo_is_rejected(client):
    response = post_job(client, photos=1)
    assert response.status_code == 400
    assert response.json()["detail"]["code"] == "too_few_photos"


def test_more_photos_than_the_cap_are_rejected(client):
    response = post_job(client, photos=client.settings.max_photos + 1)
    assert response.status_code == 400
    assert response.json()["detail"]["code"] == "too_many_photos"


def test_a_full_46_frame_sweep_is_accepted(client):
    # The pipeline was validated on a 46-frame sweep; the cap must not put that out of
    # reach of the app.
    assert post_job(client, photos=46).status_code == 202


def test_oversized_photo_is_rejected(client):
    response = post_job(client, photos=2, size=(900, 900))
    assert response.status_code == 400
    assert response.json()["detail"]["code"] in ("photo_too_large", "request_too_large")


def test_oversized_request_is_rejected(settings, fake_runner):
    """Each photo is under the per-photo cap; together they blow the total budget."""
    from dataclasses import replace
    from fastapi.testclient import TestClient
    from app.main import create_app

    tight = replace(settings, max_photo_bytes=8192, max_request_bytes=1500)
    with TestClient(create_app(settings=tight, runner=fake_runner)) as tight_client:
        response = post_job(tight_client, photos=6)
        assert response.status_code == 400
        assert response.json()["detail"]["code"] == "request_too_large"


def test_non_image_part_is_rejected(client):
    files = [("photos", ("notes.txt", b"hello", "text/plain")), photo_part("2.jpeg"),
             ("options", ("options", json.dumps(OPTIONS), "application/json"))]
    response = client.post("/jobs", files=files, headers=AUTH)
    assert response.status_code == 400
    assert response.json()["detail"]["code"] == "unsupported_photo_type"


def test_transfer_holds_without_old_photo_is_rejected(client):
    options = dict(OPTIONS, transferHolds=True, oldPhotoWidth=3333, oldPhotoHeight=2198)
    response = post_job(client, options=options)
    assert response.status_code == 400
    assert response.json()["detail"]["code"] == "old_photo_required"


def test_malformed_options_json_is_rejected(client):
    files = [photo_part(), photo_part("2.jpeg"),
             ("options", ("options", "{not json", "application/json"))]
    response = client.post("/jobs", files=files, headers=AUTH)
    assert response.status_code == 400
    assert response.json()["detail"]["code"] == "invalid_options"


def test_a_rejected_upload_leaves_no_job_behind(client):
    post_job(client, photos=1)
    assert client.store.list_ids() == []


# ---- lifecycle --------------------------------------------------------------

def test_post_returns_202_queued_then_the_job_succeeds(client):
    client.runner.gate.clear()
    created = post_job(client)
    assert created.status_code == 202
    body = created.json()
    assert body["status"] == "queued"
    job_id = body["jobId"]

    queued = client.get(f"/jobs/{job_id}", headers=AUTH).json()
    assert queued["status"] in ("queued", "running")
    assert queued["result"] is None and queued["error"] is None

    client.runner.gate.set()
    done = wait_for(client, job_id, "succeeded")
    assert done["progress"] == 1.0
    assert done["error"] is None
    result = done["result"]
    assert result["flatMaster"] == {"artifact": "flat.jpg", "width": 400, "height": 260}
    assert result["naturalMaster"]["artifact"] == "natural.jpg"
    assert result["displayFlat"] == "display-flat.jpg"
    assert result["displayNatural"] == "display-natural.jpg"
    assert result["camerasJson"] == "cameras.json"
    assert result["wallWidthM"] == pytest.approx(5.5)
    assert result["curvature"]["default"] == "gentle"
    assert result["curvature"]["curves"][0]["artifact"] == "natural-gentle.jpg"
    assert result["diagnostics"]["elapsedSeconds"] == pytest.approx(187.5)
    # No prior holds were sent, so there is nothing to carry over.
    assert result["carryover"] is None


def test_progress_and_stage_are_reported_while_running(client):
    client.runner.gate.clear()
    job_id = post_job(client).json()["jobId"]
    deadline = time.time() + 5
    while time.time() < deadline:
        if client.get(f"/jobs/{job_id}", headers=AUTH).json()["status"] == "running":
            break
        time.sleep(0.01)
    running = client.get(f"/jobs/{job_id}", headers=AUTH).json()
    assert running["status"] == "running"
    assert running["stage"] == "preparing"
    assert 0.0 < running["progress"] < 1.0
    client.runner.gate.set()
    wait_for(client, job_id, "succeeded")


def test_carryover_is_returned_when_transfer_was_requested(client):
    hold_id = str(uuid.uuid4())
    options = dict(OPTIONS, transferHolds=True, oldPhotoWidth=3333, oldPhotoHeight=2198,
                   holds=[{"id": hold_id, "x": 0.51, "y": 0.33, "radius": 0.012,
                           "shapePoints": [{"dx": 0.01, "dy": -0.02}], "color": "pink",
                           "category": 0, "boulderLinkCount": 3}])
    job_id = post_job(client, options=options, old_photo=photo_bytes()).json()["jobId"]
    result = wait_for(client, job_id, "succeeded")["result"]
    carryover = result["carryover"]
    assert [h["id"] for h in carryover["carried"]] == [hold_id]
    assert carryover["carried"][0]["classification"] == "CARRIED_OVER"
    assert carryover["carried"][0]["boulderLinkCount"] == 3
    assert carryover["counts"]["CARRIED_OVER"] == 1
    # The pipeline's standing caveat must survive the whole round trip: the app decides
    # whether to apply the match, and it cannot do that if the warning is dropped here.
    assert carryover["blocker"]


@pytest.mark.parametrize("code", ["insufficient_overlap", "no_dominant_plane",
                                  "too_few_usable_images", "timeout"])
def test_a_failing_pipeline_produces_an_actionable_error(client, code):
    client.runner.fail(code)
    job_id = post_job(client).json()["jobId"]
    failed = wait_for(client, job_id, "failed")
    assert failed["error"]["code"] == code
    assert failed["result"] is None
    message = failed["error"]["message"]
    assert len(message) > 20 and "Traceback" not in message and "/" not in message.split(" ")[0]


def test_an_unexpected_crash_never_leaks_paths_or_tracebacks(client):
    client.runner.explode()
    job_id = post_job(client).json()["jobId"]
    failed = wait_for(client, job_id, "failed")
    assert failed["error"]["code"] == "pipeline_failed"
    assert "/secret/path" not in failed["error"]["message"]
    assert "Traceback" not in failed["error"]["message"]


# ---- artifacts and deletion --------------------------------------------------

@pytest.mark.parametrize("name,content_type", [
    ("flat.jpg", "image/jpeg"), ("natural.jpg", "image/jpeg"),
    ("natural-gentle.jpg", "image/jpeg"),
    ("display-flat.jpg", "image/jpeg"), ("display-natural.jpg", "image/jpeg"),
])
def test_artifacts_are_served_with_the_right_content_type(client, name, content_type):
    job_id = post_job(client).json()["jobId"]
    wait_for(client, job_id, "succeeded")
    response = client.get(f"/jobs/{job_id}/artifacts/{name}", headers=AUTH)
    assert response.status_code == 200
    assert response.headers["content-type"] == content_type
    assert len(response.content) > 0


def test_unknown_and_traversing_artifact_names_are_404(client):
    job_id = post_job(client).json()["jobId"]
    wait_for(client, job_id, "succeeded")
    for name in ("nope.png", "..%2F..%2Fstate.json", "state.json"):
        assert client.get(f"/jobs/{job_id}/artifacts/{name}", headers=AUTH).status_code == 404


def test_unknown_job_is_404_everywhere(client):
    assert client.get("/jobs/does-not-exist", headers=AUTH).status_code == 404
    assert client.get("/jobs/does-not-exist/artifacts/flat.jpg", headers=AUTH).status_code == 404
    assert client.delete("/jobs/does-not-exist", headers=AUTH).status_code == 204


def test_delete_returns_204_and_removes_the_job_directory(client):
    job_id = post_job(client).json()["jobId"]
    wait_for(client, job_id, "succeeded")
    assert client.delete(f"/jobs/{job_id}", headers=AUTH).status_code == 204
    assert client.get(f"/jobs/{job_id}", headers=AUTH).status_code == 404
    assert job_id not in client.store.list_ids()


def test_queue_full_is_refused_with_503(client):
    client.runner.gate.clear()
    accepted = 0
    for _ in range(client.settings.queue_limit + 4):
        if post_job(client).status_code == 202:
            accepted += 1
        else:
            break
    response = post_job(client)
    assert response.status_code == 503
    assert response.json()["detail"]["code"] == "queue_full"
    client.runner.gate.set()
