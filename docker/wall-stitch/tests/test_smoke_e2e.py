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


def test_the_real_pipeline_produces_both_projections(tmp_path, sample_copies):
    from fastapi.testclient import TestClient

    from app.config import Settings
    from app.main import create_app
    from app.runner import SubprocessPipelineRunner

    repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    settings = Settings(
        auth_token=TOKEN, data_dir=str(tmp_path / "jobs"),
        pipeline_dir=os.path.join(repo, "pipeline"), python_executable=sys.executable,
        onnx_model=os.environ.get("WALLSTITCH_ONNX_MODEL", ""),
        min_photos=2, max_photos=12, max_photo_bytes=128 * 1024 * 1024,
        max_request_bytes=1024 * 1024 * 1024, job_timeout_seconds=TIMEOUT_SECONDS,
        job_ttl_seconds=86400, reaper_interval_seconds=3600, workers=1, queue_limit=2,
        display_max_edge=2000, display_jpeg_quality=88)

    app = create_app(settings=settings, runner=SubprocessPipelineRunner(settings))
    options = {"wallAngleDegrees": 45.0, "defaultProjection": "angled",
               "transferHolds": False, "holds": []}

    with TestClient(app) as client:
        files = [("photos", (path.name, path.read_bytes(), "image/jpeg")) for path in sample_copies]
        files.append(("options", ("options", json.dumps(options), "application/json")))
        created = client.post("/jobs", files=files, headers=AUTH)
        assert created.status_code == 202
        job_id = created.json()["jobId"]

        state = _poll(client, job_id)
        assert state["status"] == "succeeded", state.get("error")
        result = state["result"]

        ortho, angled = result["ortho"], result["angled"]
        assert ortho["width"] > 2000 and ortho["height"] > 1000
        # The angled view is the ortho with only the vertical axis scaled by cos(45 deg).
        assert angled["width"] == ortho["width"]
        assert angled["height"] == pytest.approx(ortho["height"] * result["verticalScale"], rel=0.02)
        assert result["verticalScale"] == pytest.approx(0.7071, abs=1e-3)

        for name, content_type in (("ortho.png", "image/png"), ("angled.png", "image/png"),
                                   ("display-ortho.jpg", "image/jpeg"),
                                   ("display-angled.jpg", "image/jpeg")):
            response = client.get(f"/jobs/{job_id}/artifacts/{name}", headers=AUTH)
            assert response.status_code == 200, name
            assert response.headers["content-type"] == content_type
            assert len(response.content) > 10_000

        display = client.get(f"/jobs/{job_id}/artifacts/display-ortho.jpg", headers=AUTH).content
        assert len(display) < len(client.get(
            f"/jobs/{job_id}/artifacts/ortho.png", headers=AUTH).content)

        diagnostics = result["diagnostics"]
        assert diagnostics["imagesUsed"]
        assert diagnostics["seamAngleRmsDeg"] < 1.0
        assert diagnostics["bowMedianPx"] < 20.0

        assert client.delete(f"/jobs/{job_id}", headers=AUTH).status_code == 204


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
