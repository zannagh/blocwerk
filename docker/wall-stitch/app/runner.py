"""Executing the vendored pipeline for one job.

Everything here treats the pipeline as a black-box CLI: build an argv, stream its stdout
to derive progress, then hand the work directory to `results` to be read. This module is
process plumbing only; what the artifacts mean lives next door.
"""
from __future__ import annotations

import os
import subprocess
import threading
from typing import Callable, Dict, List, Optional, Protocol

from . import holdsio, invocation, results
from .config import Settings
from .errors import JobFailure, classify
from .stages import PIPELINE_MARKERS, ProgressTracker

ProgressCallback = Callable[[float, str], None]

SCRIPT = "wall_pipeline.py"


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
    """Runs `wall_pipeline.py` once, as a child process, and reads what it left."""

    def __init__(self, settings: Settings):
        self.settings = settings

    def run(self, job: JobContext, on_progress: ProgressCallback) -> Dict[str, object]:
        os.makedirs(job.work_dir, exist_ok=True)
        prior_holds = ""
        if job.options.transfer_holds and job.options.holds and job.old_photo:
            prior_holds = holdsio.write_prior_holds(
                os.path.join(job.work_dir, "prior"), job.options.holds)

        argv = invocation.pipeline_command(
            self.settings.python_executable, self.settings.pipeline_dir,
            self.settings.pipeline_script, job.input_dir, job.work_dir,
            os.path.join(job.work_dir, ".cache"),
            natural=job.options.natural or self.settings.natural,
            curve=job.options.curve or self.settings.curve,
            onnx_model=self.settings.onnx_model,
            wall_width_m=job.options.wall_width_m,
            wall_height_m=job.options.wall_height_m,
            work_mp=self.settings.work_mp,
            compose_mp=self.settings.compose_mp,
            max_canvas_mpx=self.settings.max_canvas_mpx,
            nfeat=self.settings.nfeat,
            prior_holds=prior_holds,
            prior_image=job.old_photo or "")

        tracker = ProgressTracker(PIPELINE_MARKERS, 0.02, 0.97, "registering")
        self._execute(job, argv, self.settings.pipeline_dir, tracker, on_progress)

        manifest = results.read_manifest(job.work_dir)
        if not manifest:
            raise JobFailure(classify(self._tail(job), "pipeline_failed"),
                             "pipeline produced no manifest")

        on_progress(0.98, "packaging")
        publisher = results.Publisher(job.work_dir, job.artifact_dir, self.settings)
        result = results.assemble(manifest, publisher, job.photos)
        if result is None:
            raise JobFailure(classify(self._tail(job), "pipeline_failed"),
                             "pipeline produced no master image")
        return result

    # ---- process plumbing -------------------------------------------------------

    def _execute(self, job: JobContext, argv, cwd: str, tracker,
                 on_progress: ProgressCallback) -> None:
        env = dict(os.environ)
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
