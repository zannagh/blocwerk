"""COLMAP structure-from-motion (CPU SIFT; recipe from the M4 feasibility study, 14/14 registered).

Works with COLMAP 3.9 (Ubuntu 24.04 apt) and 4.x (Homebrew): the option names that moved between
them (SiftExtraction.use_gpu/max_image_size/num_threads -> FeatureExtraction.*, SiftMatching.* ->
FeatureMatching.*; max_num_features stays SiftExtraction.* in both) are detected from the `-h` output
of the commands instead of parsing versions.

Memory: the job fits COLMAP to its memory budget (resources.py, tuning.py): extraction threads, and
a matching tier. Guided matching (the old default, about twice the sparse points of plain unguided)
holds a features x features distance matrix per matcher, 2 matchers per thread: 6.2 GB at 1 thread
and 9.7 GB at 4 on 14 photos of 8.2k-13.4k features. When that does not fit, matching runs unguided
with a looser ratio test and no cross-check, and point_triangulator then adds the two-view tracks
(16k points instead of 4.2k; about 150 MB). Note SiftExtraction.max_num_features is a soft cap in
COLMAP 4.2: 8192 still gives 8.2k-13.4k per photo.

GPU (COLMAP_USE_GPU, a CUDA build: Dockerfile.cuda): matching runs on SiftGPU/CUDA, sized to the VRAM
(tuning.gpu_matching_tiers); extraction too unless DSP / affine shape are on (covariant SIFT is CPU-only).
"""
import os
import re
import shutil
import sqlite3
import subprocess

from computejobs.child import tool_env

from .colmap_model import read_text_model
from .parsers import ExtractParser, MapParser, MatchParser, UndistortParser
from .procs import ToolRun
from .tuning import Tier

UNGUIDED = Tier(False, 0, 0)  # plain unguided matching with the configured thread cap
OPTION = re.compile(r"--([A-Za-z]+\.[A-Za-z_]+)")
PAIR_CHUNK = 250  # pairs per matches_importer run (progress granularity; memory does not grow with it)
GPU_CHUNK_FACTOR = 4


def tool_version(bin_path):
    """'4.2.0' (or None if COLMAP cannot be run)."""
    try:
        r = subprocess.run([bin_path, "-h"], capture_output=True, text=True, timeout=30, env=tool_env())
        out = r.stdout + r.stderr
    except (OSError, subprocess.SubprocessError):
        return None
    m = re.search(r"COLMAP (\d+\.\d+(?:\.\d+)?)", out)
    return m.group(1) if m else "unknown"


def help_options(bin_path, command):
    """The `--Section.option` names `colmap <command> -h` lists (empty if it cannot be run)."""
    try:
        r = subprocess.run([bin_path, command, "-h"], capture_output=True, text=True, timeout=30, env=tool_env())
        return set(OPTION.findall(r.stdout + r.stderr))
    except (OSError, subprocess.SubprocessError):
        return set()


