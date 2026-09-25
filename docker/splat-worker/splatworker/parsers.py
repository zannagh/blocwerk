"""Progress parsers for tool output (COLMAP 3.9 .. 4.x glog lines, Brush 0.3 TTY progress, gsplat_train.py).

Each parser is fed one cleaned line at a time and returns a fraction in [0, 1] (plus a short detail
text) when the line tells us something, else None. Samples: tests/fixtures/*.log.
"""
import json
import re

EXTRACT = re.compile(r"Processed file \[(\d+)/(\d+)\]")
BLOCK = re.compile(r"(?:Matching|Processing) block \[(\d+)/(\d+), (\d+)/(\d+)\]")
SEQ_IMAGE = re.compile(r"(?:Matching|Processing) image \[(\d+)/(\d+)\]")
REGISTER = re.compile(r"Registering image #\d+ \((?:num_reg_frames=)?(\d+)\)")
INITIAL_PAIR = re.compile(r"Registering initial image pair")
UNDISTORT = re.compile(r"Undistorting image \[(\d+)/(\d+)\]")
BRUSH_STEPS = re.compile(r"(\d+)/(\d+)\s+Steps")
BRUSH_SPLATS = re.compile(r"Current splat count (\d+)")
BRUSH_TOOK = re.compile(r"Training took (\S+)")
BRUSH_DATASET = re.compile(r"Loading dataset with (\d+) training")
GSPLAT_STEP = re.compile(r"^step (\d+)/(\d+) splats (\d+)")
GSPLAT_LOADED = re.compile(r"^loaded (\d+)/(\d+) images")
GSPLAT_TOOK = re.compile(r"^Training took (\S+)")
GSPLAT_DONE = re.compile(r"^wrote (\d+) splats; peak VRAM (\d+) MB")
GSPLAT_OOM = re.compile(r"^GSPLAT_OOM (.*)")
GSPLAT_EVAL = re.compile(r"^eval psnr ([0-9.]+) ssim ([0-9.]+) views (\d+)")
GSPLAT_EVAL_WALL = re.compile(r"^eval wall psnr ([0-9.]+) ssim ([0-9.]+) views (\d+)")
GSPLAT_ZONES = re.compile(r"^zones (\{.*\})")


class ExtractParser:
    """Feature extraction may run once per camera group: `offset` images were done before this run."""

    def __init__(self, total, offset=0):
        self.total, self.offset = max(1, total), offset

    def __call__(self, line):
        m = EXTRACT.search(line)
        if not m:
            return None
        done = self.offset + int(m.group(1))
        return min(1.0, done / self.total), f"{done}/{self.total} photos"


class MatchParser:
    def __call__(self, line):
        m = BLOCK.search(line)
        if m:
            i, n, j, k = map(int, m.groups())
            done, total = (i - 1) * k + j, n * k
            return (done - 1) / total, f"block {done}/{total}"  # a block is started, not finished
        m = SEQ_IMAGE.search(line)
        if m:
            i, n = map(int, m.groups())
            return (i - 1) / max(1, n), f"image {i}/{n}"
        return None


class MapParser:
    def __init__(self, total):
        self.total = max(1, total)

    def __call__(self, line):
        m = REGISTER.search(line)
        if m:
            n = int(m.group(1))
            return min(1.0, n / self.total), f"{n}/{self.total} images registered"
        if INITIAL_PAIR.search(line):
            return 2 / self.total, f"2/{self.total} images registered"
        return None


class UndistortParser:
    def __call__(self, line):
        m = UNDISTORT.search(line)
        if m:
            i, n = map(int, m.groups())
            return i / max(1, n), f"{i}/{n} images"
        return None


class BrushParser:
    """Steps -> fraction; also remembers the last splat count and Brush's own training time."""

    def __init__(self):
        self.step, self.total, self.splats, self.took, self.views = 0, None, None, None, None

    def __call__(self, line):
        m = BRUSH_SPLATS.search(line)
        if m:
            self.splats = int(m.group(1))
        m = BRUSH_TOOK.search(line)
        if m:
            self.took = m.group(1)
        m = BRUSH_DATASET.search(line)
        if m:
            self.views = int(m.group(1))
        m = BRUSH_STEPS.search(line)
        if not m:
            return None
        step, total = int(m.group(1)), int(m.group(2))
        if total <= 0 or step < self.step:
            return None
        self.step, self.total = step, total
        return step / total, f"step {step}/{total}"


class GsplatParser:
    """gsplat_train.py's lines -> fraction of the train stage: loading the images is the first
    LOAD_SHARE, the steps the rest, writing the .ply the end. Remembers steps, splats, time, peak VRAM,
    and the out-of-memory message if there was one."""
    LOAD_SHARE = 0.03

    def __init__(self):
        self.step, self.total, self.splats, self.took, self.views = 0, None, None, None, None
        self.peak_vram_mb, self.oom = None, None
        self.eval = None  # {"psnr", "ssim", "views"[, "wall": {...}]} of the held-out views (GSPLAT_EVAL_EVERY)
        self.zones = None  # {"wall", "surround", "outside"}: where the trained splats ended up (--zones)

    def __call__(self, line):
        m = GSPLAT_OOM.search(line)
        if m:
            self.oom = m.group(1)
            return None
        m = GSPLAT_LOADED.search(line)
        if m:
            i, n = int(m.group(1)), int(m.group(2))
            self.views = n
            return self.LOAD_SHARE * i / max(1, n), f"loading images {i}/{n}"
        m = GSPLAT_STEP.search(line)
        if m:
            step, total, self.splats = int(m.group(1)), int(m.group(2)), int(m.group(3))
            if total <= 0 or step < self.step:
                return None
            self.step, self.total = step, total
            return self.LOAD_SHARE + (1 - self.LOAD_SHARE) * step / total, f"step {step}/{total}, {self.splats} splats"
        m = GSPLAT_TOOK.search(line)
        if m:
            self.took = m.group(1)
            return 1.0, "writing splats"
        m = GSPLAT_EVAL.search(line)
        if m:
            self.eval = {**(self.eval or {}), "psnr": float(m.group(1)), "ssim": float(m.group(2)), "views": int(m.group(3))}
            return None
        m = GSPLAT_EVAL_WALL.search(line)
        if m:
            self.eval = {**(self.eval or {}), "wall": {"psnr": float(m.group(1)), "ssim": float(m.group(2)),
                                                       "views": int(m.group(3))}}
            return None
        m = GSPLAT_ZONES.search(line)
        if m:
            self.zones = json.loads(m.group(1))
            return None
        m = GSPLAT_DONE.search(line)
        if m:
            self.splats, self.peak_vram_mb = int(m.group(1)), int(m.group(2))
        return None
