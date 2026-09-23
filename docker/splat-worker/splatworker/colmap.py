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
"""
import os
import re
import shutil
import sqlite3
import subprocess

import numpy as np
from computejobs.child import tool_env

from .parsers import ExtractParser, MapParser, MatchParser, UndistortParser
from .procs import ToolRun
from .tuning import Tier

UNGUIDED = Tier(False, 0, 0)  # plain unguided matching with the configured thread cap
OPTION = re.compile(r"--([A-Za-z]+\.[A-Za-z_]+)")
PAIR_CHUNK = 250  # pairs per matches_importer run (progress granularity; memory does not grow with it)


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
                     "max_memory_mb": 0, "max_swap_growth_mb": 0, **(caps or {})}
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

    def extract_args(self, db, image_dir, image_list, shared, params, max_features):
        feats = min(max_features, self.caps["max_features"]) if self.caps["max_features"] > 0 else max_features
        args = ["feature_extractor", "--database_path", db, "--image_path", image_dir,
                "--image_list_path", image_list, "--ImageReader.camera_model", "RADIAL",
                f"--{self.ext_ns}.use_gpu", "0",
                "--SiftExtraction.max_num_features", str(feats),
                "--SiftExtraction.estimate_affine_shape", "1", "--SiftExtraction.domain_size_pooling", "1"]
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
        """The options every matcher run shares: CPU, the match cap, and the tier's guided matching and
        threads. The unguided tier also loosens the ratio test and drops the cross-check (more
        matches per pair; RANSAC verification still filters them): 4.2k -> 7.3k points, and with
        triangulate() 16k."""
        threads = tier.threads or self.caps["threads"]
        args += [f"--{self.match_ns}.use_gpu", "0", f"--{self.match_ns}.guided_matching", "1" if tier.guided else "0"]
        self._capped(args, "max_matches", self.caps["max_matches"],
                     f"{self.match_ns}.max_num_matches", "FeatureMatching.max_num_matches", "SiftMatching.max_num_matches")
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

    def triangulate_args(self, db, image_dir, model_dir, out_dir):
        """point_triangulator on the mapper's model (same database, poses kept, points kept) that also
        keeps two-view tracks, which the mapper drops."""
        args = ["point_triangulator", "--database_path", db, "--image_path", image_dir, "--input_path", model_dir,
                "--output_path", out_dir, "--clear_points", "0", "--Mapper.tri_ignore_two_view_tracks", "0",
                "--Mapper.ba_refine_principal_point", "0"]
        if self.caps["threads"] > 0:
            args += ["--Mapper.num_threads", str(self.caps["threads"])]
        return args

    def map_args(self, db, image_dir, out_dir):
        args = ["mapper", "--database_path", db, "--image_path", image_dir, "--output_path", out_dir,
                "--Mapper.ba_refine_principal_point", "0"]
        if self.caps["threads"] > 0:  # same name in 3.9 and 4.x; mapper -h is not read
            args += ["--Mapper.num_threads", str(self.caps["threads"])]
        return args

    def _run(self, stage, args, parser=None, report=None):
        def on_line(line):
            if parser and report:
                r = parser(line)
                if r:
                    report(*r)
        return ToolRun(stage, [self.bin, *args], self.cwd, self.log, on_line, name="COLMAP",
                       mem_limit_mb=self.caps["max_memory_mb"], limit_address_space=True,
                       swap_limit_mb=self.caps["max_swap_growth_mb"]).run()

    def extract(self, db, image_dir, groups, report, max_features):
        """groups: list of (image names relative to image_dir, camera_params or None, shared camera?)."""
        total, done = sum(len(g[0]) for g in groups), 0
        for i, (names, params, shared) in enumerate(groups):
            lst = os.path.join(self.cwd, f"images-{i}.txt")
            with open(lst, "w") as fh:
                fh.write("\n".join(names) + "\n")
            args = self.extract_args(db, image_dir, lst, shared, params, max_features)
            self._run("sfm-features", args, ExtractParser(total, done), report)
            done += len(names)

    def match(self, db, matcher, n, report, tier=UNGUIDED):
        self._run("sfm-matching", self.match_args(db, matcher, n, tier), MatchParser(), report)

    def match_pairs(self, db, pairs, report, chunk=PAIR_CHUNK, tier=UNGUIDED):
        """Match an explicit pair list in chunks: one matches_importer run per chunk (each under the
        memory guard), which also gives a progress line per chunk ("pairs 250/2680")."""
        for start in range(0, len(pairs), chunk):
            part = pairs[start:start + chunk]
            path = os.path.join(self.cwd, "pairs.txt")
            with open(path, "w") as fh:
                fh.write("".join(f"{a} {b}\n" for a, b in part))
            self._run("sfm-matching", self.pairs_args(db, path, tier))
            report((start + len(part)) / len(pairs), f"pairs {start + len(part)}/{len(pairs)}")

    def map(self, db, image_dir, out_dir, n, report):
        os.makedirs(out_dir, exist_ok=True)
        self._run("sfm-mapping", self.map_args(db, image_dir, out_dir), MapParser(n), report)

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


def qvec_to_R(q):
    w, x, y, z = q
    return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y)],
                     [2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x)],
                     [2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y)]])


def read_text_model(txt_dir):
    """{'images': {name: camera centre (3,)}, 'points': n, 'meanReprojErrorPx': float|None}."""
    images = {}
    with open(os.path.join(txt_dir, "images.txt")) as fh:
        lines = [ln for ln in fh.read().split("\n") if not ln.startswith("#")]
    for ln in lines[0::2]:
        p = ln.split()
        if len(p) < 10:
            continue
        R, t = qvec_to_R([float(v) for v in p[1:5]]), np.array([float(v) for v in p[5:8]])
        images[p[9]] = -R.T @ t
    n_points, err_sum = 0, 0.0
    with open(os.path.join(txt_dir, "points3D.txt")) as fh:
        for ln in fh:
            if ln.startswith("#") or not ln.strip():
                continue
            n_points += 1
            err_sum += float(ln.split()[7])
    return {"images": images, "points": n_points,
            "meanReprojErrorPx": round(err_sum / n_points, 3) if n_points else None}
