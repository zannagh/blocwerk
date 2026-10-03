"""The pause switch: "don't take new jobs", kept in <state dir>/pause.json so it survives a restart.

Modes: running (claims jobs); after-job (finish the running job, then paused); paused (no claims, no trainer, no GPU
probes: the loop idles and only says hello with `paused: true` now and then, so the server shows the runner as paused,
not offline). `now` is set by a "pause now": the running job is stopped and handed back as a free pause
(job.py, `fail` with `shutdown` + `pause`). A process that starts in after-job holds no job, so it starts paused.
RUNNER_STATE_DIR (default <work dir>/state) holds this file and the job history (history.py). Stdlib only."""
import json
import logging
import os
import tempfile
import threading
import time

log = logging.getLogger("gpurunner")
RUNNING, AFTER_JOB, PAUSED = "running", "after-job", "paused"


def state_dir(work_dir, env=None):
    env = os.environ if env is None else env
    return env.get("RUNNER_STATE_DIR") or os.path.join(work_dir, "state")


def write_json(path, doc):
    """Atomic: a crash mid-write leaves the old file."""
    os.makedirs(os.path.dirname(path), exist_ok=True)
    fd, tmp = tempfile.mkstemp(prefix=".tmp-", dir=os.path.dirname(path))
    with os.fdopen(fd, "w") as fh:
        json.dump(doc, fh)
    os.replace(tmp, path)


class PauseControl:
    def __init__(self, directory, clock=time.time):
        self.path, self.clock = os.path.join(directory, "pause.json"), clock
        self.lock = threading.Lock()
        self.changed = threading.Event()  # wakes the idle loop (resume, shutdown)
        self.now = threading.Event()  # "pause now": stops the running job
        self.busy = False  # a job is running (set by the loop)
        self.mode, self.since = self._load()
        if self.mode == PAUSED:
            self.now.set()

    def _load(self):
        try:
            with open(self.path) as fh:
                doc = json.load(fh)
        except (OSError, ValueError):
            return RUNNING, None
        mode = doc.get("mode") if isinstance(doc, dict) else None
        if mode in (PAUSED, AFTER_JOB):
            return PAUSED, doc.get("since")
        return RUNNING, None

    def _set(self, mode):
        """Under the lock."""
        if mode != self.mode:
            self.mode, self.since = mode, (self.clock() if mode != RUNNING else None)
            try:
                write_json(self.path, {"mode": mode, "since": self.since})
            except OSError as e:
                log.warning("could not keep the pause state in %s (%s): it is lost on a restart", self.path, e)
            log.info("pause switch: %s", mode)
        self.changed.set()

    @property
    def claiming(self):
        return self.mode == RUNNING

    @property
    def paused(self):
        return self.mode == PAUSED

    def pause(self, now=True):
        """Now: stop the running job (handed back for free) and take no more. Else: finish it first; with no job
        running that is the same as now."""
        with self.lock:
            if now or not self.busy or self.mode == PAUSED:
                self.now.set()
                self._set(PAUSED)
            else:
                self._set(AFTER_JOB)

    def resume(self):
        with self.lock:
            self.now.clear()
            self._set(RUNNING)

    def settle(self):
        """Between jobs: an after-job pause becomes a pause."""
        with self.lock:
            if self.mode == AFTER_JOB:
                self.now.set()
                self._set(PAUSED)

    def set_busy(self, busy):
        with self.lock:
            self.busy = busy

    def wait(self, seconds):
        """Idles until the switch changes (or `seconds` pass)."""
        if self.changed.wait(seconds):
            self.changed.clear()

    def doc(self):
        return {"mode": self.mode, "since": self.since}
