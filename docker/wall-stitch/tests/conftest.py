"""Fixtures: a real app wired to a fake pipeline runner, so nothing heavy runs."""
from __future__ import annotations

import io
import os
import sys
import threading
from typing import Callable, Dict, List, Optional

import pytest
from fastapi.testclient import TestClient
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from app.config import Settings  # noqa: E402
from app.errors import JobFailure  # noqa: E402
from app.main import create_app  # noqa: E402
from app.runner import JobContext  # noqa: E402

TOKEN = "test-token-0123456789"
AUTH = {"Authorization": f"Bearer {TOKEN}"}


def photo_bytes(width: int = 64, height: int = 48, fmt: str = "JPEG") -> bytes:
    buffer = io.BytesIO()
    Image.new("RGB", (width, height), (90, 120, 160)).save(buffer, fmt)
    return buffer.getvalue()


def photo_part(name: str = "1.jpeg", **kwargs):
    return ("photos", (name, photo_bytes(**kwargs), "image/jpeg"))


class FakeRunner:
    """Stands in for the subprocess pipeline: writes plausible artifacts, no OpenCV."""

    def __init__(self):
        self.behaviour: Callable[[JobContext, Callable], Dict] = self.succeed
        self.gate = threading.Event()
        self.gate.set()
        self.calls: List[JobContext] = []
        self.progress: List[tuple] = []

    def run(self, job: JobContext, on_progress) -> Dict[str, object]:
        self.calls.append(job)
        self.gate.wait(timeout=10)
        recorder = lambda p, s: (self.progress.append((p, s)), on_progress(p, s))[1]  # noqa: E731
        return self.behaviour(job, recorder)

    # -- behaviours ---------------------------------------------------------
    def succeed(self, job: JobContext, on_progress) -> Dict[str, object]:
        on_progress(0.30, "matching")
        on_progress(0.80, "blending")
        os.makedirs(job.artifact_dir, exist_ok=True)
        for name, size in (("flat.jpg", (400, 260)), ("natural.jpg", (360, 260)),
                           ("natural-gentle.jpg", (360, 260)),
                           ("display-flat.jpg", (200, 130)),
                           ("display-natural.jpg", (180, 130)),
                           ("display-natural-gentle.jpg", (180, 130))):
            Image.new("RGB", size, (30, 60, 90)).save(os.path.join(job.artifact_dir, name))
        with open(os.path.join(job.artifact_dir, "cameras.json"), "w") as handle:
            handle.write('{"schema": "blocwerk.wall-cameras/1", "frames": []}')
        return {
            "flatMaster": {"artifact": "flat.jpg", "width": 400, "height": 260},
            "naturalMaster": {"artifact": "natural.jpg", "width": 360, "height": 260},
            "displayFlat": "display-flat.jpg",
            "displayNatural": "display-natural.jpg",
            "camerasJson": "cameras.json",
            "wallWidthM": job.options.wall_width_m,
            "wallHeightM": job.options.wall_height_m,
            "curvature": {
                "default": job.options.curve, "requestedThetaMaxDeg": 30.0,
                "viewDistM": 3.0, "eyeFrac": 0.34,
                "curves": [{"name": "gentle", "artifact": "natural-gentle.jpg",
                            "display": "display-natural-gentle.jpg", "width": 360,
                            "height": 260, "thetaMaxDeg": 30.0, "k": 0.636,
                            "radiusM": 5.25}],
            },
            "diagnostics": {"imagesUsed": [os.path.basename(p) for p in job.photos],
                            "imagesRejected": [], "referenceFrame": "2.jpeg",
                            "straightness": 0.0, "inpaintedPx": 12,
                            "elapsedSeconds": 187.5, "coverageWarnings": []},
            "holds": [{"id": "0", "x": 0.5, "y": 0.5, "radius": 0.02,
                       "confidence": 0.9}],
            "carryover": {
                "generation": 1,
                "counts": {"CARRIED_OVER": len(job.options.holds), "MISSING": 0,
                           "NEW": 0},
                "countsBoulderLinked": {},
                "estimatedPrecision": 0.86,
                "blocker": "DO NOT APPLY UNATTENDED.",
                "carried": [{"id": h.id, "x": h.x, "y": h.y, "radius": h.radius,
                             "shapePoints": None, "classification": "CARRIED_OVER",
                             "boulderLinkCount": h.boulder_link_count}
                            for h in job.options.holds],
                "missing": [], "new": [],
            } if job.options.holds else None,
        }

    def fail(self, code: str):
        def behaviour(job, on_progress):
            raise JobFailure(code, "detail that must never reach the client")
        self.behaviour = behaviour

    def explode(self):
        def behaviour(job, on_progress):
            raise RuntimeError("/secret/path/to/pipeline.py line 42: boom")
        self.behaviour = behaviour


@pytest.fixture()
def settings(tmp_path) -> Settings:
    return Settings(
        auth_token=TOKEN, data_dir=str(tmp_path / "jobs"), pipeline_dir=str(tmp_path / "pipeline"),
        python_executable=sys.executable, onnx_model=str(tmp_path / "model.onnx"),
        min_photos=2, max_photos=48, max_photo_bytes=8192, max_request_bytes=65536,
        job_timeout_seconds=60, job_ttl_seconds=86400, reaper_interval_seconds=3600,
        workers=1, queue_limit=4, display_max_edge=2000, display_jpeg_quality=88)


@pytest.fixture()
def fake_runner() -> FakeRunner:
    return FakeRunner()


@pytest.fixture()
def client(settings, fake_runner):
    app = create_app(settings=settings, runner=fake_runner)
    with TestClient(app) as test_client:
        test_client.runner = fake_runner
        test_client.settings = settings
        test_client.store = app.state.store
        yield test_client


def wait_for(client, job_id: str, status: str, timeout: float = 10.0) -> dict:
    import time
    deadline = time.time() + timeout
    body: Optional[dict] = None
    while time.time() < deadline:
        body = client.get(f"/jobs/{job_id}", headers=AUTH).json()
        if body["status"] == status:
            return body
        time.sleep(0.02)
    raise AssertionError(f"job never reached {status!r}; last state was {body}")
