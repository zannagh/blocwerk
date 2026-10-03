"""One claimed job: download the bundle (resumable) -> train (train.py: the worker's own trainer path) ->
upload the slim .ply (gzip). Outcomes: succeeded, failed (reported), cancelled (the server says the job is over
for good: dropped with its checkpoints), gone (requeued or taken away: dropped, checkpoints kept), shutdown
(handed back with `shutdown: true` and the newest checkpoint's step, no attempt used), paused (the owner paused the
runner now: handed back like a shutdown, with `pause: true`, free), abandoned (network lost
for too long; reported as `unreachable` so the server need not wait for the lease, at no training attempt). A
gsplat job saves
checkpoints (a retry of the same job and bundle resumes from them; dropped once the job is over for good) and
uploads previews while it trains (previews.py; resume.py)."""
import json
import logging
import os
import re
import shutil
import tempfile
import threading
import time

from computejobs.child import JobError

from .. import checkpoints, procs
from ..bundle import BundleError, extract_bundle
from ..procs import ToolStopped
from ..slimply import write_slim_ply
from ..splatio import read_ply
from . import client as http
from .client import Backoff, BundleCorrupt, BundleTooLarge, Gone, Rejected, Stopped, Transient, Unauthorized
from .heartbeat import Heartbeat
from .previews import PreviewUploader
from .train import train_bundle

log = logging.getLogger("gpurunner")
NETWORK_PATIENCE_S = 240  # how long a job waits out a lost connection (download / upload) before giving up
UPLOAD_PATIENCE_S = 1800  # an upload waits longer: the server may be busy (429) or short of disk (507)
MAX_STATS_BYTES = 8192
MAX_CORRUPT_DOWNLOADS = 3  # a bundle that arrives damaged this often: handed back as a failure of this runner
STEP = re.compile(r"step (\d+)/(\d+)")


class JobAbandoned(Exception):
    """The job cannot go on here (stopped, network lost for too long)."""


