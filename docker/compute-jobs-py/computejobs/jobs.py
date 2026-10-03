"""In-memory job queue with ONE worker (single instance by design; state is lost on restart).

Every job gets a directory under WORK_DIR for its inputs and output files, and runs in a child
process (spawned, not forked: the parent is multi-threaded) that leads its own process group, so a
timeout or cancel also kills whatever the job started (COLMAP, a trainer, ...). Finished jobs and
their files are deleted RESULT_TTL_S after they finish.

Child -> parent messages over a multiprocessing Pipe (see child.py):
  ("progress", fraction, stage[, detail]) ... then exactly one of ("done", result) / ("error", message).
"""
import logging
import multiprocessing as mp
import os
import queue
import shutil
import signal
import threading
import time
import uuid
from datetime import datetime, timezone

from .callbacks import CallbackSender
from .settings import settings

log = logging.getLogger("computejobs.jobs")
TERMINAL = {"succeeded", "failed", "cancelled"}


def _now():
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


class QueueFull(Exception):
    pass


class Job:
    def __init__(self, kind, callback_url=None):
        self.id = uuid.uuid4().hex
        self.kind = kind
        self.dir = os.path.join(settings.work_dir, self.id)
        os.makedirs(self.dir, exist_ok=True)
        self.status, self.progress, self.stage, self.message, self.error = "queued", 0.0, "queued", None, None
        self.stage_detail = None
        self.created = self.updated = _now()
        self.finished_at = None
        self.result, self.files = None, []
        self.callback_url = callback_url
        self.cancel_requested = False

    def to_status(self, url_prefix=""):
        result = self.result
        if result is not None and self.files:
            result = {**result, "files": [{"name": f, "url": f"{url_prefix}/v1/jobs/{self.id}/files/{f}"}
                                          for f in self.files]}
        return {"jobId": self.id, "kind": self.kind, "status": self.status,
                "progress": round(self.progress, 3), "stage": self.stage, "stageDetail": self.stage_detail,
                "message": self.message, "error": self.error, "createdAt": self.created,
                "updatedAt": self.updated, "result": result}


def _on_sigterm(_signum, _frame):
    """Take the tools this job started down with it (also when the service itself shuts down and
    multiprocessing SIGTERMs its daemonic children)."""
    signal.signal(signal.SIGTERM, signal.SIG_DFL)
    os.killpg(0, signal.SIGKILL)


def _child_entry(runner, kind, job_dir, conn):
    """Child-process entry: lead a new process group so the parent can kill the whole tree."""
    try:
        os.setsid()
        signal.signal(signal.SIGTERM, _on_sigterm)
    except OSError:
        pass
    runner(kind, job_dir, conn)


def _kill_tree(proc):
    """SIGTERM the job's process group, SIGKILL whatever is left after 3 s."""
    for sig, wait in ((signal.SIGTERM, 3), (signal.SIGKILL, 2)):
        try:
            os.killpg(proc.pid, sig)
        except (ProcessLookupError, PermissionError, OSError):
            if proc.is_alive():
                proc.terminate() if sig == signal.SIGTERM else proc.kill()
        proc.join(wait)
        if not proc.is_alive() and sig == signal.SIGTERM:
            try:  # leader gone; make sure no grandchild survives it
                os.killpg(proc.pid, signal.SIGKILL)
            except (ProcessLookupError, PermissionError, OSError):
                pass
            return


