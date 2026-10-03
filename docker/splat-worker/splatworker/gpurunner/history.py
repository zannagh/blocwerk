"""The jobs this runner took on: one JSON line per finished job in <state dir>/jobs.jsonl, the newest MAX_JOBS kept;
and the running job's live record (stage times, progress, ETA) for the local status page (web.py). Stdlib only."""
import json
import logging
import os
import threading
import time

log = logging.getLogger("gpurunner")
MAX_JOBS = 200
MIN_ETA_FRACTION = 0.01  # no ETA before 1 % of training has passed in this process
MIN_ETA_SECONDS = 10.0


class JobHistory:
    def __init__(self, directory, cap=MAX_JOBS):
        self.path, self.cap, self.lock = os.path.join(directory, "jobs.jsonl"), cap, threading.Lock()

    def _read(self):
        try:
            with open(self.path) as fh:
                lines = fh.read().splitlines()
        except OSError:
            return []
        out = []
        for line in lines:
            try:
                doc = json.loads(line)
            except ValueError:
                continue  # a torn last line after a crash
            if isinstance(doc, dict):
                out.append(doc)
        return out

    def append(self, record):
        """Adds a finished job; keeps the newest `cap` (rewritten atomically). Never raises."""
        with self.lock:
            try:
                docs = (self._read() + [record])[-self.cap:]
                os.makedirs(os.path.dirname(self.path), exist_ok=True)
                tmp = self.path + ".tmp"
                with open(tmp, "w") as fh:
                    fh.writelines(json.dumps(d, separators=(",", ":")) + "\n" for d in docs)
                os.replace(tmp, self.path)
            except (OSError, TypeError, ValueError) as e:
                log.warning("could not record the job in %s (%s)", self.path, e)

    def recent(self, n=None):
        """Newest first."""
        with self.lock:
            docs = self._read()
        docs.reverse()
        return docs if n is None else docs[:n]


class JobStatus:
    """The running job as the status page shows it; update() is the heartbeat's observer (every progress change)."""

    def __init__(self, job, server, clock=time.time):
        self.clock, self.lock = clock, threading.Lock()
        now = clock()
        self.rec = {"jobId": str(job.get("jobId")), "server": server, "wallId": job.get("wallId"),
                    "captureId": job.get("captureId"), "quality": job.get("quality"),
                    "reattached": bool(job.get("reattached")), "startedAt": now, "finishedAt": None,
                    "durationS": None, "stages": {}, "outcome": None, "previewsUploaded": None, "error": None}
        self.progress = {}
        self.stage, self.stage_at, self.eta_base = None, now, None

    def _enter(self, stage, now):
        if self.stage is not None:
            stages = self.rec["stages"]
            stages[self.stage] = round(stages.get(self.stage, 0.0) + now - self.stage_at, 1)
        self.stage, self.stage_at, self.eta_base = stage, now, None

    def update(self, state):
        with self.lock:
            now = self.clock()
            if state.get("stage") != self.stage:
                self._enter(state.get("stage"), now)
            self.progress = dict(state)
            fraction = state.get("fraction") or 0.0
            if self.stage == "train" and self.eta_base is None and fraction > 0:
                self.eta_base = (now, fraction)  # a resumed job starts high: measure from here

    def eta_s(self):
        """Seconds of training left, from the rate since the first progress of this stage; None when unknown."""
        if self.stage != "train" or self.eta_base is None:
            return None
        t0, f0 = self.eta_base
        f, dt = self.progress.get("fraction") or 0.0, self.clock() - t0
        if f - f0 < MIN_ETA_FRACTION or dt < MIN_ETA_SECONDS:
            return None
        return round((1.0 - f) * dt / (f - f0))

    def finish(self, outcome, error=None, previews=None):
        with self.lock:
            now = self.clock()
            self._enter(None, now)
            self.rec.update(finishedAt=now, durationS=round(now - self.rec["startedAt"], 1), outcome=outcome,
                            error=(str(error)[:500] if error else None), previewsUploaded=previews)
            return dict(self.rec)

    def snapshot(self):
        with self.lock:
            stages = dict(self.rec["stages"])
            if self.stage is not None:
                stages[self.stage] = round(stages.get(self.stage, 0.0) + self.clock() - self.stage_at, 1)
            return {**self.rec, "stages": stages, "stage": self.stage, "progress": dict(self.progress),
                    "etaS": self.eta_s(), "elapsedS": round(self.clock() - self.rec["startedAt"], 1)}
