"""Which GPU a 3D runner trains on and how much memory it has, on every OS (caps.py; gpu.py stays the NVIDIA-only
probe gsplat plans with).

detect() -> {"name", "vendor", "totalMb", "freeMb", "unified"}:
- SPLAT_VRAM_MB > 0 wins (free = total = that value; a discrete GPU).
- NVIDIA: `nvidia-smi` (Linux, Windows: the driver puts it on PATH).
- macOS: the chipset name from `system_profiler`; Apple Silicon has unified memory, so its "VRAM" is the machine's
  memory (the host budget is what limits Brush there).
- Windows without nvidia-smi: the display adapters in the registry (HardwareInformation.qwMemorySize, the dedicated
  memory as a 64-bit value: WMI's AdapterRAM is 32-bit and tops out at 4 GB). AMD / Intel discrete cards land here.
- Linux without nvidia-smi: amdgpu's sysfs (mem_info_vram_total / mem_info_vram_used under /sys/class/drm).
An adapter with less than SHARED_BELOW_MB of dedicated memory (an iGPU's carve-out) counts as unified: it trains out of
the host memory. Everything here is stdlib; the parsers are pure (tests/test_gpuprobe.py)."""
import json
import os
import subprocess
import sys

from . import gpu
from .settings import settings

SHARED_BELOW_MB = 2048
VIRTUAL_ADAPTERS = ("microsoft basic", "remote display", "virtual", "parsec", "citrix", "vmware", "hyper-v")
AMD_VENDOR = "0x1002"
VENDORS = (("nvidia", "nvidia"), ("geforce", "nvidia"), ("quadro", "nvidia"), ("radeon", "amd"), ("amd", "amd"),
           ("intel", "intel"), ("apple", "apple"))
DISPLAY_CLASS = r"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"
MB = 1024 * 1024


def vendor_of(name):
    low = (name or "").lower()
    return next((v for key, v in VENDORS if key in low), "unknown")


def gpu_doc(name, total_mb, free_mb=None, unified=None, vendor=None):
    """The detect() document; unified defaults to "less than SHARED_BELOW_MB dedicated"."""
    if unified is None:
        unified = not total_mb or total_mb < SHARED_BELOW_MB
    return {"name": name or "unknown", "vendor": vendor or vendor_of(name), "totalMb": total_mb,
            "freeMb": free_mb if free_mb is not None else total_mb, "unified": bool(unified)}


def best(docs):
    """The adapter to train on: discrete before unified, then the most memory."""
    docs = [d for d in docs if d]
    return max(docs, key=lambda d: (not d["unified"], d["totalMb"] or 0)) if docs else None


def _registry_int(value):
    """A registry memory size: QWORD (int), DWORD or REG_BINARY (little-endian bytes)."""
    if isinstance(value, int):
        return value
    if isinstance(value, (bytes, bytearray)) and value:
        return int.from_bytes(bytes(value[:8]), "little")
    return None


def parse_windows_adapters(entries):
    """detect() documents from the display class's subkeys: dicts with DriverDesc and HardwareInformation.qwMemorySize
    (or .MemorySize). Virtual adapters (basic render, remote desktop, ...) are dropped."""
    out = []
    for e in entries:
        name = str(e.get("DriverDesc") or "").strip()
        if not name or any(v in name.lower() for v in VIRTUAL_ADAPTERS):
            continue
        size = _registry_int(e.get("HardwareInformation.qwMemorySize"))
        if not size:
            size = _registry_int(e.get("HardwareInformation.MemorySize"))
        out.append(gpu_doc(name, (size or 0) // MB or None))
    return out


def _windows_registry_entries():
    try:
        import winreg
    except ImportError:
        return []
    entries = []
    try:
        root = winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, DISPLAY_CLASS)
    except OSError:
        return []
    with root:
        for i in range(64):
            try:
                sub = winreg.EnumKey(root, i)
            except OSError:
                break
            if not sub.isdigit():
                continue  # "Properties" and the like
            entries.append(_read_adapter_key(winreg, root, sub))
    return entries


def _read_adapter_key(winreg, root, sub):
    doc = {}
    try:
        with winreg.OpenKey(root, sub) as key:
            for name in ("DriverDesc", "HardwareInformation.qwMemorySize", "HardwareInformation.MemorySize"):
                try:
                    doc[name] = winreg.QueryValueEx(key, name)[0]
                except OSError:
                    pass
    except OSError:
        pass
    return doc


def parse_amd_sysfs(root="/sys/class/drm"):
    """detect() documents of amdgpu cards: card*/device/{vendor, mem_info_vram_total, mem_info_vram_used}."""
    out = []
    try:
        cards = sorted(n for n in os.listdir(root) if n.startswith("card") and n[4:].isdigit())
    except OSError:
        return out
    for card in cards:
        dev = os.path.join(root, card, "device")
        if _read(os.path.join(dev, "vendor")) != AMD_VENDOR:
            continue
        total, used = _read(os.path.join(dev, "mem_info_vram_total")), _read(os.path.join(dev, "mem_info_vram_used"))
        if not (total or "").isdigit():
            continue
        total_mb = int(total) // MB
        free_mb = total_mb - int(used) // MB if (used or "").isdigit() else total_mb
        out.append(gpu_doc(f"AMD GPU ({card})", total_mb, free_mb, vendor="amd"))
    return out


def _read(path):
    try:
        with open(path) as fh:
            return fh.read().strip()
    except OSError:
        return None


def apple_name(profiler_json):
    """The chipset name from `system_profiler SPDisplaysDataType -json`, or None."""
    try:
        doc = json.loads(profiler_json or "{}")
        return (doc.get("SPDisplaysDataType") or [{}])[0].get("sppci_model")
    except (ValueError, IndexError, AttributeError):
        return None


def _run(cmd):
    try:
        return subprocess.run(cmd, capture_output=True, text=True, timeout=20).stdout
    except (OSError, subprocess.SubprocessError):
        return ""


def detect(platform=None, total_ram_mb=None):
    """The GPU this runner trains on (see the module doc); total_ram_mb: the machine's memory (Apple's unified pool)."""
    platform = platform or sys.platform
    if settings.vram_mb > 0:
        return gpu_doc("SPLAT_VRAM_MB", settings.vram_mb, unified=False, vendor="override")
    if platform == "darwin":
        name = apple_name(_run(["system_profiler", "SPDisplaysDataType", "-json"])) or "Apple GPU"
        return gpu_doc(name, total_ram_mb, unified=True, vendor="apple")
    nvidia = gpu.vram()
    if nvidia:
        return gpu_doc(nvidia["name"], nvidia["totalMb"], nvidia["freeMb"], unified=False, vendor="nvidia")
    if platform == "win32":
        return best(parse_windows_adapters(_windows_registry_entries()))
    if platform.startswith("linux"):
        return best(parse_amd_sysfs())
    return None
