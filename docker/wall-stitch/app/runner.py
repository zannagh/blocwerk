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
            job.options.wall_angle_degrees, [os.path.basename(p) for p in job.photos],
            max_canvas_mpx=self.settings.max_canvas_mpx,
            png_compression=self.settings.png_compression,
            emit_facets=self.settings.emit_facets)
        tracker = ProgressTracker(STITCH_MARKERS, stitch_span[0], stitch_span[1], "registering")
        self._execute(job, argv, self.settings.stitch_dir, {}, tracker, on_progress)

        ortho = os.path.join(job.work_dir, ORTHO_MASTER)
        angled = os.path.join(job.work_dir, ANGLED_MASTER)
        if not os.path.exists(ortho) or not os.path.exists(angled):
            raise JobFailure(classify(self._tail(job), "pipeline_failed"),
                             "pipeline produced no orthophoto")

        holds = None
        if job.options.transfer_holds and holds_span is not None:
            holds = self._transfer_holds(job, angled, holds_span, on_progress)

        return self._assemble(job, ortho, angled, holds, on_progress)

    # ---- phases ----------------------------------------------------------------

    def _transfer_holds(self, job: JobContext, new_master: str, span, on_progress) -> List[Dict]:
        # Recognition retargeted to the NATURAL master (the `angled` slot): the old photo is
        # registered onto it and holds are emitted in ONE whole-wall coordinate space
        # normalised against that single image (matches the DB: whole-wall, SegmentId NULL).
        # The natural master shows the whole wall in one perspective, so far fewer holds fall
        # off-frame than on the facet-composited ortho. (Full whole-wall benefit needs the
        # stitch run with --emit-facets so the angled slot is the natural photographic stitch
        # rather than a vertically-squashed ortho.)
        match_dir = os.path.join(job.work_dir, "holds-match")
        os.makedirs(match_dir, exist_ok=True)
        paths = holdsio.write_inputs(os.path.join(job.work_dir, "holds"),
                                     job.options.holds, job.options.wall_angle_degrees)
        if not job.old_photo:
            raise JobFailure("hold_transfer_failed", "no old photo supplied")

        argv = invocation.holds_command(
            self.settings.python_executable, self.settings.holds_match_dir, "remap_holds.py",
            match_dir, job.old_photo, new_master, paths["holds"], paths["wall"])
        env = invocation.holds_environment(
            job.work_dir, job.old_photo, new_master, paths["holds"], paths["wall"],
            self.settings.onnx_model)
        tracker = HoldsProgressTracker(span[0], span[1])
        try:
            self._execute(job, argv, self.settings.holds_match_dir, env, tracker, on_progress)
            # Hold-aware crop of the cylindrical natural master: now that the live holds
            # sit on the (uncropped) natural master, crop_natural trims it to the wall,
            # overwrites the angled-slot PNG/JPG in place, and re-normalises the holds in
            # holds-remapped.json to the crop. Only meaningful under --emit-facets, whose
            # angled slot IS the cylindrical natural master; skipped on the legacy path
            # (there the angled slot is a squashed ortho, not a photographic stitch).
            if self.settings.emit_facets:
                self._crop_natural(job, env, on_progress)
            return holdsio.read_results(os.path.join(match_dir, REMAP_RESULT))
        except JobFailure:
            raise
        except Exception as exc:  # noqa: BLE001 - any matcher problem is one failure to the user
            raise JobFailure("hold_transfer_failed", type(exc).__name__) from exc

    def _crop_natural(self, job: JobContext, env: Dict[str, str], on_progress) -> None:
        # Degrade gracefully: a crop failure (e.g. no placed holds to crop against) must
        # NOT sink the job. The uncropped cylindrical natural master and the whole-wall
        # holds already normalised against it stay valid - the wall just keeps the extra
        # attic margin the crop would have trimmed.
        on_progress(0.965, "cropping")
        argv = invocation.crop_command(
            self.settings.python_executable, self.settings.holds_match_dir, "crop_natural.py",
            png_compression=self.settings.png_compression)
        tracker = HoldsProgressTracker(0.965, 0.97)
        try:
            self._execute(job, argv, self.settings.holds_match_dir, env, tracker, on_progress)
        except Exception as exc:  # noqa: BLE001 - keep the uncropped master rather than failing
            with open(job.log_path, "a", encoding="utf-8") as handle:
                handle.write(f"\ncrop_natural skipped ({type(exc).__name__}); "
                             "keeping the uncropped natural master\n")

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
        env.setdefault("WALLSTITCH_STRIP_BUDGET_MB", str(self.settings.strip_budget_mb))
        # Left alone, OpenCV and the BLAS underneath NumPy each open a thread pool the
        # size of the host's core count, and every one of those threads keeps its own
        # scratch arena. On a small box that multiplies the pipeline's transient
        # footprint and starves the app container of CPU for no throughput gain: the
        # heavy stages are memory-bandwidth bound well before they are core bound.
        if self.settings.pipeline_threads:
            for var in ("OMP_NUM_THREADS", "OPENBLAS_NUM_THREADS", "MKL_NUM_THREADS",
                        "NUMEXPR_NUM_THREADS", "OPENCV_FOR_THREADS_NUM"):
                env.setdefault(var, str(self.settings.pipeline_threads))

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
            if fired:
                raise JobFailure("timeout", f"exit {code}")
            # SIGKILL with nothing on stdout is what a cgroup OOM kill looks like from
            # in here: the kernel does not let the victim explain itself, so there is no
            # MemoryError for classify() to find. Under a container memory limit this is
            # the ordinary way a too-large job fails, so it must not read as "unexpected".
            if code in (-9, 137):
                raise JobFailure(classify(self._tail(job), "out_of_memory"), f"exit {code}")
            raise JobFailure(classify(self._tail(job)), f"exit {code}")

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
