"""The COLMAP part of a job, fitted to the machine: memory budget -> extraction threads + matching tier,
and one step down on a memory-guard kill instead of failing the job.

sfm-features: extraction threads from the budget (tuning.extraction_threads); killed -> once more
with half the threads (COLMAP skips the images it already has features for). On the GPU
(COLMAP_USE_GPU, no DSP / affine shape): a failed GPU run is repeated on the CPU.
sfm-matching: the tiers from tuning.matching_tiers (guided at N threads, guided at 1, unguided +
triangulate), or with COLMAP_USE_GPU from tuning.gpu_matching_tiers (fitted to the free VRAM); killed
(or a GPU run failed) -> the next tier (the matchers skip the pairs already in the database). Then,
with COLMAP_VOCAB_TREE_IMAGES, the video frames' vocabulary-tree loop closure.
sfm-mapping: mapper (or global_mapper: COLMAP_MAPPER), then point_triangulator when the tier asks for it.
"""
import os

from computejobs.child import JobError

from . import gpu
from .colmap import Colmap, help_options
from .procs import MemoryLimitError
from .resources import memory_budget, system_memory
from .settings import settings
from .tuning import extraction_threads, gpu_matching_tiers, guided_seconds, matching_tiers

MAPPERS = ("incremental", "global")


def max_threads():
    return settings.colmap_threads if settings.colmap_threads > 0 else (os.cpu_count() or 4)


