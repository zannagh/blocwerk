"""The runner's main loop: hello -> claim (long-poll) -> JobRun -> claim ... with reconnect backoff.

One job at a time (the server hands a runner one claim). Every turn touches the liveness file (alive.py).
A 401 ends the loop with exit code 3 (the key was revoked or is wrong); a 404 / 410 on a job drops it and
the loop goes on; SIGTERM / SIGINT hand the running job back (fail with shutdown: true) and exit 0.
The pause switch (control.py) gates the claim: while paused the loop makes no claims, probes no trainer and only
says hello (cached capabilities, `paused: true`) every PAUSED_HELLO_S. Every finished job goes to the job history
(history.py) the local status page (web.py) shows."""
import glob
import logging
import os
import shutil
import threading
import time

from . import client as http
from .alive import Alive
from .client import Backoff, Gone, Rejected, Transient, Unauthorized
from .control import PauseControl, state_dir
from .history import JobHistory, JobStatus
from .job import JobRun
from .resume import ResumeSettings

log = logging.getLogger("gpurunner")
EXIT_OK, EXIT_MISCONFIGURED, EXIT_UNAUTHORIZED = 0, 2, 3
HELLO_EVERY_S = 600  # re-announce the capabilities (the free memory changes) every 10 minutes
PAUSED_HELLO_S = 30  # while paused: well inside the server's 60 s online window, so it shows "paused"
IDLE_TICK_S = 5.0  # while paused: how often the loop wakes (liveness file, shutdown)
MIN_CLAIM_INTERVAL_S = 2.0  # a server answering 204 at once must not be hammered


