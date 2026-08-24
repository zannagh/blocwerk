"""In-process job queue, worker pool and TTL reaper.

Stitching is CPU- and memory-heavy, so the pool is deliberately tiny (one worker by
default). The queue is bounded: when it is full, submission is refused with 503
rather than accepting work the sidecar has no chance of getting to.
"""
from __future__ import annotations

import logging
import queue
import threading
from typing import Optional

from .errors import JobFailure, MESSAGES
from .runner import JobContext, PipelineRunner
from .store import JobStore

log = logging.getLogger("wallstitch.worker")


class JobQueueFull(Exception):
    """Raised when the bounded queue cannot take another job."""


class JobWorkerPool:
    """Runs queued jobs on a fixed number of daemon threads."""

    def __init__(self, store: JobStore, runner: PipelineRunner, workers: int, queue_limit: int):
        self.store = store
        self.runner = runner
        self.queue: "queue.Queue[Optional[JobContext]]" = queue.Queue(maxsize=max(1, queue_limit))
        self.threads = [threading.Thread(target=self._loop, name=f"stitch-{i}", daemon=True)
                        for i in range(max(1, workers))]
        self.active: dict[str, JobContext] = {}
        self.lock = threading.Lock()
        self.started = False

    def start(self) -> None:
        if self.started:
            return
        self.started = True
        for thread in self.threads:
            thread.start()

    def stop(self) -> None:
        for _ in self.threads:
            try:
                self.queue.put_nowait(None)
            except queue.Full:
                pass

    def submit(self, job: JobContext) -> None:
        try:
            self.queue.put_nowait(job)
        except queue.Full as exc:
            raise JobQueueFull() from exc

    def cancel(self, job_id: str) -> None:
        with self.lock:
            job = self.active.get(job_id)
        if job is not None:
            job.cancelled.set()

    @property
    def depth(self) -> int:
        return self.queue.qsize()

    def _loop(self) -> None:
        while True:
            job = self.queue.get()
            if job is None:
                return
            try:
                self._run(job)
            except Exception:  # noqa: BLE001 - a worker thread must never die
                log.exception("job %s crashed the worker loop", job.job_id)
            finally:
                self.queue.task_done()

    def _run(self, job: JobContext) -> None:
        record = self.store.read(job.job_id)
        if record is None or record.get("status") != "queued":
            return  # deleted or already resolved while it sat in the queue

        with self.lock:
            self.active[job.job_id] = job
        self.store.update(job.job_id, status="running", progress=0.01, stage="preparing")

        def on_progress(progress: float, stage: str) -> None:
            if self.store.read(job.job_id) is None:
                job.cancelled.set()
                return
            self.store.update(job.job_id, progress=progress, stage=stage)

        try:
            result = self.runner.run(job, on_progress)
            self.store.update(job.job_id, status="succeeded", progress=1.0,
                              stage="completed", result=result, error=None)
        except JobFailure as failure:
            log.warning("job %s failed: %s (%s)", job.job_id, failure.code, failure.detail)
            self.store.update(job.job_id, status="failed", stage=None,
                              error={"code": failure.code, "message": failure.message})
        except Exception as exc:  # noqa: BLE001 - never leak the traceback to the caller
            log.exception("job %s failed unexpectedly", job.job_id)
            self.store.update(job.job_id, status="failed", stage=None,
                              error={"code": "pipeline_failed",
                                     "message": MESSAGES["pipeline_failed"]})
            del exc
        finally:
            with self.lock:
                self.active.pop(job.job_id, None)


class TtlReaper:
    """Deletes job directories older than the TTL so the volume cannot grow unbounded."""

    def __init__(self, store: JobStore, ttl_seconds: int, interval_seconds: int):
        self.store = store
        self.ttl_seconds = ttl_seconds
        self.interval_seconds = max(30, interval_seconds)
        self.stopping = threading.Event()
        self.thread = threading.Thread(target=self._loop, name="stitch-reaper", daemon=True)

    def start(self) -> None:
        self.thread.start()

    def stop(self) -> None:
        self.stopping.set()

    def sweep(self) -> int:
        removed = self.store.reap(self.ttl_seconds)
        if removed:
            log.info("reaped %d expired job directories", len(removed))
        return len(removed)

    def _loop(self) -> None:
        while not self.stopping.wait(self.interval_seconds):
            try:
                self.sweep()
            except Exception:  # noqa: BLE001
                log.exception("ttl sweep failed")
