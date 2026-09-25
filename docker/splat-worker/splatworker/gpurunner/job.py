"""One claimed job: download the bundle (resumable) -> train (train.py: the worker's own trainer path) ->
upload the slim .ply (gzip). Outcomes: succeeded, failed (reported), cancelled / gone (dropped), shutdown
(handed back with `shutdown: true`, no attempt used), abandoned (network lost for too long)."""
import json
import logging
import os
import re
import shutil
import tempfile
import threading
import time

from computejobs.child import JobError

from .. import procs
from ..bundle import BundleError, extract_bundle
from ..procs import ToolStopped
from ..slimply import write_slim_ply
from ..splatio import read_ply
from . import client as http
from .client import Backoff, Gone, Rejected, Stopped, Transient, Unauthorized
from .heartbeat import Heartbeat
from .train import train_bundle

log = logging.getLogger("gpurunner")
NETWORK_PATIENCE_S = 240  # how long a job waits out a lost connection (download / upload) before giving up
UPLOAD_PATIENCE_S = 1800  # an upload waits longer: the server may be busy (429) or short of disk (507)
MAX_STATS_BYTES = 8192
STEP = re.compile(r"step (\d+)/(\d+)")


class JobAbandoned(Exception):
    """The job cannot go on here (stopped, network lost for too long)."""


def with_retries(what, call, stop, patience=None, clock=time.monotonic):
    """call() until it succeeds; Transient errors are retried with backoff for `patience` seconds."""
    patience = NETWORK_PATIENCE_S if patience is None else patience
    backoff, t0 = Backoff(), clock()
    while True:
        if stop.is_set():
            raise JobAbandoned(f"{what}: stopped")
        try:
            return call()
        except Transient as e:
            if clock() - t0 > patience or backoff.n > 50:
                raise JobAbandoned(f"{what}: gave up after {clock() - t0:.0f} s ({e})") from e
            delay = backoff.next(e.retry_after)
            log.info("%s failed (%s); retrying in %.0f s", what, e, delay)
            http.pause(stop, delay)


def trim_stats(stats):
    """The X-Blocwerk-Stats document: flat, under MAX_STATS_BYTES (retries dropped first)."""
    doc = {k: v for k, v in stats.items() if k == "retries" or not isinstance(v, (dict, list))}
    while len(json.dumps(doc, separators=(",", ":"))) > MAX_STATS_BYTES and doc.get("retries"):
        doc["retries"] = doc["retries"][:-1]
    if len(json.dumps(doc, separators=(",", ":"))) > MAX_STATS_BYTES:
        doc = {k: v for k, v in doc.items() if not isinstance(v, (list, str)) or k == "quality"}
    return doc


class JobRun:
    def __init__(self, client, job, work_dir, caps, shutdown, alive=None):
        self.client, self.job, self.caps, self.shutdown = client, job, caps or {}, shutdown
        self.id = str(job["jobId"])
        self.dir = tempfile.mkdtemp(prefix="job-", dir=work_dir)
        self.stop = threading.Event()
        self.hb = Heartbeat(client, self.id, self.stop, alive)

    def run(self):
        """Returns the outcome. Raises Unauthorized only when the key was refused (the loop exits then)."""
        self.hb.start()
        watcher = threading.Thread(target=self._watch_shutdown, daemon=True)
        watcher.start()
        procs.current_stop = self.stop  # kills Brush / gsplat on cancel or shutdown
        try:
            return self._run()
        finally:
            procs.current_stop = None
            self.hb.close()
            shutil.rmtree(self.dir, ignore_errors=True)

    def _watch_shutdown(self):
        while not self.stop.is_set() and not self.hb.done.is_set():
            if self.shutdown.wait(0.5):
                self.stop.set()
                return

    def _run(self):
        try:
            ply, stats = self._download_and_train()
            self.hb.set(stage="upload", fraction=1.0, detail="uploading the trained scene", step=None)
            sent = with_retries("upload", lambda: self.client.upload_result(self.id, ply, trim_stats(stats), self.stop),
                                self.stop, UPLOAD_PATIENCE_S)
            log.info("job %s uploaded (%.1f MB sent, %.1f MB scene)", self.id, sent / 1e6, os.path.getsize(ply) / 1e6)
            return "succeeded"
        except BundleError as e:
            return self._fail(f"bad bundle: {e}", retryable=False)
        except Rejected as e:
            return self._fail(f"the server refused the result: {e}", retryable=False)
        except (ToolStopped, JobAbandoned, Gone, Stopped) as e:
            return self._stopped(e)
        except JobError as e:  # MemoryLimitError / CudaOomError included: another runner may fit
            return self._stopped(e) if self.stop.is_set() else self._fail(str(e), retryable=True)
        except Unauthorized:
            self.stop.set()
            raise
        except Exception as e:  # noqa: BLE001 - a bug here must not end the runner; the job may work elsewhere
            log.exception("job %s: unexpected error", self.id)
            return self._fail(f"runner error: {type(e).__name__}: {e}", retryable=True)

    def _download_and_train(self):
        zip_path = os.path.join(self.dir, "bundle.zip")
        size = with_retries("download", lambda: self.client.download_bundle(
            self.id, zip_path, self.job.get("bundleBytes"), self.job.get("bundleSha256"), stop=self.stop), self.stop)
        log.info("job %s: bundle %.1f MB", self.id, size / 1e6)
        dataset, profile, zones = extract_bundle(zip_path, os.path.join(self.dir, "b"))
        os.remove(zip_path)
        self.hb.set(stage="train", fraction=0.0, detail=f"{profile.name}{', wall zones' if zones else ''}")
        train_dir = os.path.join(self.dir, "t")
        os.makedirs(train_dir)
        ply, stats = train_bundle(dataset, train_dir, profile, zones, self._report)
        slim = os.path.join(self.dir, "splat.ply")
        write_slim_ply(read_ply(ply), slim)
        shutil.rmtree(train_dir, ignore_errors=True)
        shutil.rmtree(os.path.join(self.dir, "b"), ignore_errors=True)
        return slim, {**stats, "runnerGpu": self.caps.get("gpuName"), "runnerVersion": self.caps.get("runnerVersion"),
                      "runnerTrainer": self.caps.get("trainer"), "runnerPlatform": self.caps.get("platform")}

    def _report(self, fraction, stage=None, detail=None):
        m = STEP.search(detail or "")
        step, total = (int(m.group(1)), int(m.group(2))) if m else (None, None)
        self.hb.set(fraction=round(float(fraction), 4), detail=(detail or "")[:300] or None, step=step,
                    totalSteps=total, stage="train")

    def _stopped(self, why):
        if self.hb.revoked:
            raise Unauthorized(str(why))
        if self.shutdown.is_set() and not (self.hb.gone or self.hb.cancelled):
            log.info("job %s: the runner is shutting down; handing it back", self.id)
            self._report_failure("the runner was shut down", retryable=True, shutdown=True)
            return "shutdown"
        log.info("job %s stopped: %s", self.id, why)
        if self.hb.cancelled:
            return "cancelled"
        return "gone" if self.hb.gone or isinstance(why, Gone) else "abandoned"

    def _fail(self, reason, retryable):
        log.warning("job %s failed: %s", self.id, reason)
        self._report_failure(reason, retryable)
        return "failed"

    def _report_failure(self, reason, retryable, shutdown=False):
        try:
            self.client.fail(self.id, reason, retryable, shutdown)
        except (Transient, Gone, Rejected, Unauthorized) as e:
            log.info("could not report the failure (%s)", e)
