"""Brush trainer (wgpu: Metal on macOS, Vulkan on Linux). Pinned release: v0.3.0."""
import glob
import os
import subprocess

from computejobs.child import JobError, tool_env

from .parsers import BRUSH_STEPS, BrushParser
from .procs import ToolRun


def tool_version(bin_path):
    try:
        out = subprocess.run([bin_path, "--version"], capture_output=True, text=True, timeout=30, env=tool_env()).stdout
    except (OSError, subprocess.SubprocessError):
        return None
    return out.strip().split()[-1] if out.strip() else "unknown"


def _worth_logging(line):
    """Keep the log small: no spinner frames, a step-counter line every 500 steps."""
    if "\U0001f58c" in line and "Loading dataset" not in line:  # the paintbrush spinner / header
        return False
    m = BRUSH_STEPS.search(line)
    return m is None or int(m.group(1)) % 500 == 0


def train(bin_path, dataset_dir, out_dir, steps, max_edge, cache_dir, log_path, report, max_memory_mb=0, swap_limit_mb=0):
    """Train `steps` steps on a COLMAP dataset (images/ + sparse/0); returns (ply path, parser)."""
    os.makedirs(out_dir, exist_ok=True)
    os.makedirs(cache_dir, exist_ok=True)
    parser = BrushParser()

    def on_line(line):
        r = parser(line)
        if r:
            report(*r)

    cmd = [bin_path, dataset_dir, "--total-steps", str(steps), "--export-every", str(steps),
           "--export-path", out_dir, "--export-name", "splat_{iter}.ply", "--max-resolution", str(max_edge),
           "--eval-every", str(steps + 1)]
    # A TTY is required: Brush prints nothing (not even errors) to a pipe.
    # Memory: the RSS watchdog only (RLIMIT_AS would trip on the GPU driver's virtual reservations).
    ToolRun("train", cmd, cache_dir, log_path, on_line, use_pty=True, log_filter=_worth_logging,
            mem_limit_mb=max_memory_mb, swap_limit_mb=swap_limit_mb, name="Brush").run()
    plys = sorted(glob.glob(os.path.join(out_dir, "splat_*.ply")), key=os.path.getmtime)
    if not plys:
        raise JobError("train", "Brush exited without exporting a splat (no GPU adapter? see the "
                                f"worker's train.log): {parser.took or 'no training output'}")
    return plys[-1], parser
