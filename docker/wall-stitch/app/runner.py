"""Executing the vendored pipeline for one job.

Everything here treats the pipeline as a black-box CLI: build an argv, stream its
stdout to derive progress, then read the files it left in the work directory.
"""
from __future__ import annotations

import json
import math
import os
import shutil
import subprocess
import threading
from typing import Callable, Dict, List, Optional, Protocol

from . import holdsio, invocation
from .config import Settings
from .errors import JobFailure, classify
from .imaging import dimensions, write_display_copy
from .stages import HoldsProgressTracker, ProgressTracker, STITCH_MARKERS, split_phases

ProgressCallback = Callable[[float, str], None]

ORTHO_MASTER = os.path.join("06-final", "wall-orthophoto.png")
ANGLED_MASTER = os.path.join("06-final", "wall-orthophoto-angled.png")
STITCH_REPORT = os.path.join("06-final", "report.json")
REMAP_RESULT = "holds-remapped.json"


class PipelineRunner(Protocol):
    """Seam the tests replace: everything above it is pure HTTP and bookkeeping."""

    def run(self, job: "JobContext", on_progress: ProgressCallback) -> Dict[str, object]:
        ...


class JobContext:
    """Everything one job needs: its directories, its options, its inputs."""

    def __init__(self, job_id: str, job_dir: str, options, photos: List[str],
                 old_photo: Optional[str]):
        self.job_id = job_id
        self.job_dir = job_dir
        self.options = options
        self.photos = photos
        self.old_photo = old_photo
        self.cancelled = threading.Event()

    @property
    def input_dir(self) -> str:
        return os.path.join(self.job_dir, "input")

    @property
    def work_dir(self) -> str:
        return os.path.join(self.job_dir, "work")

    @property
    def artifact_dir(self) -> str:
        return os.path.join(self.job_dir, "artifacts")

    @property
    def log_path(self) -> str:
        return os.path.join(self.job_dir, "pipeline.log")


