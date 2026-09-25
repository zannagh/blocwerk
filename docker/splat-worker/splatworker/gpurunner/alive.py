"""The runner's liveness file: the loop (and the job's heartbeat thread) touch `<work dir>/alive`; the
container's HEALTHCHECK (gpurunner.health) calls the runner healthy while it is younger than MAX_AGE_S.
Stdlib only: the health check imports this without the worker's dependencies."""
import os
import tempfile
import time

MAX_AGE_S = 120
MIN_TOUCH_INTERVAL_S = 5.0


def work_dir():
    """RUNNER_WORK_DIR, else <tmp>/blocwerk-runner (the same default for the runner and its health check)."""
    return os.environ.get("RUNNER_WORK_DIR") or os.path.join(tempfile.gettempdir(), "blocwerk-runner")


def path(directory=None):
    return os.path.join(directory or work_dir(), "alive")


class Alive:
    """touch() at most every MIN_TOUCH_INTERVAL_S (the loop calls it on every turn); never raises."""

    def __init__(self, directory, clock=time.monotonic):
        self.path, self.clock, self.last = path(directory), clock, None

    def touch(self):
        now = self.clock()
        if self.last is not None and now - self.last < MIN_TOUCH_INTERVAL_S:
            return
        self.last = now
        try:
            with open(self.path, "a"):
                os.utime(self.path, None)
        except OSError:
            pass

    def remove(self):
        try:
            os.remove(self.path)
        except OSError:
            pass


def age_s(file_path):
    """Seconds since the file was touched, or None when it does not exist."""
    try:
        return time.time() - os.path.getmtime(file_path)
    except OSError:
        return None