class Runner:
    def __init__(self, client, work_dir, capabilities, max_jobs=None, clock=time.monotonic, resume=None,
                 control=None, history=None):
        self.client, self.work_dir, self.capabilities = client, work_dir, capabilities
        self.max_jobs, self.clock = max_jobs, clock
        self.shutdown = threading.Event()
        self.backoff = Backoff()
        self.caps, self.hello_at, self.jobs_done = None, None, 0
        self.outcomes = []
        self.alive = Alive(work_dir)
        self.resume = resume or ResumeSettings(work_dir)
        self.control = control or PauseControl(state_dir(work_dir))
        self.history = history or JobHistory(state_dir(work_dir))
        self.current = None  # the running job's history.JobStatus
        self.reported_paused = None  # what the last hello told the server
        self.name, self.last_error, self.started_at = None, None, time.time()

    def stop(self):
        """SIGTERM / SIGINT: hand the running job back and end the loop (also from the idle wait)."""
        self.shutdown.set()
        self.control.changed.set()

    def run(self):
        """Returns the process exit code (0 on shutdown / max_jobs, 2 when the trainer turns out unusable on a
        resume, 3 when the key is refused)."""
        os.makedirs(self.work_dir, exist_ok=True)
        self._clean_stale()
        try:
            while not self.shutdown.is_set():
                self.alive.touch()
                if not self.control.claiming:
                    self._idle()
                    continue
                if getattr(self.capabilities, "usable", True) is False:
                    log.error("the %s trainer is not usable here: not taking jobs",
                              getattr(self.capabilities, "trainer", "configured"))
                    return EXIT_MISCONFIGURED
                if self._hello_due(HELLO_EVERY_S) and not self._hello():
                    continue
                if not self._claim_and_work():
                    break
        except Unauthorized as e:
            log.error("key revoked or invalid (%s): create a new runner key in Blocwerk", e)
            return EXIT_UNAUTHORIZED
        finally:
            self.alive.remove()
        return EXIT_OK

    def _clean_stale(self):
        """Job dirs a killed runner left behind (their bundles can be gigabytes) and checkpoints past their TTL."""
        for d in glob.glob(os.path.join(self.work_dir, "job-*")):
            shutil.rmtree(d, ignore_errors=True)
        if self.resume.prune():
            log.info("removed checkpoints older than %d h", self.resume.ttl_s // 3600)

    def _wait(self, error):
        delay = self.backoff.next(getattr(error, "retry_after", None))
        self.last_error = str(error)
        log.warning("server not reachable (%s); retrying in %.0f s", error, delay)
        http.pause(self.shutdown, delay)

    def _hello_due(self, every):
        paused = self.control.paused
        return self.hello_at is None or self.reported_paused != paused or self.clock() - self.hello_at > every

    def _idle(self):
        """Paused: no claim, no trainer probe, no GPU work; a hello now and then so the server shows "paused"."""
        self.control.settle()
        if self._hello_due(PAUSED_HELLO_S):
            self._hello()
        self.control.wait(IDLE_TICK_S)

    def _capabilities(self):
        """Fresh capabilities when claiming; while paused the last ones (or, never probed, those without the
        trainer probe: it imports torch in a child process)."""
        if self.control.claiming:
            return self.capabilities()
        if self.caps is None:
            lite = getattr(self.capabilities, "lite", None)
            return lite() if lite else self.capabilities()
        return self.caps

    def _hello(self):
        paused = self.control.paused
        self.caps = self._capabilities()
        try:
            me = self.client.hello({**self.caps, "paused": paused})
        except (Transient, Rejected) as e:
            self._wait(e)
            return False
        self.backoff.reset()
        self.hello_at, self.reported_paused, self.last_error = self.clock(), paused, None
        if me.get("name") != self.name or not paused:
            log.info("connected as runner %r (%s, %s VRAM, %s trainer, up to %s)%s", me.get("name"),
                     self.caps.get("gpuName"), f"{(self.caps.get('vramMb') or 0) / 1024:.1f} GB",
                     self.caps.get("trainer"), self.caps.get("maxQuality"), ", paused" if paused else "")
        self.name = me.get("name")
        return True

    def _claim_and_work(self):
        """One claim (+ the job if one came); False when the loop should end."""
        t0 = self.clock()
        try:
            job = self.client.claim(self.caps.get("maxQuality"))
        except (Transient, Rejected) as e:
            self._wait(e)
            return True
        self.backoff.reset()
        if job is None:
            if self.clock() - t0 < MIN_CLAIM_INTERVAL_S:
                http.pause(self.shutdown, MIN_CLAIM_INTERVAL_S)
            return True
        log.info("claimed job %s (%s)%s", job.get("jobId"), job.get("quality"),
                 ", again after a restart" if job.get("reattached") else "")
        if self.control.now.is_set():  # paused while the claim was long-polling: straight back, for free
            self._hand_back_paused(job)
            return True
        outcome = self._work(job)
        self.jobs_done += 1
        log.info("job %s %s", job.get("jobId"), outcome)
        self.resume.prune()  # checkpoints of jobs that never came back, also on a runner that runs for weeks
        return self.max_jobs is None or self.jobs_done < self.max_jobs

    def _work(self, job):
        status = JobStatus(job, self.client.server)
        self.current, outcome, run = status, "error", None
        self.control.set_busy(True)
        try:
            run = JobRun(self.client, job, self.work_dir, self.caps, self.shutdown, self.alive, self.resume,
                         pause=self.control.now, status=status)
            outcome = run.run()
            return outcome
        finally:
            self.control.set_busy(False)
            self.outcomes.append(outcome)
            self.history.append(status.finish(outcome, run.error if run else None,
                                              run.previews_uploaded if run else None))
            self.current = None

    def _hand_back_paused(self, job):
        try:
            self.client.fail(job["jobId"], "the runner was paused", True, shutdown=True, pause=True)
        except (Transient, Gone, Rejected) as e:
            log.info("could not hand job %s back (%s); its lease runs out", job.get("jobId"), e)

    def snapshot(self):
        """The status page's document (no key, no secrets)."""
        current = self.current
        return {"server": self.client.server, "runnerName": self.name, "startedAt": self.started_at,
                "pause": self.control.doc(), "busy": current is not None, "lastError": self.last_error,
                "caps": {k: (self.caps or {}).get(k) for k in ("gpuName", "vramMb", "maxQuality", "trainer",
                                                                "runnerVersion")},
                "current": current.snapshot() if current else None, "jobs": self.history.recent(50)}