class Sfm:
    def __init__(self, run, info=None, gpu_info=None):
        """run: the pipeline.Run (stages, progress, log); info: system_memory() (read now if None);
        gpu_info: gpu.vram() (read now when COLMAP_USE_GPU and None)."""
        self.run = run
        self.info = info if info is not None else system_memory()
        self.budget_mb, self.budget_why = memory_budget(self.info, settings.max_memory_mb)
        self.threads = max_threads()
        self.tier, self.retries, self.brush_budget_mb, self.vocab = None, [], None, None
        self.gpu = None  # the GPU COLMAP uses ({"name", "totalMb", "freeMb"}), None = CPU
        if settings.colmap_use_gpu:
            self.gpu = gpu_info if gpu_info is not None else gpu.vram()
            if not self.gpu:
                self._log("COLMAP_USE_GPU=1 but no NVIDIA GPU is visible (nvidia-smi): COLMAP runs on the CPU")
        self.mapper = settings.colmap_mapper if settings.colmap_mapper in MAPPERS else "incremental"
        self.cm = Colmap(settings.colmap_bin, run.log, run.dir, caps={
            "max_image_size": settings.colmap_max_image_size, "max_features": settings.colmap_max_features,
            "max_matches": settings.colmap_max_matches, "threads": settings.colmap_threads,
            "extract_threads": extraction_threads(self.budget_mb, self.threads),
            "max_memory_mb": self.budget_mb, "max_swap_growth_mb": settings.max_swap_growth_mb,
            "use_gpu": bool(self.gpu), "gpu_index": settings.colmap_gpu_index,
            "dsp": settings.colmap_dsp_sift, "affine": settings.colmap_affine_shape})
        if self.mapper == "global" and not help_options(settings.colmap_bin, "global_mapper"):
            self._log("COLMAP_MAPPER=global needs COLMAP 4.x (global_mapper): using the incremental mapper")
            self.mapper = "incremental"

    def budget_note(self):
        return f"memory budget {self.budget_mb / 1024:.1f} GB ({self.budget_why})"

    def _log(self, text):
        with open(self.run.log, "a") as fh:
            fh.write(f"# {text}\n")

    def extract(self, db, img_dir, batches, max_features):
        r = self.run
        r.begin("sfm-features")
        where = f"GPU ({self.gpu['name']})" if self.cm.gpu_extraction else f"{self.cm.caps['extract_threads']} threads"
        r.note = f"{self.budget_note()}; {where}"
        if self.gpu and not self.cm.gpu_extraction:
            self._log("DSP / affine-shape SIFT is CPU-only in COLMAP: extracting on the CPU, matching on the GPU")
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
        except JobError as e:
            if not self.cm.gpu_extraction:
                raise
            self.cm.caps["cpu_extraction"] = True
            self.retries.append({"stage": "sfm-features", "reason": "gpu", "to": "cpu"})
            self._log(f"GPU feature extraction failed ({e.message}) -> retrying on the CPU")
            self.cm.extract(db, img_dir, batches, r.report, max_features)

    def tiers(self, db, pairs=0):
        """pairs: how many image pairs will be matched (the CPU tiers' time guard)."""
        counts = self.cm.feature_counts(db)
        if self.gpu:
            return gpu_matching_tiers(self.gpu.get("freeMb") or 0, counts, settings.colmap_max_matches, self.threads)
        tiers = matching_tiers(self.budget_mb, counts, self.threads, pairs)
        if not tiers[0].guided and matching_tiers(self.budget_mb, counts, self.threads)[0].guided:
            self._log(f"guided matching skipped: ~{guided_seconds(counts, self.threads, pairs) // 60} min for "
                      f"{pairs} pairs on the CPU; unguided + triangulate instead")
        return tiers

    def _tier_note(self, tier):
        if tier.gpu:
            return f"{tier.name} (est. {tier.vram_mb / 1024:.1f} GB of {self.gpu['freeMb'] / 1024:.1f} GB free VRAM)"
        return f"{tier.name} (est. {tier.estimate_mb / 1024:.1f} GB of {self.budget_mb / 1024:.1f} GB)"

    def match(self, db, matcher, n, pairs=None, vocab_queries=None):
        """pairs: the explicit pair list for matcher == "pairs"; vocab_queries: the video frames for
        the vocabulary-tree loop closure (COLMAP_VOCAB_TREE_IMAGES)."""
        r = self.run
        r.begin("sfm-matching")
        n_pairs = len(pairs) if pairs is not None else (n * (n - 1) // 2 if matcher == "exhaustive" else n * 10)
        tiers = self.tiers(db, n_pairs)
        for i, tier in enumerate(tiers):
            self.tier = tier
            r.note = self._tier_note(tier)
            self._log(f"matching tier: {r.note}")
            r.report(0.0)
            try:
                if matcher == "pairs":
                    self.cm.match_pairs(db, pairs, r.report, tier=tier)
                else:
                    self.cm.match(db, matcher, n, r.report, tier=tier)
                break
            except JobError as e:  # MemoryLimitError, or (GPU tiers only) any COLMAP failure
                last = i == len(tiers) - 1
                if last or not (isinstance(e, MemoryLimitError) or tier.gpu):
                    raise
                self.retries.append({"stage": "sfm-matching", "reason": getattr(e, "kind", "gpu"),
                                     "from": tier.name, "to": tiers[i + 1].name})
                self._log(f"{e.message} -> retrying as {tiers[i + 1].name}")
        if vocab_queries:
            self.vocab_match(db, vocab_queries)
        return self.tier

    def vocab_match(self, db, queries):
        """COLMAP_VOCAB_TREE_IMAGES > 0: vocab_tree_matcher over the frames (optional: a failure is logged)."""
        k, tree = settings.colmap_vocab_tree_images, settings.colmap_vocab_tree_path
        if k <= 0:
            return
        if not tree or not os.path.isfile(tree):
            self.vocab = f"off: no vocabulary tree at COLMAP_VOCAB_TREE_PATH={tree!r}"
            self._log(f"vocabulary-tree matching {self.vocab}")
            return
        self.run.report(1.0, f"vocabulary-tree loop closure ({len(queries)} frames x {k})")
        try:
            self.cm.match_vocab(db, queries, k, tree, self.tier)
            self.vocab = f"{len(queries)} frames x {k} images"
        except JobError as e:
            self.vocab = f"failed: {e.message}"
            self._log(f"vocabulary-tree matching failed ({e.message}); continuing with the pair list's matches")

    def map(self, db, img_dir, n):
        """Mapper (+ point_triangulator for the loose tier): (model dir, parsed text model) or None."""
        r = self.run
        r.begin("sfm-mapping")
        sparse = os.path.join(r.dir, "sparse")
        self.cm.map(db, img_dir, sparse, n, r.report, self.mapper)
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
                "trainMemoryBudgetMb": self.brush_budget_mb, "colmapGpu": (self.gpu or {}).get("name"),
                "colmapGpuExtraction": self.cm.gpu_extraction, "colmapMapper": self.mapper,
                "siftDsp": self.cm.caps["dsp"], "siftAffineShape": self.cm.caps["affine"],
                "vocabTreeMatching": self.vocab}
