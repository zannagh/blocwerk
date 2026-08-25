"""Building the pipeline command lines.

The stitcher is treated as a black box: the only thing this module knows is that it
takes an input directory plus an output ("work") directory, and that it may or may
not yet accept an explicit image list. The accepted flags are read from the script's
own `--help` at startup, so the wrapper keeps working while the CLI is generalised
from "1..5.jpeg in a fixed folder" to "arbitrary list of images".
"""
from __future__ import annotations

import functools
import re
import subprocess
from typing import Dict, List, Sequence

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


def stitch_command(python_executable: str, stitch_dir: str, script: str,
                   src_dir: str, work_dir: str, cache_dir: str,
                   wall_angle_degrees: float, images: Sequence[str],
                   max_canvas_mpx: float = 0.0, png_compression: int = -1,
                   emit_facets: bool = False) -> List[str]:
    """`stitch_wall.py --src <input> --work <output> [--images ...] --wall-angle <deg>`."""
    flags = supported_flags(python_executable, script, stitch_dir)
    argv = [python_executable, script]
    if not flags or "--src" in flags:
        argv += ["--src", src_dir]
    if not flags or "--work" in flags:
        argv += ["--work", work_dir]
    if not flags or "--cache" in flags:
        argv += ["--cache", cache_dir]
    if not flags or "--wall-angle" in flags:
        argv += ["--wall-angle", f"{wall_angle_degrees:g}"]
    # Memory controls, passed only where the pipeline advertises them, so an older
    # vendored snapshot keeps working unchanged.
    if max_canvas_mpx > 0 and "--max-canvas-mpx" in flags:
        argv += ["--max-canvas-mpx", f"{max_canvas_mpx:g}"]
    if png_compression >= 0 and "--png-compression" in flags:
        argv += ["--png-compression", str(png_compression)]
    # The shipping default: emit the multi-facet flat composite (ortho slot) and the
    # cylindrical natural photographic master (angled slot). Passed only where the
    # vendored pipeline advertises the flag, so an older snapshot silently keeps its
    # single-plane behaviour rather than erroring on an unknown argument.
    if emit_facets and "--emit-facets" in flags:
        argv += ["--emit-facets"]

    # The generalised CLI takes the images explicitly; the current one derives them
    # from --src, so passing nothing there is correct rather than a fallback hack.
    image_flag = _first(flags, "--images", "--inputs", "--photos")
    if image_flag and images:
        argv += [image_flag, *images]
    return argv


def holds_command(python_executable: str, holds_dir: str, script: str,
                  work_dir: str, old_image: str, new_image: str,
                  holds_json: str, wall_json: str) -> List[str]:
    """`remap_holds.py --work <output>` plus explicit inputs where the CLI takes them."""
    flags = supported_flags(python_executable, script, holds_dir)
    argv = [python_executable, script]
    if not flags or "--work" in flags:
        argv += ["--work", work_dir]
    for flag, value in (
        (_first(flags, "--old", "--old-image", "--old-photo"), old_image),
        (_first(flags, "--new", "--new-image", "--orthophoto"), new_image),
        (_first(flags, "--holds", "--holds-json"), holds_json),
        (_first(flags, "--wall", "--wall-json"), wall_json),
    ):
        if flag:
            argv += [flag, value]
    return argv


def crop_command(python_executable: str, holds_dir: str, script: str,
                 png_compression: int = -1) -> List[str]:
    """`crop_natural.py [--png-compression N]`.

    The hold-aware crop reads every path (work root, natural master, holds-remapped.json,
    report.json) from the same environment the matcher uses - see holds_environment - so
    there are no path arguments here. It runs AFTER remap_holds has transferred the live
    holds onto the uncropped cylindrical natural master.
    """
    flags = supported_flags(python_executable, script, holds_dir)
    argv = [python_executable, script]
    if png_compression >= 0 and (not flags or "--png-compression" in flags):
        argv += ["--png-compression", str(png_compression)]
    return argv


def holds_environment(work_root: str, old_image: str, new_image: str,
                      holds_json: str, wall_json: str, onnx_model: str) -> Dict[str, str]:
    """Inputs for the vendored copy, which reads them from the environment.

    See the VENDORED-COPY PATCH note in pipeline/holds_match/hm_common.py: the upstream
    file hardcodes a developer's home directory. These variables are ignored by any
    version that grows real CLI flags, so both paths can be passed at once.
    """
    return {
        "WALLSTITCH_WORK_ROOT": work_root,
        "WALLSTITCH_OLD_IMG": old_image,
        "WALLSTITCH_NEW_IMG": new_image,
        "WALLSTITCH_HOLDS_JSON": holds_json,
        "WALLSTITCH_WALL_JSON": wall_json,
        "WALLSTITCH_ONNX_MODEL": onnx_model,
    }
