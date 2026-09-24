"""One claimed job: download the bundle -> train (fitted to this machine) -> upload the slim .ply."""
import json
import logging
import os
import re
import shutil
import tempfile
import threading
import time

from computejobs.child import JobError

from ..bundle import BundleError, extract_bundle
from ..procs import ToolStopped
from ..splatio import read_ply, write_slim_ply
from ..training import train_fitted
from . import caps
from . import client as http
from .client import Backoff, Gone, Rejected, Transient, Unauthorized
from .heartbeat import Heartbeat

log = logging.getLogger("gpurunner")
NETWORK_PATIENCE_S = 240  # how long a job waits out a lost connection (download / upload) before giving up
MAX_STATS_BYTES = 8192
STEP = re.compile(r"step (\d+)/(\d+)")


class JobAbandoned(Exception):
    """The job cannot go on here (cancelled, gone, network lost for too long)."""


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
            if clock() - t0 > patience:
                raise JobAbandoned(f"{what}: gave up after {patience} s ({e})") from e
            delay = backoff.next(e.retry_after)
            log.info("%s failed (%s); retrying in %.0f s", what, e, delay)
            if backoff.n > 50:
                raise JobAbandoned(f"{what}: too many retries ({e})") from e
            http.pause(stop, delay)


def trim_stats(stats):
    """The X-Blocwerk-Stats document, kept under MAX_STATS_BYTES (retries dropped first)."""
    doc = dict(stats)
    while len(json.dumps(doc, separators=(",", ":"))) > MAX_STATS_BYTES and doc.get("retries"):
        doc["retries"] = doc["retries"][:-1]
    if len(json.dumps(doc, separators=(",", ":"))) > MAX_STATS_BYTES:
        doc = {k: v for k, v in doc.items() if not isinstance(v, (list, dict, str)) or k == "quality"}
    return doc


class JobRun:
    def __init__(self, client, job, work_dir, gpu_name, shutdown):
        self.client, self.job, self.gpu_name, self.shutdown = client, job, gpu_name, shutdown
        self.id = str(job["jobId"])
        self.dir = tempfile.mkdtemp(prefix="job-", dir=work_dir)
        self.stop = threading.Event()
        self.hb = Heartbeat(client, self.id, self.stop)

    def run(self):
        """Returns "succeeded", "cancelled", "failed" or "abandoned". Never raises Unauthorized upwards
        except when the key was revoked (the loop exits then)."""
        self.hb.start()
        watcher = threading.Thread(target=self._watch_shutdown, daemon=True)
        watcher.start()
        try:
            return self._run()
        finally:
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
            self.hb.set(stage="upload", fraction=1.0, detail="uploading the trained scene")
            with_retries("upload", lambda: self.client.upload_result(self.id, ply, "ply", trim_stats(stats)),
                         self.stop)
            log.info("job %s uploaded (%.1f MB)", self.id, os.path.getsize(ply) / 1e6)
            return "succeeded"
        except BundleError as e:
            return self._fail(f"bad bundle: {e}", retryable=False)
        except Rejected as e:
            return self._fail(f"the server refused the result: {e}", retryable=False)
        except (ToolStopped, JobAbandoned, Gone) as e:
            return self._stopped(e)
        except JobError as e:  # MemoryLimitError included: another runner (or a later run) may fit
            return self._stopped(e) if self.stop.is_set() else self._fail(str(e), retryable=True)
        except Unauthorized:
            self.stop.set()
            raise

    def _download_and_train(self):
        zip_path = os.path.join(self.dir, "bundle.zip")
        size = with_retries("download", lambda: self.client.download_bundle(
            self.id, zip_path, self.job.get("bundleBytes"), self.job.get("bundleSha256")), self.stop)
        log.info("job %s: bundle %.1f MB", self.id, size / 1e6)
        dataset, profile = extract_bundle(zip_path, os.path.join(self.dir, "b"))
        os.remove(zip_path)
        budget = caps.budget_mb()
        self.hb.set(stage="train", fraction=0.0, detail=f"{profile.name}, budget {budget / 1024:.1f} GB")
        t0 = time.time()
        out = train_fitted(dataset, os.path.join(self.dir, "train"), os.path.join(self.dir, "train.log"),
                           profile, budget, self._report, lambda t: log.info("job %s: %s", self.id, t),
                           lambda note: None, stop=self.stop)
        slim = os.path.join(self.dir, "splat.ply")
        write_slim_ply(read_ply(out.ply), slim)
        stats = {**out.stats, "trainingSeconds": round(time.time() - t0, 1), "runnerGpu": self.gpu_name,
                 "trainMemoryBudgetMb": budget, "retries": out.retries}
        return slim, stats

    def _report(self, fraction, detail=None):
        m = STEP.search(detail or "")
        step, total = (int(m.group(1)), int(m.group(2))) if m else (None, None)
        self.hb.set(fraction=round(float(fraction), 4), detail=detail, step=step, totalSteps=total)

    def _stopped(self, why):
        if self.shutdown.is_set() and not (self.hb.gone or self.hb.cancelled):
            return self._fail("the runner was shut down", retryable=True)
        log.info("job %s stopped: %s", self.id, why)
        return "cancelled" if self.hb.cancelled or self.hb.gone else "abandoned"

    def _fail(self, reason, retryable):
        log.warning("job %s failed: %s", self.id, reason)
        try:
            self.client.fail(self.id, reason, retryable)
        except (Transient, Gone, Rejected, Unauthorized) as e:
            log.info("could not report the failure (%s)", e)
        return "failed"
