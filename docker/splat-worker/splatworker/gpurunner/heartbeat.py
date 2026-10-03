"""Progress + lease heartbeat of the running job, on its own thread (training never waits on the network).

Posts the latest progress at most every MIN_INTERVAL_S when it changed, and at least every MAX_INTERVAL_S
regardless (the server's 5-minute lease). A `cancel` answer, a 404 / 410 (the job is no longer ours: cancelled,
requeued, the runner may no longer train that wall) or a 401 sets `stop`, which kills the trainer
(procs.ToolRun's stop event). It also keeps the liveness file fresh while a long job holds the main loop."""
import logging
import threading
import time

from .client import Gone, Rejected, Transient, Unauthorized

log = logging.getLogger("gpurunner")
MIN_INTERVAL_S, MAX_INTERVAL_S = 3.0, 20.0
TICK_S = 1.0


class Heartbeat:
    def __init__(self, client, job_id, stop, alive=None, clock=time.monotonic):
        self.client, self.job_id, self.stop, self.alive, self.clock = client, job_id, stop, alive, clock
        self.state = {"fraction": 0.0, "step": None, "totalSteps": None, "stage": "download", "detail": None}
        self.changed, self.sent_at, self.lock = True, None, threading.Lock()
        self.cancelled = self.gone = self.revoked = False
        self.last_ok = clock()
        self.done = threading.Event()
        self.thread = threading.Thread(target=self._loop, name="heartbeat", daemon=True)

    def start(self):
        self.thread.start()
        return self

    def set(self, **kw):
        with self.lock:
            if any(self.state.get(k) != v for k, v in kw.items()):
                self.state.update(kw)
                self.changed = True

    def close(self):
        self.done.set()
        if self.thread.is_alive():
            self.thread.join(timeout=5)

    def _due(self):
        if self.sent_at is None:
            return True
        age = self.clock() - self.sent_at
        return age >= MAX_INTERVAL_S or (self.changed and age >= MIN_INTERVAL_S)

    def beat(self):
        """One post if due; returns False once the job is no longer ours."""
        if not self._due():
            return True
        with self.lock:
            doc, self.changed = dict(self.state), False
        self.sent_at = self.clock()
        try:
            answer = self.client.progress(self.job_id, doc)
            self.last_ok = self.clock()
        except (Gone, Unauthorized) as e:
            self.gone, self.revoked = True, isinstance(e, Unauthorized)
            log.warning("job %s is no longer ours (%s): stopping it", self.job_id, e)
            self.stop.set()
            return False
        except (Transient, Rejected) as e:
            log.info("progress not delivered (%s); retrying", e)
            return True
        if answer.get("cancel"):
            self.cancelled = True
            log.warning("job %s was cancelled on the server: stopping it", self.job_id)
            self.stop.set()
            return False
        return True

    def _loop(self):
        while not self.done.is_set() and not self.stop.is_set():
            if self.alive is not None:
                self.alive.touch()
            if not self.beat():
                return
            self.done.wait(TICK_S)
