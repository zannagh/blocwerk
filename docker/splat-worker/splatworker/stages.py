"""Stage bookkeeping shared by the job bodies (all-in-one, prepare, finish): progress bands, timings and
the tools.log notes."""
import os
import time


class Stages:
    """bands {stage: (lo, hi)} of the overall progress; progress(fraction, stage, detail=None)."""

    def __init__(self, job_dir, progress, bands):
        self.dir, self.progress, self.bands = job_dir, progress, bands
        self.timings, self.stage, self.t0 = {}, None, None
        self.log = os.path.join(job_dir, "tools.log")
        self.suffix = None  # appended to every stage detail once known ("87/120 video frames registered")
        self.note = None  # appended to the details of the current stage ("memory budget 5.2 GB (...)")

    def begin(self, stage):
        self.end()
        self.stage, self.t0, self.note = stage, time.time(), None
        self.progress(self.bands[stage][0], stage)

    def end(self):
        if self.stage:
            self.timings[self.stage] = round(time.time() - self.t0, 1)
            self.stage = None

    def report(self, fraction, detail=None):
        lo, hi = self.bands[self.stage]
        detail = "; ".join(p for p in (detail, self.note, self.suffix) if p) or None
        self.progress(lo + (hi - lo) * min(1.0, max(0.0, fraction)), self.stage, detail)

    def _log_line(self, text):
        with open(self.log, "a") as fh:
            fh.write(f"# {text}\n")
