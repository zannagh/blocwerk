"""What memory this machine (or container) can give a job, read at job start: the memory budget.

- macOS: `sysctl hw.memsize` (total), `vm_stat` (free + inactive + speculative + purgeable pages =
  available without swapping: inactive and purgeable pages are reclaimed on demand), `sysctl vm.swapusage`.
- Linux (the worker runs in Docker): /proc/meminfo MemTotal / MemAvailable / SwapTotal / SwapFree, and
  the cgroup limit (v2 /sys/fs/cgroup/memory.max + memory.current, v1 memory.limit_in_bytes +
  memory.usage_in_bytes, page cache from memory.stat counted as reclaimable), because /proc/meminfo
  shows the whole VM, not the container.

budget = min(available + reclaimable, total x BUDGET_FRACTION), at least MIN_BUDGET_MB (never more
than the total fraction), and never above SPLAT_MAX_MEMORY_MB when that is set (a hard upper bound).
"""
import ctypes
import os
import re
import subprocess
import sys

BUDGET_FRACTION = 0.6
MIN_BUDGET_MB = 3072  # feature extraction at 4 threads needs ~2.5 GB; below that the tuning drops threads
MB = 1024 * 1024
CGROUP = "/sys/fs/cgroup"
UNLIMITED = 1 << 60  # cgroup v1 reports "no limit" as a huge number


def _sysctl(name):
    try:
        return subprocess.run(["sysctl", "-n", name], capture_output=True, text=True, timeout=10).stdout.strip()
    except (OSError, subprocess.SubprocessError):
        return ""


def parse_vm_stat(text):
    """Bytes available without swapping from `vm_stat` output (free + inactive + speculative + purgeable)."""
    page = int(m.group(1)) if (m := re.search(r"page size of (\d+) bytes", text)) else 16384
    pages = {k.strip().lower(): int(v.strip().rstrip(".")) for k, v in re.findall(r"^Pages ([^:]+):\s+(\d+)\.?$", text, re.M)}
    return page * sum(pages.get(k, 0) for k in ("free", "inactive", "speculative", "purgeable"))


def parse_swapusage(text):
    """(total, used) MB from `sysctl -n vm.swapusage` ("total = 7168.00M  used = 5986.19M  free = ...")."""
    vals = dict(re.findall(r"(total|used) = ([0-9.]+)M", text))
    return float(vals.get("total", 0)), float(vals.get("used", 0))


def parse_meminfo(text):
    """{key: bytes} from /proc/meminfo."""
    out = {}
    for line in text.splitlines():
        key, _, rest = line.partition(":")
        parts = rest.split()
        if parts and parts[0].isdigit():
            out[key] = int(parts[0]) * (1024 if len(parts) > 1 else 1)
    return out


def _read(path):
    try:
        with open(path) as fh:
            return fh.read().strip()
    except OSError:
        return None


def cgroup_memory(root=CGROUP):
    """(limit, usage, reclaimable page cache) bytes of this container's cgroup, or None when unlimited."""
    limit, usage = _read(os.path.join(root, "memory.max")), _read(os.path.join(root, "memory.current"))
    stat_file, cache_key = "memory.stat", "inactive_file"
    if limit is None:  # cgroup v1
        limit = _read(os.path.join(root, "memory", "memory.limit_in_bytes"))
        usage = _read(os.path.join(root, "memory", "memory.usage_in_bytes"))
        stat_file, cache_key = os.path.join("memory", "memory.stat"), "total_inactive_file"
    if not limit or not limit.isdigit() or int(limit) >= UNLIMITED:  # "max" or absent
        return None
    stat = dict(ln.split()[:2] for ln in (_read(os.path.join(root, stat_file)) or "").splitlines() if len(ln.split()) >= 2)
    cache = int(stat.get(cache_key, 0)) if str(stat.get(cache_key, "0")).isdigit() else 0
    return int(limit), int(usage) if usage and usage.isdigit() else 0, cache