class BundleDamaged(Exception):
    """The bundle kept arriving damaged: maybe this runner's disk or network, maybe the server's copy. Reported as a
    retryable failure, so another runner tries it next and the attempt budget ends it if the server's copy is bad."""


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
    def __init__(self, client, job, work_dir, caps, shutdown, alive=None, resume=None, pause=None, status=None):
        """pause: the pause switch's "now" event (control.py); status: the job's live record (history.JobStatus)."""
        self.client, self.job, self.caps, self.shutdown, self.pause = client, job, caps or {}, shutdown, pause
        self.id = str(job["jobId"])
        self.dir = tempfile.mkdtemp(prefix="job-", dir=work_dir)
        self.stop = threading.Event()
        self.hb = Heartbeat(client, self.id, self.stop, alive, observer=status.update if status else None)
        if status is not None:
            status.update(dict(self.hb.state))
        self.error, self.previews_uploaded = None, None  # for the job history
        # checkpoints (kept for a retry unless the job is over for good) and previews: resume.py
        self.resume = resume.for_job(job, self.dir) if resume else checkpoints.TrainResume()
        self.over = False
        self.corrupt_downloads = 0

    def run(self):
        """Returns the outcome. Raises Unauthorized only when the key was refused (the loop exits then)."""
        self.hb.start()
        watcher = threading.Thread(target=self._watch_shutdown, daemon=True)
        watcher.start()
        procs.current_stop = self.stop  # kills Brush / gsplat on cancel or shutdown
        outcome = None
        try:
            outcome = self._run()
            return outcome
        finally:
            procs.current_stop = None
            self.hb.close()
            shutil.rmtree(self.dir, ignore_errors=True)
            if outcome in ("succeeded", "cancelled") or self.over:
                checkpoints.discard(self.resume.checkpoint_dir)

    def _pausing(self):
        return self.pause is not None and self.pause.is_set()

    def _watch_shutdown(self):
        while not self.stop.is_set() and not self.hb.done.is_set():
            if self.shutdown.wait(0.5) or self._pausing():
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
        except (BundleTooLarge, BundleDamaged) as e:
            return self._fail(str(e), retryable=True)
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
        size = with_retries("download", lambda: self._download(zip_path), self.stop)
        log.info("job %s: bundle %.1f MB", self.id, size / 1e6)
        dataset, profile, zones = extract_bundle(zip_path, os.path.join(self.dir, "b"))
        os.remove(zip_path)
        self.hb.set(stage="train", fraction=0.0, detail=f"{profile.name}{', wall zones' if zones else ''}")
        train_dir = os.path.join(self.dir, "t")
        os.makedirs(train_dir)
        previews = None
        if self.resume.preview_dir:
            os.makedirs(self.resume.preview_dir, exist_ok=True)
            previews = PreviewUploader(self.client, self.id, self.resume.preview_dir, dataset, self.stop).start()
        try:
            ply, stats = train_bundle(dataset, train_dir, profile, zones, self._report, self.resume)
        finally:
            if previews is not None:
                previews.close()  # an unfinished preview upload yields to the final result
        if previews is not None:
            stats["previewsUploaded"] = self.previews_uploaded = len(previews.uploaded)
        slim = os.path.join(self.dir, "splat.ply")
        write_slim_ply(read_ply(ply), slim)
        shutil.rmtree(train_dir, ignore_errors=True)
        shutil.rmtree(os.path.join(self.dir, "b"), ignore_errors=True)
        return slim, {**stats, "runnerGpu": self.caps.get("gpuName"), "runnerVersion": self.caps.get("runnerVersion"),
                      "runnerTrainer": self.caps.get("trainer"), "runnerPlatform": self.caps.get("platform")}

    def _download(self, zip_path):
        """One download attempt. A bundle that keeps arriving damaged (checksum, size) is given back as this
        runner's failure after MAX_CORRUPT_DOWNLOADS, instead of retrying until the network patience runs out."""
        try:
            return self.client.download_bundle(self.id, zip_path, self.job.get("bundleBytes"),
                                               self.job.get("bundleSha256"), stop=self.stop)
        except BundleCorrupt as e:
            self.corrupt_downloads += 1
            if self.corrupt_downloads >= MAX_CORRUPT_DOWNLOADS:
                raise BundleDamaged(f"{e} after {self.corrupt_downloads} downloads") from e
            raise

    def _report(self, fraction, stage=None, detail=None):
        m = STEP.search(detail or "")
        step, total = (int(m.group(1)), int(m.group(2))) if m else (None, None)
        self.hb.set(fraction=round(float(fraction), 4), detail=(detail or "")[:300] or None, step=step,
                    totalSteps=total, stage="train")

    def _stopped(self, why):
        if self.hb.revoked:
            raise Unauthorized(str(why))
        over = self.hb.cancelled or (isinstance(why, Gone) and why.over)
        pausing = self._pausing() and not self.shutdown.is_set()  # a process that stops is offline: a shutdown
        if (self.shutdown.is_set() or pausing) and not (self.hb.gone or over):
            step = checkpoints.newest_step(self.resume.checkpoint_dir)
            what = "paused" if pausing else "shutting down"
            log.info("job %s: the runner is %s; handing it back (checkpoint: %s)", self.id, what, step)
            reason = "the runner was paused" if pausing else "the runner was shut down"
            self._report_failure(reason, retryable=True, shutdown=True, checkpoint_step=step, pause=pausing)
            return "paused" if pausing else "shutdown"
        log.info("job %s stopped: %s", self.id, why)
        self.error = str(why)
        if over:
            return "cancelled"
        if self.hb.gone or isinstance(why, Gone):
            return "gone"
        # Given up (the server stayed out of reach or busy): reported as unreachable (no training attempt, this runner
        # not marked as failing it), so the reason is stored and the job is requeued now, not when the lease runs out.
        self._report_failure(f"the runner gave up: {why}", retryable=True, unreachable=True)
        return "abandoned"

    def _fail(self, reason, retryable):
        log.warning("job %s failed: %s", self.id, reason)
        self.over = not retryable
        self._report_failure(reason, retryable)
        return "failed"

    def _report_failure(self, reason, retryable, shutdown=False, checkpoint_step=None, unreachable=False, pause=False):
        self.error = reason
        try:
            self.client.fail(self.id, reason, retryable, shutdown, checkpoint_step, unreachable, pause)
        except (Transient, Gone, Rejected, Unauthorized) as e:
            log.info("could not report the failure (%s)", e)