class SubprocessPipelineRunner:
    """Runs `stitch_wall.py`, then optionally `remap_holds.py`, as child processes."""

    def __init__(self, settings: Settings):
        self.settings = settings

    def run(self, job: JobContext, on_progress: ProgressCallback) -> Dict[str, object]:
        stitch_span, holds_span = split_phases(job.options.transfer_holds)
        os.makedirs(job.work_dir, exist_ok=True)
        cache_dir = os.path.join(job.work_dir, ".cache")

        argv = invocation.stitch_command(
            self.settings.python_executable, self.settings.stitch_dir, "stitch_wall.py",
            job.input_dir, job.work_dir, cache_dir,
            job.options.wall_angle_degrees, [os.path.basename(p) for p in job.photos])
        tracker = ProgressTracker(STITCH_MARKERS, stitch_span[0], stitch_span[1], "registering")
        self._execute(job, argv, self.settings.stitch_dir, {}, tracker, on_progress)

        ortho = os.path.join(job.work_dir, ORTHO_MASTER)
        angled = os.path.join(job.work_dir, ANGLED_MASTER)
        if not os.path.exists(ortho) or not os.path.exists(angled):
            raise JobFailure(classify(self._tail(job), "pipeline_failed"),
                             "pipeline produced no orthophoto")

        holds = None
        if job.options.transfer_holds and holds_span is not None:
            holds = self._transfer_holds(job, ortho, holds_span, on_progress)

        return self._assemble(job, ortho, angled, holds, on_progress)

    # ---- phases ----------------------------------------------------------------

    def _transfer_holds(self, job: JobContext, ortho: str, span, on_progress) -> List[Dict]:
        match_dir = os.path.join(job.work_dir, "holds-match")
        os.makedirs(match_dir, exist_ok=True)
        paths = holdsio.write_inputs(os.path.join(job.work_dir, "holds"),
                                     job.options.holds, job.options.wall_angle_degrees)
        if not job.old_photo:
            raise JobFailure("hold_transfer_failed", "no old photo supplied")

        argv = invocation.holds_command(
            self.settings.python_executable, self.settings.holds_match_dir, "remap_holds.py",
            match_dir, job.old_photo, ortho, paths["holds"], paths["wall"])
        env = invocation.holds_environment(
            job.work_dir, job.old_photo, ortho, paths["holds"], paths["wall"],
            self.settings.onnx_model)
        tracker = HoldsProgressTracker(span[0], span[1])
        try:
            self._execute(job, argv, self.settings.holds_match_dir, env, tracker, on_progress)
            return holdsio.read_results(os.path.join(match_dir, REMAP_RESULT))
        except JobFailure:
            raise
        except Exception as exc:  # noqa: BLE001 - any matcher problem is one failure to the user
            raise JobFailure("hold_transfer_failed", type(exc).__name__) from exc

    def _assemble(self, job: JobContext, ortho: str, angled: str,
                  holds: Optional[List[Dict]], on_progress) -> Dict[str, object]:
        on_progress(0.98, "packaging")
        os.makedirs(job.artifact_dir, exist_ok=True)
        shutil.copyfile(ortho, os.path.join(job.artifact_dir, "ortho.png"))
        shutil.copyfile(angled, os.path.join(job.artifact_dir, "angled.png"))

        for master, name in ((ortho, "display-ortho.jpg"), (angled, "display-angled.jpg")):
            write_display_copy(master, os.path.join(job.artifact_dir, name),
                               self.settings.display_max_edge, self.settings.display_jpeg_quality)

        ortho_w, ortho_h = dimensions(ortho)
        angled_w, angled_h = dimensions(angled)
        angle = float(job.options.wall_angle_degrees)
        return {
            "ortho": {"artifact": "ortho.png", "width": ortho_w, "height": ortho_h},
            "angled": {"artifact": "angled.png", "width": angled_w, "height": angled_h},
            "displayOrtho": "display-ortho.jpg",
            "displayAngled": "display-angled.jpg",
            "wallAngleDegrees": angle,
            "verticalScale": round(math.cos(math.radians(angle)), 6),
            "diagnostics": self._diagnostics(job),
            "holds": holds,
        }

    # ---- plumbing --------------------------------------------------------------

    def _execute(self, job: JobContext, argv, cwd: str, extra_env: Dict[str, str],
                 tracker, on_progress: ProgressCallback) -> None:
        env = dict(os.environ)
        env.update(extra_env)
        env.setdefault("PYTHONUNBUFFERED", "1")
        env.setdefault("OPENCV_IO_MAX_IMAGE_PIXELS", str(2 ** 40))

        with open(job.log_path, "a", encoding="utf-8") as log:
            log.write("\n$ " + " ".join(str(a) for a in argv) + "\n")
            log.flush()
            try:
                proc = subprocess.Popen(  # noqa: S603 - fixed argv, no shell
                    argv, cwd=cwd, env=env, stdout=subprocess.PIPE,
                    stderr=subprocess.STDOUT, text=True, bufsize=1)
            except OSError as exc:
                raise JobFailure("pipeline_failed", str(type(exc).__name__)) from exc

            timer = threading.Timer(self.settings.job_timeout_seconds, _terminate, [proc])
            timer.start()
            try:
                for line in proc.stdout:  # type: ignore[union-attr]
                    log.write(line)
                    if tracker.feed(line.rstrip("\n")):
                        on_progress(tracker.progress, tracker.stage)
                    if job.cancelled.is_set():
                        _terminate(proc)
                code = proc.wait()
            finally:
                fired = not timer.is_alive()
                timer.cancel()
                log.flush()

        if job.cancelled.is_set():
            raise JobFailure("cancelled")
        if code != 0:
            raise JobFailure("timeout" if fired else classify(self._tail(job)), f"exit {code}")

    def _diagnostics(self, job: JobContext) -> Dict[str, object]:
        used = [os.path.basename(p) for p in job.photos]
        report = self._report(job)
        check = report.get("rectification_check", {}) if isinstance(report, dict) else {}
        coverage = report.get("coverage", {}) if isinstance(report, dict) else {}

        warnings = []
        below = coverage.get("frac_below_30pct_of_best")
        if isinstance(below, (int, float)) and below > 0.35:
            warnings.append(
                f"{below * 100:.0f}% of the wall is resolved at under a third of the best "
                "sampled area; shoot the sparse parts from closer up.")
        crop = report.get("crop", {}) if isinstance(report, dict) else {}
        if isinstance(crop.get("filled_fraction"), (int, float)) and crop["filled_fraction"] < 0.97:
            warnings.append("The stitched area has gaps; the photos do not cover the whole wall.")

        best = coverage.get("best_source_share") or {}
        rejected = [{"name": n, "reason": "no usable overlap with the other photos"}
                    for n in used if best and os.path.splitext(n)[0] not in best]
        return {
            "imagesUsed": used,
            "imagesRejected": rejected,
            "seamAngleRmsDeg": float(check.get("angle_rms_deg") or 0.0),
            "bowMedianPx": float(check.get("bow_sagitta_median_px") or 0.0),
            "coverageWarnings": warnings,
        }

    def _report(self, job: JobContext) -> Dict[str, object]:
        try:
            with open(os.path.join(job.work_dir, STITCH_REPORT), "r", encoding="utf-8") as handle:
                return json.load(handle)
        except (OSError, json.JSONDecodeError):
            return {}

    @staticmethod
    def _tail(job: JobContext) -> str:
        try:
            with open(job.log_path, "r", encoding="utf-8", errors="replace") as handle:
                return handle.read()[-20000:]
        except OSError:
            return ""


def _terminate(proc: subprocess.Popen) -> None:
    if proc.poll() is not None:
        return
    proc.terminate()
    try:
        proc.wait(timeout=20)
    except subprocess.TimeoutExpired:
        proc.kill()
