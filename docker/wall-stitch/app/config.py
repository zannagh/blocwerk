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


def _float(name: str, default: float) -> float:
    raw = os.environ.get(name)
    if raw is None or raw.strip() == "":
        return default
    try:
        return float(raw)
    except ValueError as exc:
        raise RuntimeError(f"{name} must be a number, got {raw!r}") from exc


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
    # Defaulted so that adding a pipeline knob does not break every caller that builds a
    # Settings by hand; load_settings() always passes them explicitly.
    #
    # The three resolution knobs are the whole memory story of a job. work_mp is what
    # feature matching sees, compose_mp what each frame is resampled to when the flat
    # base is built, and max_canvas_mpx the hard ceiling on the base itself - the
    # pipeline lowers its own compose scale to stay under it rather than failing.
    # Which display projection the pipeline renders. NOT settled: "flat" applies no
    # reprojection and is the safe default while the choice is open; "cylindrical" is
    # the provisional curved view. See the pipeline's --natural.
    natural: str = "flat"
    curve: str = "gentle"
    work_mp: float = 0.7
    compose_mp: float = 2.5
    max_canvas_mpx: float = 40.0
    nfeat: int = 12000
    strip_budget_mb: int = 96
    pipeline_threads: int = 4

    @property
    def pipeline_script(self) -> str:
        return os.path.join(self.pipeline_dir, "wall_pipeline.py")


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
        # A real sweep of a bouldering wall is 40-50 frames, not a dozen: the pipeline
        # was validated on 46. The request cap is sized to match at ~30 MB a frame.
        max_photos=_int("WALLSTITCH_MAX_PHOTOS", 48),
        max_photo_bytes=_int("WALLSTITCH_MAX_PHOTO_BYTES", 64 * 1024 * 1024),
        max_request_bytes=_int("WALLSTITCH_MAX_REQUEST_BYTES", 1536 * 1024 * 1024),
        job_timeout_seconds=_int("WALLSTITCH_JOB_TIMEOUT_SECONDS", 1800),
        job_ttl_seconds=_int("WALLSTITCH_JOB_TTL_SECONDS", 86400),
        reaper_interval_seconds=_int("WALLSTITCH_REAPER_INTERVAL_SECONDS", 900),
        workers=max(1, _int("WALLSTITCH_WORKERS", 1)),
        queue_limit=_int("WALLSTITCH_QUEUE_LIMIT", 16),
        display_max_edge=_int("WALLSTITCH_DISPLAY_MAX_EDGE", 2000),
        display_jpeg_quality=_int("WALLSTITCH_DISPLAY_JPEG_QUALITY", 88),
        natural=os.environ.get("WALLSTITCH_NATURAL", "flat").strip() or "flat",
        curve=os.environ.get("WALLSTITCH_CURVE", "gentle").strip() or "gentle",
        work_mp=_float("WALLSTITCH_WORK_MP", 0.7),
        compose_mp=_float("WALLSTITCH_COMPOSE_MP", 2.5),
        max_canvas_mpx=_float("WALLSTITCH_MAX_CANVAS_MPX", 40.0),
        nfeat=_int("WALLSTITCH_NFEAT", 12000),
        strip_budget_mb=_int("WALLSTITCH_STRIP_BUDGET_MB", 96),
        pipeline_threads=max(0, _int("WALLSTITCH_PIPELINE_THREADS", 4)),
    )