class Colmap:
    def __init__(self, bin_path, log_path, cwd, caps=None, options=None):
        """caps: dict(max_image_size, max_features, max_matches, threads, extract_threads, max_memory_mb),
        0 = off (extract_threads 0 = threads);
        options: the known option names (default: read from the binary's -h output)."""
        self.bin, self.log, self.cwd = bin_path, log_path, cwd
        self.caps = {"max_image_size": 0, "max_features": 0, "max_matches": 0, "threads": 0, "extract_threads": 0,
                     "max_memory_mb": 0, "max_swap_growth_mb": 0, "use_gpu": False, "gpu_index": 0,
                     "dsp": True, "affine": True, **(caps or {})}
        if options is None:
            options = help_options(bin_path, "feature_extractor") | help_options(bin_path, "exhaustive_matcher")
        self.options = options
        self.v4 = "FeatureExtraction.use_gpu" in options
        self.ext_ns = "FeatureExtraction" if self.v4 else "SiftExtraction"
        self.match_ns = "FeatureMatching" if self.v4 else "SiftMatching"

    def _opt(self, *names):
        """The first of `names` this COLMAP knows (the first one if the help could not be read)."""
        return next((n for n in names if n in self.options), names[0] if not self.options else None)

    def _capped(self, args, cap, value, *names):
        """Append `--<option> value` when the cap is on and some spelling of the option exists."""
        name = self._opt(*names)
        if self.caps[cap] > 0 and name:
            args += [f"--{name}", str(value)]
        return args

    @property
    def gpu_extraction(self):
        """GPU SIFT extraction: COLMAP_USE_GPU without DSP / affine shape (COLMAP extracts those
        covariant features on the CPU only; 4.2 would silently switch, so the worker says it up front)."""
        return bool(self.caps["use_gpu"]) and not (self.caps["dsp"] or self.caps["affine"]) \
            and not self.caps.get("cpu_extraction")

    def _gpu(self, args, ns, on):
        """--<ns>.use_gpu, and --<ns>.gpu_index when on (and known)."""
        args += [f"--{ns}.use_gpu", "1" if on else "0"]
        name = self._opt(f"{ns}.gpu_index")
        if on and name:
            args += [f"--{name}", str(self.caps["gpu_index"])]
        return args

    def extract_args(self, db, image_dir, image_list, shared, params, max_features):
        feats = min(max_features, self.caps["max_features"]) if self.caps["max_features"] > 0 else max_features
        args = ["feature_extractor", "--database_path", db, "--image_path", image_dir,
                "--image_list_path", image_list, "--ImageReader.camera_model", "RADIAL"]
        self._gpu(args, self.ext_ns, self.gpu_extraction)
        args += ["--SiftExtraction.max_num_features", str(feats),
                 "--SiftExtraction.estimate_affine_shape", "1" if self.caps["affine"] else "0",
                 "--SiftExtraction.domain_size_pooling", "1" if self.caps["dsp"] else "0"]
        self._capped(args, "max_image_size", self.caps["max_image_size"],
                     f"{self.ext_ns}.max_image_size", "FeatureExtraction.max_image_size", "SiftExtraction.max_image_size")
        threads = self.caps["extract_threads"] or self.caps["threads"]
        self._capped(args, "threads", threads,
                     f"{self.ext_ns}.num_threads", "FeatureExtraction.num_threads", "SiftExtraction.num_threads")
        args += ["--ImageReader.single_camera", "1"] if shared else ["--ImageReader.single_camera_per_image", "1"]
        if params:
            args += ["--ImageReader.camera_params", ",".join(f"{p:.6f}" for p in params)]
        return args

    def _matching(self, args, tier):
        """The options every matcher run shares: CPU or GPU, the match cap, and the tier's guided matching
        and threads (on the GPU: the RANSAC verifier threads). The unguided tier also loosens the ratio
        test and drops the cross-check (more matches per pair; RANSAC verification still filters them):
        4.2k -> 7.3k points, and with triangulate() 16k."""
        threads = tier.threads or self.caps["threads"]
        self._gpu(args, self.match_ns, tier.gpu)
        args += [f"--{self.match_ns}.guided_matching", "1" if tier.guided else "0"]
        names = (f"{self.match_ns}.max_num_matches", "FeatureMatching.max_num_matches", "SiftMatching.max_num_matches")
        if tier.gpu and tier.max_matches > 0:  # SiftGPU's buffer: the features per image it can match
            args += [f"--{self._opt(*names)}", str(tier.max_matches)] if self._opt(*names) else []
        else:
            self._capped(args, "max_matches", self.caps["max_matches"], *names)
        if threads > 0:
            name = self._opt(f"{self.match_ns}.num_threads", "FeatureMatching.num_threads", "SiftMatching.num_threads")
            args += [f"--{name}", str(threads)] if name else []
        if tier.loose:
            args += ["--SiftMatching.max_ratio", "0.9", "--SiftMatching.cross_check", "0"]
        return args

    def match_args(self, db, matcher, n, tier=UNGUIDED):
        args = self._matching([f"{matcher}_matcher", "--database_path", db], tier)
        if matcher == "exhaustive":  # small blocks => a progress line every few pairs
            args += ["--ExhaustiveMatching.block_size", str(max(4, -(-n // 5)))]
        else:
            args += ["--SequentialMatching.overlap", "15", "--SequentialMatching.loop_detection", "0"]
        return args

    def pairs_args(self, db, pairs_file, tier=UNGUIDED):
        """matches_importer over an explicit pair list (frames.build_pairs): same options as the matchers."""
        return self._matching(["matches_importer", "--database_path", db, "--match_list_path", pairs_file,
                               "--match_type", "pairs"], tier)

    def vocab_tree_args(self, db, tree, query_list, num_images, tier=UNGUIDED):
        """vocab_tree_matcher: each image of `query_list` (the video frames) with its `num_images` most
        similar images (retrieval over the whole database; pairs already matched are skipped)."""
        return self._matching(["vocab_tree_matcher", "--database_path", db, "--VocabTreeMatching.vocab_tree_path",
                               tree, "--VocabTreeMatching.match_list_path", query_list,
                               "--VocabTreeMatching.num_images", str(num_images)], tier)

    def triangulate_args(self, db, image_dir, model_dir, out_dir):
        """point_triangulator on the mapper's model (same database, poses kept, points kept) that also
        keeps two-view tracks, which the mapper drops."""
        args = ["point_triangulator", "--database_path", db, "--image_path", image_dir, "--input_path", model_dir,
                "--output_path", out_dir, "--clear_points", "0", "--Mapper.tri_ignore_two_view_tracks", "0",
                "--Mapper.ba_refine_principal_point", "0"]
        if self.caps["threads"] > 0:
            args += ["--Mapper.num_threads", str(self.caps["threads"])]
        return args

    def map_args(self, db, image_dir, out_dir, mapper="incremental"):
        """mapper (incremental) or global_mapper (COLMAP 4.x: GLOMAP's rotation averaging + global
        positioning, then bundle adjustment); both write <out_dir>/<n>/."""
        if mapper == "global":
            args = ["global_mapper", "--database_path", db, "--image_path", image_dir, "--output_path", out_dir,
                    "--GlobalMapper.ba_refine_principal_point", "0"]
            if self.caps["threads"] > 0:
                args += ["--GlobalMapper.num_threads", str(self.caps["threads"])]
            return args
        args = ["mapper", "--database_path", db, "--image_path", image_dir, "--output_path", out_dir,
                "--Mapper.ba_refine_principal_point", "0"]
        if self.caps["threads"] > 0:  # same name in 3.9 and 4.x; mapper -h is not read
            args += ["--Mapper.num_threads", str(self.caps["threads"])]
        return args

    def _run(self, stage, args, parser=None, report=None, gpu=False):
        """gpu: a CUDA run gets no RLIMIT_AS (the driver reserves huge virtual ranges; cuInit would
        fail under it); the footprint watchdog still guards host memory."""
        def on_line(line):
            if parser and report:
                r = parser(line)
                if r:
                    report(*r)
        return ToolRun(stage, [self.bin, *args], self.cwd, self.log, on_line, name="COLMAP",
                       mem_limit_mb=self.caps["max_memory_mb"], limit_address_space=not gpu,
                       swap_limit_mb=self.caps["max_swap_growth_mb"]).run()

    def extract(self, db, image_dir, groups, report, max_features):
        """groups: list of (image names relative to image_dir, camera_params or None, shared camera?)."""
        total, done = sum(len(g[0]) for g in groups), 0
        for i, (names, params, shared) in enumerate(groups):
            lst = os.path.join(self.cwd, f"images-{i}.txt")
            with open(lst, "w") as fh:
                fh.write("\n".join(names) + "\n")
            args = self.extract_args(db, image_dir, lst, shared, params, max_features)
            self._run("sfm-features", args, ExtractParser(total, done), report, gpu=self.gpu_extraction)
            done += len(names)

    def match(self, db, matcher, n, report, tier=UNGUIDED):
        self._run("sfm-matching", self.match_args(db, matcher, n, tier), MatchParser(), report, gpu=tier.gpu)

    def match_pairs(self, db, pairs, report, chunk=PAIR_CHUNK, tier=UNGUIDED):
        """Match an explicit pair list in chunks: one matches_importer run per chunk (each under the
        memory guard), which also gives a progress line per chunk ("pairs 250/2680"). GPU chunks are
        GPU_CHUNK_FACTOR x bigger: each run sets CUDA and SiftGPU's buffers up anew."""
        chunk = chunk * GPU_CHUNK_FACTOR if tier.gpu else chunk
        for start in range(0, len(pairs), chunk):
            part = pairs[start:start + chunk]
            path = os.path.join(self.cwd, "pairs.txt")
            with open(path, "w") as fh:
                fh.write("".join(f"{a} {b}\n" for a, b in part))
            self._run("sfm-matching", self.pairs_args(db, path, tier), gpu=tier.gpu)
            report((start + len(part)) / len(pairs), f"pairs {start + len(part)}/{len(pairs)}")

    def match_vocab(self, db, query_names, num_images, tree, tier=UNGUIDED):
        """Loop closure for the video frames: vocab_tree_matcher over `query_names`."""
        path = os.path.join(self.cwd, "vocab-queries.txt")
        with open(path, "w") as fh:
            fh.write("\n".join(query_names) + "\n")
        self._run("sfm-matching", self.vocab_tree_args(db, tree, path, num_images, tier), gpu=tier.gpu)

    def map(self, db, image_dir, out_dir, n, report, mapper="incremental"):
        os.makedirs(out_dir, exist_ok=True)
        self._run("sfm-mapping", self.map_args(db, image_dir, out_dir, mapper), MapParser(n), report)

    def triangulate(self, db, image_dir, model_dir, out_dir):
        os.makedirs(out_dir, exist_ok=True)
        self._run("sfm-mapping", self.triangulate_args(db, image_dir, model_dir, out_dir))

    def feature_counts(self, db):
        """SIFT features per image in the database (after extraction)."""
        con = sqlite3.connect(db)
        try:
            return [n for (n,) in con.execute("SELECT rows FROM keypoints")]
        finally:
            con.close()

    def to_text(self, model_dir, out_dir):
        os.makedirs(out_dir, exist_ok=True)
        self._run("sfm-mapping", ["model_converter", "--input_path", model_dir, "--output_path", out_dir,
                                  "--output_type", "TXT"])
        return read_text_model(out_dir)

    def best_model(self, sparse_dir):
        """The reconstruction with the most registered images: (model dir, parsed text model) or None."""
        best = None
        for d in sorted(os.listdir(sparse_dir)) if os.path.isdir(sparse_dir) else []:
            path = os.path.join(sparse_dir, d)
            if not os.path.isdir(path) or not d.isdigit():
                continue
            model = self.to_text(path, os.path.join(self.cwd, "sparse-txt", d))
            if best is None or len(model["images"]) > len(best[1]["images"]):
                best = (path, model)
        return best

    def undistort(self, image_dir, model_dir, out_dir, max_size, report):
        self._run("undistort", ["image_undistorter", "--image_path", image_dir, "--input_path", model_dir,
                                "--output_path", out_dir, "--output_type", "COLMAP",
                                "--max_image_size", str(max_size)], UndistortParser(), report)
        sparse, target = os.path.join(out_dir, "sparse"), os.path.join(out_dir, "sparse", "0")
        os.makedirs(target, exist_ok=True)
        for f in os.listdir(sparse):  # Brush expects <dataset>/sparse/0/*.bin
            if os.path.isfile(os.path.join(sparse, f)):
                shutil.move(os.path.join(sparse, f), os.path.join(target, f))
