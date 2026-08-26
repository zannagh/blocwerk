"""One end-to-end run of the real pipeline over the sample photos.

Skipped unless the heavy dependencies import AND a directory of sample wall photos is
pointed at by WALLSTITCH_SAMPLE_DIR (default ~/Desktop/wall-photos). The samples are
COPIED into a temp directory; the source directory is never written to.

    WALLSTITCH_SAMPLE_DIR=~/Desktop/wall-photos pytest tests/test_smoke_e2e.py -m e2e
"""
from __future__ import annotations

import json
import os
import shutil
import sys
import time

import pytest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from conftest import AUTH, TOKEN  # noqa: E402

SAMPLE_DIR = os.path.expanduser(os.environ.get("WALLSTITCH_SAMPLE_DIR", "~/Desktop/wall-photos"))
SAMPLES = [f"{n}.jpeg" for n in range(1, 6)]
TIMEOUT_SECONDS = int(os.environ.get("WALLSTITCH_SMOKE_TIMEOUT", "3600"))


def _deps_available() -> bool:
    try:
        import cv2  # noqa: F401
        import numpy  # noqa: F401
        import scipy  # noqa: F401
        return True
    except Exception:  # noqa: BLE001
        return False


def _samples_available() -> bool:
    """Readable, not merely present: a sandboxed runner can stat files it cannot open."""
    for name in SAMPLES:
        try:
            with open(os.path.join(SAMPLE_DIR, name), "rb") as handle:
                handle.read(1)
        except OSError:
            return False
    return True


pytestmark = [
    pytest.mark.e2e,
    pytest.mark.skipif(not _deps_available(), reason="pipeline dependencies are not installed"),
    pytest.mark.skipif(not _samples_available(), reason=f"no sample photos in {SAMPLE_DIR}"),
]


@pytest.fixture()
def sample_copies(tmp_path):
    """Copies (never moves, never edits) the sample photos into a temp directory."""
    staging = tmp_path / "samples"
    staging.mkdir()
    for name in SAMPLES:
        shutil.copyfile(os.path.join(SAMPLE_DIR, name), staging / name)
    return [staging / name for name in SAMPLES]


def _run_sample_job(tmp_path, sample_copies):
    """Runs the real pipeline once over the samples and returns (client, job_id, result)."""
    from fastapi.testclient import TestClient

    from app.config import Settings
    from app.main import create_app
    from app.runner import SubprocessPipelineRunner

    repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    settings = Settings(
        auth_token=TOKEN, data_dir=str(tmp_path / "jobs"),
        pipeline_dir=os.path.join(repo, "pipeline"), python_executable=sys.executable,
        onnx_model=os.environ.get("WALLSTITCH_ONNX_MODEL", ""),
        min_photos=2, max_photos=48, max_photo_bytes=128 * 1024 * 1024,
        max_request_bytes=1024 * 1024 * 1024, job_timeout_seconds=TIMEOUT_SECONDS,
        job_ttl_seconds=86400, reaper_interval_seconds=3600, workers=1, queue_limit=2,
        display_max_edge=2000, display_jpeg_quality=88)

    app = create_app(settings=settings, runner=SubprocessPipelineRunner(settings))
    options = {"curve": "gentle", "wallWidthM": 5.5, "wallHeightM": 2.5,
               "transferHolds": False, "holds": []}

    client = TestClient(app)
    client.__enter__()
    files = [("photos", (path.name, path.read_bytes(), "image/jpeg")) for path in sample_copies]
    files.append(("options", ("options", json.dumps(options), "application/json")))
    created = client.post("/jobs", files=files, headers=AUTH)
    assert created.status_code == 202
    job_id = created.json()["jobId"]

    state = _poll(client, job_id)
    assert state["status"] == "succeeded", state.get("error")
    return client, job_id, state["result"]


def _assert_common_artifacts(client, job_id, result):
    """The masters, the display shrink, the camera solve, and the diagnostics."""
    for name, content_type in (("flat.jpg", "image/jpeg"), ("natural.jpg", "image/jpeg"),
                               ("display-flat.jpg", "image/jpeg"),
                               ("display-natural.jpg", "image/jpeg")):
        response = client.get(f"/jobs/{job_id}/artifacts/{name}", headers=AUTH)
        assert response.status_code == 200, name
        assert response.headers["content-type"] == content_type
        assert len(response.content) > 10_000

    display = client.get(f"/jobs/{job_id}/artifacts/display-flat.jpg", headers=AUTH).content
    assert len(display) < len(client.get(
        f"/jobs/{job_id}/artifacts/flat.jpg", headers=AUTH).content)

    cameras = client.get(f"/jobs/{job_id}/artifacts/cameras.json", headers=AUTH)
    assert cameras.status_code == 200
    solved = json.loads(cameras.content)
    assert solved["schema"] == "blocwerk.wall-cameras/1"
    assert len(solved["frames"]) >= 2
    assert len(solved["frames"][0]["matrix"]) == 3

    diagnostics = result["diagnostics"]
    assert diagnostics["imagesUsed"]
    assert diagnostics["referenceFrame"]
    assert diagnostics["elapsedSeconds"] > 0
    assert client.delete(f"/jobs/{job_id}", headers=AUTH).status_code == 204


def test_a_real_run_produces_a_flat_master_and_a_narrower_natural(tmp_path, sample_copies):
    client, job_id, result = _run_sample_job(tmp_path, sample_copies)
    try:
        flat, natural = result["flatMaster"], result["naturalMaster"]
        assert flat["width"] > 1000 and flat["height"] > 500
        # The cylinder normalises on the CENTRE derivative, so the ends foreshorten and
        # the natural is always NARROWER than the flat base it was curved from. A
        # natural at least as wide means the old edges-stay-at-the-edges normalisation
        # (the anamorphic widening) has come back.
        assert natural["width"] < flat["width"]
        assert natural["height"] == flat["height"]

        curvature = result["curvature"]
        assert curvature["default"] == "gentle"
        assert {c["name"] for c in curvature["curves"]} == {"gentle", "medium", "strong"}
        # Stronger curvature means a tighter arc and a narrower picture.
        by_name = {c["name"]: c for c in curvature["curves"]}
        assert by_name["gentle"]["width"] > by_name["strong"]["width"]
        assert by_name["gentle"]["radiusM"] > by_name["strong"]["radiusM"]

        _assert_common_artifacts(client, job_id, result)
    finally:
        client.__exit__(None, None, None)


def _poll(client, job_id: str) -> dict:
    deadline = time.time() + TIMEOUT_SECONDS
    last = None
    while time.time() < deadline:
        last = client.get(f"/jobs/{job_id}", headers=AUTH).json()
        if last["status"] in ("succeeded", "failed"):
            return last
        assert 0.0 <= last["progress"] <= 1.0
        time.sleep(2.0)
    raise AssertionError(f"pipeline did not finish within {TIMEOUT_SECONDS}s; last state {last}")
