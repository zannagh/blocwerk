"""The runner's main loop: hello -> claim (long-poll) -> JobRun -> claim ... with reconnect backoff.

One job at a time (the server hands a runner one claim). Every turn touches the liveness file (alive.py).
A 401 ends the loop with exit code 3 (the key was revoked or is wrong); a 404 / 410 on a job drops it and
the loop goes on; SIGTERM / SIGINT hand the running job back (fail with shutdown: true) and exit 0."""
import glob
import logging
import os
import shutil
import threading
import time

from . import client as http
from .alive import Alive
from .client import Backoff, Rejected, Transient, Unauthorized
from .job import JobRun

log = logging.getLogger("gpurunner")
EXIT_OK, EXIT_UNAUTHORIZED = 0, 3
HELLO_EVERY_S = 600  # re-announce the capabilities (the free memory changes) every 10 minutes
MIN_CLAIM_INTERVAL_S = 2.0  # a server answering 204 at once must not be hammered


class Runner:
    def __init__(self, client, work_dir, capabilities, max_jobs=None, clock=time.monotonic):
        self.client, self.work_dir, self.capabilities = client, work_dir, capabilities
        self.max_jobs, self.clock = max_jobs, clock
        self.shutdown = threading.Event()
        self.backoff = Backoff()
        self.caps, self.hello_at, self.jobs_done = None, None, 0
        self.outcomes = []
        self.alive = Alive(work_dir)

    def run(self):
        """Returns the process exit code (0 on shutdown / max_jobs, 3 when the key is refused)."""
        os.makedirs(self.work_dir, exist_ok=True)
        self._clean_stale()
        try:
            while not self.shutdown.is_set():
                self.alive.touch()
                if self.hello_at is None or self.clock() - self.hello_at > HELLO_EVERY_S:
                    if not self._hello():
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
        """Job dirs a killed runner left behind (their bundles can be gigabytes)."""
        for d in glob.glob(os.path.join(self.work_dir, "job-*")):
            shutil.rmtree(d, ignore_errors=True)

    def _wait(self, error):
        delay = self.backoff.next(getattr(error, "retry_after", None))
        log.warning("server not reachable (%s); retrying in %.0f s", error, delay)
        http.pause(self.shutdown, delay)

    def _hello(self):
        self.caps = self.capabilities()
        try:
            me = self.client.hello(self.caps)
        except (Transient, Rejected) as e:
            self._wait(e)
            return False
        self.backoff.reset()
        self.hello_at = self.clock()
        log.info("connected as runner %r (%s, %s VRAM, %s trainer, up to %s)", me.get("name"), self.caps.get("gpuName"),
                 f"{(self.caps.get('vramMb') or 0) / 1024:.1f} GB", self.caps.get("trainer"), self.caps.get("maxQuality"))
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
        log.info("claimed job %s (%s)", job.get("jobId"), job.get("quality"))
        outcome = JobRun(self.client, job, self.work_dir, self.caps, self.shutdown, self.alive).run()
        self.outcomes.append(outcome)
        self.jobs_done += 1
        log.info("job %s %s", job.get("jobId"), outcome)
        return self.max_jobs is None or self.jobs_done < self.max_jobs