class JobManager:
    """runner(kind, job_dir, conn) runs in the child; timeout_for(kind) -> seconds."""

    def __init__(self, runner, timeout_for):
        self.runner, self.timeout_for = runner, timeout_for
        self.jobs, self.lock = {}, threading.Lock()
        self.q = queue.Queue()
        self.callbacks = CallbackSender()
        self.ctx = mp.get_context("spawn")
        os.makedirs(settings.work_dir, exist_ok=True)
        threading.Thread(target=self._worker, name="worker", daemon=True).start()
        threading.Thread(target=self._janitor, name="janitor", daemon=True).start()

    # ---------- API ----------
    def create(self, kind, callback_url=None):
        with self.lock:
            waiting = sum(1 for j in self.jobs.values() if j.status == "queued")
            if waiting >= settings.max_queued:
                raise QueueFull()
            job = Job(kind, callback_url)
            self.jobs[job.id] = job
        return job

    def submit(self, job):
        self._notify(job)
        self.q.put(job.id)
        log.info("job %s (%s) queued", job.id, job.kind)

    def discard(self, job):
        with self.lock:
            self.jobs.pop(job.id, None)
        shutil.rmtree(job.dir, ignore_errors=True)

    def get(self, job_id):
        with self.lock:
            return self.jobs.get(job_id)

    def cancel(self, job_id):
        job = self.get(job_id)
        if job is None:
            return None
        if job.status == "queued":
            self._finish(job, "cancelled", message="cancelled before it started", stage="cancelled")
        elif job.status == "running":
            job.cancel_requested = True
        return job

    def counts(self):
        with self.lock:
            st = [j.status for j in self.jobs.values()]
        return {"queued": st.count("queued"), "running": st.count("running")}

    # ---------- internals ----------
    def _set(self, job, notify=False, **kw):
        for k, v in kw.items():
            setattr(job, k, v)
        job.updated = _now()
        if notify:
            self._notify(job)

    def _notify(self, job):
        self.callbacks.send(job.callback_url, job.to_status())

    def _finish(self, job, status, **kw):
        self._set(job, status=status, finished_at=time.time(), stage_detail=None, **kw)
        if status == "succeeded":
            job.progress = 1.0
        self._notify(job)
        log.info("job %s (%s) %s", job.id, job.kind, status)

    def _worker(self):
        while True:
            job = self.get(self.q.get())
            if job is None or job.status != "queued":
                continue
            try:
                self._run(job)
            except Exception as e:  # noqa: BLE001 - the worker must survive anything
                log.exception("job %s crashed the worker loop", job.id)
                self._finish(job, "failed", error=f"internal error: {type(e).__name__}")

    def _await_outcome(self, job, proc, parent, t0, timeout):
        while True:
            if job.cancel_requested:
                return "cancelled", "cancelled while running"
            if time.time() - t0 > timeout:
                return "failed", f"{job.stage}: timed out after {timeout} s"
            if parent.poll(0.5):
                try:
                    msg = parent.recv()
                except EOFError:
                    return "failed", f"{job.stage}: worker process died (exit code {proc.exitcode})"
                if msg[0] == "progress":
                    detail = msg[3] if len(msg) > 3 else None
                    progress = max(job.progress, msg[1])  # monotone within a run
                    self._set(job, progress=progress, stage=msg[2], stage_detail=detail)
                elif msg[0] == "done":
                    return "succeeded", msg[1]
                else:
                    return "failed", msg[1]
            elif not proc.is_alive() and not parent.poll(0):
                return "failed", f"{job.stage}: worker process died (exit code {proc.exitcode})"

    def _run(self, job):
        timeout = self.timeout_for(job.kind)
        parent, child = self.ctx.Pipe(duplex=False)
        proc = self.ctx.Process(target=_child_entry, args=(self.runner, job.kind, job.dir, child), daemon=True)
        t0 = time.time()
        self._set(job, notify=True, status="running", stage="starting")
        proc.start()
        child.close()
        try:
            status, payload = self._await_outcome(job, proc, parent, t0, timeout)
        finally:
            _kill_tree(proc)
            parent.close()
        log.info("job %s ran %.1f s", job.id, time.time() - t0)
        if status == "succeeded":
            files = payload.pop("files", [])
            self._finish(job, "succeeded", result=payload, files=files, stage="done")
        elif status == "cancelled":
            self._finish(job, "cancelled", message=payload, stage="cancelled")
        else:
            self._finish(job, "failed", error=payload, stage="failed")

    def _janitor(self):
        while True:
            time.sleep(30)
            cutoff = time.time() - settings.result_ttl_s
            with self.lock:
                old = [j for j in self.jobs.values() if j.finished_at and j.finished_at < cutoff]
                for j in old:
                    del self.jobs[j.id]
            for j in old:
                shutil.rmtree(j.dir, ignore_errors=True)
