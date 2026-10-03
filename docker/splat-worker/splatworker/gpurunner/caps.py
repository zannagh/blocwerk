"""What this runner can do: trainer (SPLAT_TRAINER: gsplat on CUDA, Brush on Vulkan / Metal), GPU and
VRAM, host memory budget -> the highest quality profile it will train (hello, claim)."""
import json
import os
import platform
import subprocess

from .. import __version__, brush, gpu, gsplat_trainer, trainers
from ..profiles import QUALITIES
from ..resources import memory_budget, system_memory
from ..settings import settings

# Brush's host-memory needs per profile (README "quality profiles"): high 5-6 GB, max 12+ GB.
BRUSH_FLOOR_MB = {"max": 12 * 1024, "high": 5 * 1024}
# gsplat's VRAM for the profile at its splat floor (gsplat_trainer.vram_mb, 4:3 photos at the profile's
# edge, with the planner's headroom): ultra is the server's own rule (>= 98 % of 12 GB).
GSPLAT_FLOOR_MB = {"ultra": 0.98 * trainers.ULTRA_MIN_VRAM_MB, "max": 6 * 1024, "high": 4 * 1024}


def _run(cmd):
    try:
        return subprocess.run(cmd, capture_output=True, text=True, timeout=20).stdout
    except (OSError, subprocess.SubprocessError):
        return ""


def gpu_info():
    """(name, VRAM MB or None). macOS: the chipset (unified memory = VRAM); else nvidia-smi (gpu.py)."""
    if platform.system() == "Darwin":
        try:
            doc = json.loads(_run(["system_profiler", "SPDisplaysDataType", "-json"]) or "{}")
            name = (doc.get("SPDisplaysDataType") or [{}])[0].get("sppci_model")
        except (ValueError, IndexError, AttributeError):
            name = None
        return name or "Apple GPU", system_memory().get("totalMb")
    info = gpu.vram()
    return ((info or {}).get("name") or "unknown"), (info or {}).get("totalMb")


def budget_mb():
    """The host memory budget on this machine now (like the worker's train budget: SPLAT_MAX/MIN_MEMORY_MB)."""
    return memory_budget(system_memory(), settings.max_memory_mb, settings.min_memory_mb)[0]


def cap_quality(q, cap=None):
    cap = (cap or os.environ.get("RUNNER_MAX_QUALITY") or "").strip().lower()
    return cap if cap in QUALITIES and QUALITIES.index(cap) < QUALITIES.index(q) else q


def max_quality(trainer, vram_mb, budget, cap=None):
    """The highest profile this machine trains: gsplat by VRAM (ultra only on >= 12 GB), Brush by the host
    memory budget (>= 12 GB max, >= 5 GB high, else draft); at most `cap` / RUNNER_MAX_QUALITY."""
    if trainer == "gsplat":
        floors = GSPLAT_FLOOR_MB
        q = next((n for n in ("ultra", "max", "high") if (vram_mb or 0) >= floors[n]), "draft")
    else:
        q = next((n for n in ("max", "high") if budget >= BRUSH_FLOOR_MB[n]), "draft")
    return cap_quality(q, cap)


class Capabilities:
    """hello's document. The trainer's version is probed once, on first need (gsplat's probe imports torch in a child
    process, ~10 s, and touches CUDA): a runner that starts paused probes only when it is resumed."""

    UNPROBED = object()

    def __init__(self):
        self.trainer = trainers.select()
        self._version = self.UNPROBED

    @property
    def version(self):
        if self._version is self.UNPROBED:
            if self.trainer == "gsplat":
                self._version = gsplat_trainer.tool_version(settings.gsplat_python)
            else:
                self._version = brush.tool_version(settings.brush_bin)
        return self._version

    @property
    def usable(self):
        return self.version is not None

    def __call__(self):
        return self._doc(self.version)

    def lite(self):
        """The document without probing the trainer (while paused): its version stays unknown until a resume."""
        return self._doc(None if self._version is self.UNPROBED else self._version)

    def _doc(self, version):
        name, vram = gpu_info()
        budget = budget_mb()
        doc = {"runnerVersion": __version__, "gpuName": name, "vramMb": vram,
               "maxQuality": max_quality(self.trainer, vram, budget), "memoryBudgetMb": budget,
               "platform": f"{platform.system()} {platform.machine()}", "trainer": self.trainer,
               "cuda": self.trainer == "gsplat" and version is not None, "trainerVersion": version}
        if self.trainer == "brush":
            doc["brushVersion"] = version
        return doc
