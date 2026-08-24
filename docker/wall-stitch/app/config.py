"""Runtime configuration, entirely from the environment."""
from __future__ import annotations

import os
import sys
from dataclasses import dataclass


def _int(name: str, default: int) -> int:
    raw = os.environ.get(name)
    if raw is None or raw.strip() == "":
        return default
    try:
        return int(raw)
    except ValueError as exc:
        raise RuntimeError(f"{name} must be an integer, got {raw!r}") from exc


@dataclass(frozen=True)
class Settings:
    auth_token: str
    data_dir: str
    pipeline_dir: str
    python_executable: str
    onnx_model: str
    min_photos: int
    max_photos: int
    max_photo_bytes: int
    max_request_bytes: int
    job_timeout_seconds: int
    job_ttl_seconds: int
    reaper_interval_seconds: int
    workers: int
    queue_limit: int
    display_max_edge: int
    display_jpeg_quality: int

    @property
    def stitch_dir(self) -> str:
        return os.path.join(self.pipeline_dir, "stitch")

    @property
    def holds_match_dir(self) -> str:
        return os.path.join(self.pipeline_dir, "holds_match")


def load_settings() -> Settings:
    """Reads settings from the environment; refuses to build without an auth token."""
    token = os.environ.get("WALLSTITCH_AUTH_TOKEN", "").strip()
    if not token:
        raise RuntimeError(
            "WALLSTITCH_AUTH_TOKEN is not set. The sidecar refuses to start unauthenticated.")
    if len(token) < 16:
        raise RuntimeError("WALLSTITCH_AUTH_TOKEN must be at least 16 characters.")

    return Settings(
        auth_token=token,
        data_dir=os.environ.get("WALLSTITCH_DATA_DIR", "/data/jobs"),
        pipeline_dir=os.environ.get("WALLSTITCH_PIPELINE_DIR", "/opt/pipeline"),
        python_executable=os.environ.get("WALLSTITCH_PYTHON", sys.executable),
        onnx_model=os.environ.get("WALLSTITCH_ONNX_MODEL", "/opt/models/climbingcrux.onnx"),
        min_photos=_int("WALLSTITCH_MIN_PHOTOS", 2),
        max_photos=_int("WALLSTITCH_MAX_PHOTOS", 12),
        max_photo_bytes=_int("WALLSTITCH_MAX_PHOTO_BYTES", 64 * 1024 * 1024),
        max_request_bytes=_int("WALLSTITCH_MAX_REQUEST_BYTES", 768 * 1024 * 1024),
        job_timeout_seconds=_int("WALLSTITCH_JOB_TIMEOUT_SECONDS", 1800),
        job_ttl_seconds=_int("WALLSTITCH_JOB_TTL_SECONDS", 86400),
        reaper_interval_seconds=_int("WALLSTITCH_REAPER_INTERVAL_SECONDS", 900),
        workers=max(1, _int("WALLSTITCH_WORKERS", 1)),
        queue_limit=_int("WALLSTITCH_QUEUE_LIMIT", 16),
        display_max_edge=_int("WALLSTITCH_DISPLAY_MAX_EDGE", 2000),
        display_jpeg_quality=_int("WALLSTITCH_DISPLAY_JPEG_QUALITY", 88),
    )
