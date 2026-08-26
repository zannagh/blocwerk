"""Building the pipeline command line.

The pipeline is treated as a black box: the only thing this module knows is that it
takes an input directory plus an output directory, and which optional flags it might
accept. The accepted flags are read from the script's own `--help` at startup rather
than assumed, so a vendored snapshot that predates a flag keeps working instead of
dying on an unrecognised argument - which is also what lets the pipeline and the
sidecar move independently.
"""
from __future__ import annotations

import functools
import re
import subprocess
from typing import List, Optional, Sequence

FLAG = re.compile(r"(--[a-z0-9][a-z0-9-]*)")


@functools.lru_cache(maxsize=16)
def supported_flags(python_executable: str, script: str, cwd: str) -> frozenset:
    """Flags the script's argument parser advertises. Empty set if it cannot be asked."""
    try:
        proc = subprocess.run(
            [python_executable, script, "--help"],
            cwd=cwd, capture_output=True, text=True, timeout=120, check=False)
    except (OSError, subprocess.SubprocessError):
        return frozenset()
    return frozenset(FLAG.findall((proc.stdout or "") + (proc.stderr or "")))


def _first(flags: frozenset, *candidates: str) -> str:
    for candidate in candidates:
        if candidate in flags:
            return candidate
    return ""


class ArgvBuilder:
    """Appends a flag only where the pipeline advertises it (or advertises nothing)."""

    def __init__(self, flags: frozenset, argv: List[str]):
        self.flags = flags
        self.argv = argv

    def add(self, flag: str, *values: object) -> "ArgvBuilder":
        if self.flags and flag not in self.flags:
            return self
        self.argv.append(flag)
        self.argv.extend(str(v) for v in values)
        return self

    def maybe(self, flag: str, value: Optional[object]) -> "ArgvBuilder":
        """Same, but skipped entirely when the value is absent."""
        if value in (None, ""):
            return self
        return self.add(flag, value)


def pipeline_command(python_executable: str, pipeline_dir: str, script: str,
                     input_dir: str, output_dir: str, cache_dir: str,
                     natural: str, curve: str, onnx_model: str,
                     wall_width_m: float, wall_height_m: float,
                     work_mp: float, compose_mp: float, max_canvas_mpx: float,
                     nfeat: int = 0,
                     prior_holds: str = "", prior_image: str = "",
                     images: Sequence[str] = ()) -> List[str]:
    """`wall_pipeline.py --input-dir <in> --output-dir <out> ...`.

    Carryover is requested by supplying both `prior_holds` and `prior_image`; the
    pipeline skips that stage on its own when either is missing, which is exactly the
    fresh-wall case.
    """
    flags = supported_flags(python_executable, script, pipeline_dir)
    argv = [python_executable, script]
    build = ArgvBuilder(flags, argv)

    # The generalised CLI takes the frames explicitly; otherwise it derives them from
    # the input directory in filename order, which is the sweep order the uploads were
    # numbered into.
    image_flag = _first(flags, "--images")
    if image_flag and images:
        build.add(image_flag, *images)
    else:
        build.add("--input-dir", input_dir)

    build.add("--output-dir", output_dir)
    build.add("--cache-dir", cache_dir)
    build.add("--natural", natural)
    build.add("--curve", curve)
    build.add("--wall-width-m", f"{wall_width_m:g}")
    build.add("--wall-height-m", f"{wall_height_m:g}")
    build.add("--work-mp", f"{work_mp:g}")
    build.add("--compose-mp", f"{compose_mp:g}")
    build.add("--max-canvas-mpx", f"{max_canvas_mpx:g}")
    if nfeat > 0:
        build.add("--nfeat", nfeat)
    build.maybe("--onnx", onnx_model)
    if prior_holds and prior_image:
        build.add("--prior-holds", prior_holds)
        build.add("--prior-image", prior_image)
    return argv
