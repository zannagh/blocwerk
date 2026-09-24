"""What this runner can do: GPU, memory budget -> the highest quality profile it will train."""
import json
import os
import platform
import subprocess

from .. import __version__, brush
from ..profiles import QUALITIES
from ..resources import memory_budget, system_memory
from ..settings import settings

# Brush's needs per profile (README "quality profiles"): high 5-6 GB, max 12+ GB.
QUALITY_FLOOR_MB = {"max": 12 * 1024, "high": 5 * 1024}


def _run(cmd):
    try:
        return subprocess.run(cmd, capture_output=True, text=True, timeout=20).stdout
    except (OSError, subprocess.SubprocessError):
        return ""


def gpu():
    """(name, VRAM MB or None). macOS: the chipset (unified memory = VRAM); Linux: nvidia-smi."""
    if platform.system() == "Darwin":
        try:
            doc = json.loads(_run(["system_profiler", "SPDisplaysDataType", "-json"]) or "{}")
            name = (doc.get("SPDisplaysDataType") or [{}])[0].get("sppci_model")
        except (ValueError, IndexError, AttributeError):
            name = None
        total = system_memory().get("totalMb")
        return name or "Apple GPU", total
    out = _run(["nvidia-smi", "--query-gpu=name,memory.total", "--format=csv,noheader,nounits"]).strip()
    if out:
        name, _, mem = out.splitlines()[0].rpartition(",")
        try:
            return name.strip(), int(float(mem))
        except ValueError:
            return name.strip() or "unknown", None
    return "unknown", None


def budget_mb():
    """Brush's budget on this machine now (like the worker's train budget: SPLAT_MAX/MIN_MEMORY_MB)."""
    return memory_budget(system_memory(), settings.max_memory_mb, settings.min_memory_mb)[0]


def max_quality(budget, cap=None):
    """The highest profile the budget carries (>= 12 GB max, >= 5 GB high, else draft), at most `cap`."""
    q = next((name for name in ("max", "high") if budget >= QUALITY_FLOOR_MB[name]), "draft")
    cap = (cap or os.environ.get("RUNNER_MAX_QUALITY") or "").strip().lower()
    if cap in QUALITIES and QUALITIES.index(cap) < QUALITIES.index(q):
        q = cap
    return q


def capabilities():
    name, vram = gpu()
    budget = budget_mb()
    return {"runnerVersion": __version__, "gpuName": name, "vramMb": vram, "maxQuality": max_quality(budget),
            "memoryBudgetMb": budget, "platform": f"{platform.system()} {platform.machine()}",
            "brushVersion": brush.tool_version(settings.brush_bin)}
