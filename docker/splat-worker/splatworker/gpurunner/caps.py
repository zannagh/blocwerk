"""What this runner can do: trainer (SPLAT_TRAINER: gsplat on CUDA, Brush on Vulkan / DX12 / Metal), GPU and
VRAM (gpuprobe.py: NVIDIA, AMD, Intel, Apple), host memory budget -> the highest quality profile it will train (hello,
claim); and whether it renders wall textures (`capabilities`, `texturesMemoryMb`: textures.py) and finds holds
(`capabilities`: holds.py) or registers panel photos (panels.py)."""
import os
import platform

from .. import __version__, brush, gpuprobe, gsplat_trainer, trainers
from ..profiles import QUALITIES
from ..resources import memory_budget, system_memory
from ..settings import settings
from . import holds, panels, textures

# Brush's host-memory needs per profile (README "quality profiles"): high 5-6 GB, max 12+ GB.
BRUSH_FLOOR_MB = {"max": 12 * 1024, "high": 5 * 1024}
# Brush's VRAM per profile on a DISCRETE GPU (not measured yet: estimated from the splat caps at ~1 KB per splat for
# parameters + Adam state + gradients, plus raster buffers): max (5 M splats) on a 12 GB card, high (2 M) on a 6 GB
# card, at 95 % of the nominal size (what the cards report). Unified memory (Apple Silicon, iGPUs) has no separate
# pool: the host budget above is its limit. SPLAT_VRAM_MB / RUNNER_MAX_QUALITY override.
BRUSH_VRAM_FLOOR_MB = {"max": 0.95 * 12 * 1024, "high": 0.95 * 6 * 1024}
# A discrete adapter whose VRAM could not be read (a registry entry without a size): assume a small card (draft)
# rather than the host's RAM; SPLAT_VRAM_MB states the real size.
UNKNOWN_VRAM_MB = 4096
# gsplat's VRAM for the profile at its splat floor (gsplat_trainer.vram_mb, 4:3 photos at the profile's
# edge, with the planner's headroom): ultra is the server's own rule (>= 98 % of 12 GB).
GSPLAT_FLOOR_MB = {"ultra": 0.98 * trainers.ULTRA_MIN_VRAM_MB, "max": 6 * 1024, "high": 4 * 1024}


def gpu_info():
    """(name, memory MB or None, unified) of the GPU gpuprobe.detect() picks. Unified memory (Apple Silicon, an iGPU):
    the machine's memory, what Brush trains out of; a discrete GPU: its VRAM (None when unknown). No adapter seen at
    all (a container without nvidia-smi or sysfs): unified, i.e. the host budget alone, as before gpuprobe."""
    total_ram = system_memory().get("totalMb")
    info = gpuprobe.detect(total_ram_mb=total_ram)
    if info is None:
        return "unknown", None, True
    return info["name"], (total_ram if info["unified"] else info["totalMb"]), info["unified"]


def budget_mb():
    """The host memory budget on this machine now (like the worker's train budget: SPLAT_MAX/MIN_MEMORY_MB)."""
    return memory_budget(system_memory(), settings.max_memory_mb, settings.min_memory_mb)[0]


def cap_quality(q, cap=None):
    cap = (cap or os.environ.get("RUNNER_MAX_QUALITY") or "").strip().lower()
    return cap if cap in QUALITIES and QUALITIES.index(cap) < QUALITIES.index(q) else q


def _tier(floors, mb, names):
    return next((n for n in names if (mb or 0) >= floors[n]), "draft")


def max_quality(trainer, vram_mb, budget, cap=None, unified=None):
    """The highest profile this machine trains: gsplat by a discrete card's VRAM (ultra only on >= 12 GB; unified
    memory is never VRAM to it); Brush by the host memory budget (>= 12 GB max, >= 5 GB high, else draft) and, on a
    discrete GPU (unified=False), also by its VRAM (BRUSH_VRAM_FLOOR_MB; the lower tier wins), an unknown VRAM taken as
    UNKNOWN_VRAM_MB (draft). unified=None (not known): gsplat takes vram_mb as VRAM, Brush the budget alone.
    At most `cap` / RUNNER_MAX_QUALITY."""
    if trainer == "gsplat":
        q = _tier(GSPLAT_FLOOR_MB, None if unified is True else vram_mb, ("ultra", "max", "high"))
    else:
        q = _tier(BRUSH_FLOOR_MB, budget, ("max", "high"))
        if unified is False:
            q = min(q, _tier(BRUSH_VRAM_FLOOR_MB, vram_mb or UNKNOWN_VRAM_MB, ("max", "high")), key=QUALITIES.index)
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
        name, vram, unified = gpu_info()
        budget = budget_mb()
        doc = {"runnerVersion": __version__, "gpuName": name, "vramMb": vram,
               "maxQuality": max_quality(self.trainer, vram, budget, unified=unified), "memoryBudgetMb": budget,
               "platform": f"{platform.system()} {platform.machine()}", "trainer": self.trainer,
               "cuda": self.trainer == "gsplat" and version is not None, "trainerVersion": version,
               **textures.advertise()}
        doc["capabilities"] = doc["capabilities"] + holds.advertise() + panels.advertise()
        if self.trainer == "brush":
            doc["brushVersion"] = version
        return doc