def system_memory():
    """{totalMb, availableMb, swapTotalMb, swapUsedMb, cgroupLimitMb} (None where unknown)."""
    info = {"totalMb": None, "availableMb": None, "swapTotalMb": None, "swapUsedMb": None, "cgroupLimitMb": None}
    if sys.platform == "darwin":
        total = _sysctl("hw.memsize")
        info["totalMb"] = int(total) // MB if total.isdigit() else None
        try:
            vm = subprocess.run(["vm_stat"], capture_output=True, text=True, timeout=10).stdout
            info["availableMb"] = parse_vm_stat(vm) // MB
        except (OSError, subprocess.SubprocessError):
            pass
        info["swapTotalMb"], info["swapUsedMb"] = parse_swapusage(_sysctl("vm.swapusage"))
    elif sys.platform.startswith("linux"):
        mi = parse_meminfo(_read("/proc/meminfo") or "")
        info["totalMb"] = mi.get("MemTotal", 0) // MB or None
        info["availableMb"] = mi.get("MemAvailable", mi.get("MemFree", 0)) // MB or None
        info["swapTotalMb"] = mi.get("SwapTotal", 0) / MB
        info["swapUsedMb"] = (mi.get("SwapTotal", 0) - mi.get("SwapFree", 0)) / MB
        cg = cgroup_memory()
        if cg:
            limit, usage, cache = cg
            info["cgroupLimitMb"] = limit // MB
            info["totalMb"] = min(info["totalMb"] or limit // MB, limit // MB)
            room = (limit - usage + cache) // MB
            info["availableMb"] = min(info["availableMb"] or room, room)
    return info


def memory_budget(info, cap_mb=0, floor_mb=0):
    """(budget MB, one-line explanation) from system_memory(); cap_mb > 0 = SPLAT_MAX_MEMORY_MB;
    floor_mb > 0 = SPLAT_MIN_MEMORY_MB (replaces MIN_BUDGET_MB: the operator vouches that the machine can
    page idle apps out of the way; still never above BUDGET_FRACTION of it, and the swap guard stays on)."""
    total, avail = info.get("totalMb"), info.get("availableMb")
    if not total:
        budget, why = cap_mb or 6144, "machine memory unknown"
    else:
        share = int(total * BUDGET_FRACTION)
        free = avail if avail is not None else share
        budget = min(share, max(free, floor_mb if floor_mb and floor_mb > 0 else MIN_BUDGET_MB))
        why = f"{free / 1024:.1f} GB available of {total / 1024:.1f} GB"
        if info.get("cgroupLimitMb"):
            why += " (container limit)"
    if cap_mb and cap_mb > 0 and cap_mb < budget:
        budget, why = cap_mb, why + ", capped by SPLAT_MAX_MEMORY_MB"
    return int(budget), why


class SwapMeter:
    """System swap in use (MB), cheap enough to poll every second from the watchdog."""

    class _XswUsage(ctypes.Structure):
        _fields_ = [("total", ctypes.c_uint64), ("avail", ctypes.c_uint64), ("used", ctypes.c_uint64),
                    ("pagesize", ctypes.c_uint32), ("encrypted", ctypes.c_bool)]

    def __init__(self):
        self.libc = None
        if sys.platform == "darwin":
            try:
                self.libc = ctypes.CDLL("/usr/lib/libc.dylib")
            except OSError:
                self.libc = None

    def used_mb(self):
        """Swap in use, or None when it cannot be read."""
        if self.libc is not None:
            xsw, size = self._XswUsage(), ctypes.c_size_t(ctypes.sizeof(self._XswUsage))
            if self.libc.sysctlbyname(b"vm.swapusage", ctypes.byref(xsw), ctypes.byref(size), None, 0) == 0:
                return xsw.used / MB
            return None
        text = _read("/proc/meminfo")
        if text is None:
            return None
        mi = parse_meminfo(text)
        return (mi.get("SwapTotal", 0) - mi.get("SwapFree", 0)) / MB
