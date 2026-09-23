"""The COLMAP part of a job, fitted to the machine: memory budget -> extraction threads + matching tier,
and one step down on a memory-guard kill instead of failing the job.

sfm-features: extraction threads from the budget (tuning.extraction_threads); killed -> once more
with half the threads (COLMAP skips the images it already has features for).
sfm-matching: the tiers from tuning.matching_tiers (guided at N threads, guided at 1, unguided +
triangulate); killed -> the next tier (the matchers skip the pairs already in the database).
sfm-mapping: mapper, then point_triangulator when the tier asks for it.
"""
import os

from .colmap import Colmap
from .procs import MemoryLimitError
from .resources import memory_budget, system_memory
from .settings import settings
from .tuning import extraction_threads, matching_tiers


def max_threads():
    return settings.colmap_threads if settings.colmap_threads > 0 else (os.cpu_count() or 4)


class Sfm:
    def __init__(self, run, info=None):
        """run: the pipeline.Run (stages, progress, log); info: system_memory() (read now if None)."""
        self.run = run
        self.info = info if info is not None else system_memory()
        self.budget_mb, self.budget_why = memory_budget(self.info, settings.max_memory_mb)
        self.threads = max_threads()
        self.tier, self.retries, self.brush_budget_mb = None, [], None
        self.cm = Colmap(settings.colmap_bin, run.log, run.dir, caps={
            "max_image_size": settings.colmap_max_image_size, "max_features": settings.colmap_max_features,
            "max_matches": settings.colmap_max_matches, "threads": settings.colmap_threads,
            "extract_threads": extraction_threads(self.budget_mb, self.threads),
            "max_memory_mb": self.budget_mb, "max_swap_growth_mb": settings.max_swap_growth_mb})

    def budget_note(self):
        return f"memory budget {self.budget_mb / 1024:.1f} GB ({self.budget_why})"

    def _log(self, text):
        with open(self.run.log, "a") as fh:
            fh.write(f"# {text}\n")

    def extract(self, db, img_dir, batches, max_features):
        r = self.run
        r.begin("sfm-features")
        r.note = f"{self.budget_note()}; {self.cm.caps['extract_threads']} threads"
        self._log(r.note)
        r.report(0.0)
        try:
            self.cm.extract(db, img_dir, batches, r.report, max_features)
        except MemoryLimitError as e:
            threads = self.cm.caps["extract_threads"]
            if threads <= 1:
                raise
            self.cm.caps["extract_threads"] = max(1, threads // 2)
            self.retries.append({"stage": "sfm-features", "reason": e.kind, "threads": self.cm.caps["extract_threads"]})
            r.note = f"{self.budget_note()}; retrying with {self.cm.caps['extract_threads']} threads ({e.kind})"
            self._log(f"{e.message} -> {r.note}")
            self.cm.extract(db, img_dir, batches, r.report, max_features)

    def match(self, db, matcher, n, pairs=None):
        """pairs: the explicit pair list for matcher == "pairs"."""
        r = self.run
        r.begin("sfm-matching")
        tiers = matching_tiers(self.budget_mb, self.cm.feature_counts(db), self.threads)
        for i, tier in enumerate(tiers):
            self.tier = tier
            r.note = f"{tier.name} (est. {tier.estimate_mb / 1024:.1f} GB of {self.budget_mb / 1024:.1f} GB)"
            self._log(f"matching tier: {r.note}")
            r.report(0.0)
            try:
                if matcher == "pairs":
                    self.cm.match_pairs(db, pairs, r.report, tier=tier)
                else:
                    self.cm.match(db, matcher, n, r.report, tier=tier)
                return tier
            except MemoryLimitError as e:
                if i == len(tiers) - 1:
                    raise
                self.retries.append({"stage": "sfm-matching", "reason": e.kind, "from": tier.name,
                                     "to": tiers[i + 1].name})
                self._log(f"{e.message} -> retrying as {tiers[i + 1].name}")

    def map(self, db, img_dir, n):
        """Mapper (+ point_triangulator for the loose tier): (model dir, parsed text model) or None."""
        r = self.run
        r.begin("sfm-mapping")
        sparse = os.path.join(r.dir, "sparse")
        self.cm.map(db, img_dir, sparse, n, r.report)
        best = self.cm.best_model(sparse)
        if best and self.tier is not None and self.tier.loose:
            out = os.path.join(r.dir, "sparse-tri")
            self.cm.triangulate(db, img_dir, best[0], out)
            best = (out, self.cm.to_text(out, os.path.join(r.dir, "sparse-txt", "tri")))
        return best

    def train_budget_mb(self):
        """Brush's ceiling: the budget read again now (COLMAP is done, the machine may have changed), with
        SPLAT_MIN_MEMORY_MB as its floor. COLMAP never gets that floor: its tiers trade speed, and a
        raised budget picks guided matching on 1 thread, ~10x slower than unguided on 170 images."""
        self.brush_budget_mb, _ = memory_budget(system_memory(), settings.max_memory_mb, settings.min_memory_mb)
        return self.brush_budget_mb

    def stats(self):
        return {"memoryBudgetMb": self.budget_mb, "matchingTier": self.tier.name if self.tier else None,
                "extractionThreads": self.cm.caps["extract_threads"], "memoryRetries": self.retries,
                "trainMemoryBudgetMb": self.brush_budget_mb}
