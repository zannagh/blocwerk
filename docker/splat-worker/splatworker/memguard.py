"""Memory ceiling for the external tools: a footprint watchdog (every OS) and RLIMIT_AS (Linux only).

macOS does not enforce RLIMIT_AS, so the portable guard is a thread that polls the tool's memory
(the process plus its descendants) and SIGKILLs it above the ceiling. Plain RSS undercounts badly
under memory pressure: it leaves out compressed and swapped-out pages (COLMAP matching once showed
4.9 GB RSS at a 7.5 GB footprint, so a 6 GB cap never fired). So we measure what is charged to the
process instead:
- macOS: libproc's proc_pid_rusage(RUSAGE_INFO_V2).ri_phys_footprint (what `top`'s MEM column and
  Activity Monitor show; includes compressed pages), via ctypes, ~7 us per process.
- Linux: VmRSS + VmSwap from /proc/<pid>/status (the slim image has no `ps`).
- elsewhere: `ps` RSS.
Per process we take max(footprint, resident): the footprint leaves out shared/file-backed pages RSS
counts, so the guard never reads lower than plain RSS did. No psutil dependency.
"""
import collections
import ctypes
import os
import subprocess
import sys
import threading
import time

POLL_S = 0.25
# RLIMIT_AS caps VIRTUAL memory, which always exceeds RSS (thread stacks, malloc arenas, mapped
# libraries); give it headroom so the watchdog stays the precise guard and RLIMIT_AS the backstop.
AS_HEADROOM = 1.5
OOM_TEXT = ("bad_alloc", "cannot allocate memory", "out of memory", "memoryerror")
RUSAGE_INFO_V2 = 2
MAX_CHILDREN = 4096


def describe_mb(mb):
    """6144 -> '6 GB', 200 -> '200 MB'."""
    return f"{mb / 1024:.3g} GB" if mb >= 1024 else f"{mb} MB"


class _RusageInfoV2(ctypes.Structure):
    """struct rusage_info_v2 from <sys/resource.h>."""
    _fields_ = [("ri_uuid", ctypes.c_uint8 * 16)] + [(f"ri_{n}", ctypes.c_uint64) for n in (
        "user_time", "system_time", "pkg_idle_wkups", "interrupt_wkups", "pageins", "wired_size",
        "resident_size", "phys_footprint", "proc_start_abstime", "proc_exit_abstime", "child_user_time",
        "child_system_time", "child_pkg_idle_wkups", "child_interrupt_wkups", "child_pageins",
        "child_elapsed_abstime", "diskio_bytesread", "diskio_byteswritten")]


def _load_libproc():
    if sys.platform != "darwin":
        return None
    try:
        lib = ctypes.CDLL("/usr/lib/libproc.dylib", use_errno=True)
        lib.proc_pid_rusage.argtypes = [ctypes.c_int, ctypes.c_int, ctypes.c_void_p]
        lib.proc_listchildpids.argtypes = [ctypes.c_int, ctypes.c_void_p, ctypes.c_int]
        return lib
    except (OSError, AttributeError):
        return None


_LIBPROC = _load_libproc()


def _darwin_footprint(pid):
    """max(phys_footprint, resident) bytes of one process; None once it is gone."""
    info = _RusageInfoV2()
    if _LIBPROC.proc_pid_rusage(pid, RUSAGE_INFO_V2, ctypes.byref(info)) != 0:
        return None
    return max(info.ri_phys_footprint, info.ri_resident_size)


def _darwin_children(pid):
    buf = (ctypes.c_int * MAX_CHILDREN)()
    n = _LIBPROC.proc_listchildpids(pid, buf, ctypes.sizeof(buf))
    return [c for c in buf[:max(0, min(n, MAX_CHILDREN))] if c > 0]


def _darwin_tree(pid):
    total, todo, seen = None, [pid], set()
    while todo:
        p = todo.pop()
        if p in seen:
            continue
        seen.add(p)
        size = _darwin_footprint(p)
        if size is None:
            continue
        total = (total or 0) + size
        todo.extend(_darwin_children(p))
    return total or 0


def parse_linux_status(text):
    """(ppid, VmRSS + VmSwap bytes) from the text of /proc/<pid>/status."""
    ppid, size = 0, 0
    for line in text.splitlines():
        key, _, value = line.partition(":")
        if key == "PPid":
            ppid = int(value)
        elif key in ("VmRSS", "VmSwap"):
            size += int(value.split()[0]) * 1024  # always reported in kB
    return ppid, size


def _proc_table():
    """{pid: (ppid, bytes)} of all processes we can see (Linux /proc, else `ps` RSS)."""
    table = {}
    if sys.platform.startswith("linux") and os.path.isdir("/proc/self"):
        for name in os.listdir("/proc"):
            if not name.isdigit():
                continue
            try:
                with open(f"/proc/{name}/status") as fh:
                    table[int(name)] = parse_linux_status(fh.read())
            except (OSError, ValueError, IndexError):
                continue
        return table
    try:
        out = subprocess.run(["ps", "-A", "-o", "pid=,ppid=,rss="], capture_output=True, text=True,
                             timeout=10).stdout
    except (OSError, subprocess.SubprocessError):
        return table
    for line in out.splitlines():
        parts = line.split()
        if len(parts) == 3 and all(p.isdigit() for p in parts):
            table[int(parts[0])] = (int(parts[1]), int(parts[2]) * 1024)
    return table


def tree_footprint(pid):
    """Memory bytes charged to `pid` and all its descendants (0 once it is gone)."""
    if _LIBPROC is not None:
        return _darwin_tree(pid)
    table = _proc_table()
    if pid not in table:
        return 0
    children = {}
    for p, (pp, _) in table.items():
        children.setdefault(pp, []).append(p)
    total, todo = 0, [pid]
    while todo:
        p = todo.pop()
        total += table[p][1]
        todo.extend(children.get(p, ()))
    return total


def address_space_limiter(limit_mb):
    """preexec_fn setting RLIMIT_AS in the child (Linux only; None elsewhere or when off)."""
    if not limit_mb or not sys.platform.startswith("linux"):
        return None
    import resource
    cap = int(limit_mb * AS_HEADROOM) * 1024 * 1024

    def preexec():
        resource.setrlimit(resource.RLIMIT_AS, (cap, cap))
    return preexec


class Watchdog(threading.Thread):
    """Kills `proc` once its tree's footprint exceeds limit_mb, or once the SYSTEM's swap GREW by more
    than swap_limit_mb within the last SWAP_WINDOW_S seconds while the tool holds at least half its
    limit (the machine is paging fast because of it). Never the absolute swap level: macOS keeps
    "swap used" high long after the pressure is gone. `tripped` is None, "memory" or "swap";
    `peak_mb` / `swap_growth_mb` (the largest growth seen in one window) tell how far it got.
    swap_meter: object with used_mb() (None = unknown); clock: time source (tests)."""

    SWAP_EVERY = 4  # read the swap every 4th poll (~1 s)
    SWAP_WINDOW_S = 120.0  # growth rate window: slow creep over a long Brush run is not a trip
    # Swap growth is system-wide: only blame the tool when it is a big consumer itself (at least half
    # its memory limit). A Brush run at 2.1 GB of a 4.5 GB limit was once killed after 11 minutes
    # because another workload made the machine swap.
    SWAP_BLAME_FRACTION = 0.5

    def __init__(self, proc, limit_mb, poll_s=POLL_S, swap_limit_mb=0, swap_meter=None, clock=time.monotonic):
        super().__init__(daemon=True)
        self.proc, self.limit, self.poll_s = proc, limit_mb * 1024 * 1024, poll_s
        self.swap_limit, self.swap_meter = swap_limit_mb, swap_meter if swap_limit_mb else None
        self.clock, self.swap_samples = clock, collections.deque()
        self.swap_base = self.swap_meter.used_mb() if self.swap_meter else None
        if self.swap_base is not None:
            self.swap_samples.append((clock(), self.swap_base))
        self.tripped, self.peak, self.swap_growth, self.done = None, 0, 0.0, threading.Event()

    @property
    def peak_mb(self):
        return round(self.peak / (1024 * 1024))

    @property
    def swap_growth_mb(self):
        return round(self.swap_growth)

    def _swap_grew_too_much(self):
        """Swap growth over the last SWAP_WINDOW_S (from the lowest reading in the window) > the limit."""
        used = self.swap_meter.used_mb()
        if used is None or self.swap_base is None:
            return False
        now = self.clock()
        self.swap_samples.append((now, used))
        while self.swap_samples and self.swap_samples[0][0] < now - self.SWAP_WINDOW_S:
            self.swap_samples.popleft()
        growth = used - min(u for _, u in self.swap_samples)
        self.swap_growth = max(self.swap_growth, growth)
        return growth > self.swap_limit

    def _kill(self, why):
        self.tripped = why
        try:
            self.proc.kill()
        except OSError:
            pass

    def run(self):
        n = 0
        while not self.done.is_set() and self.proc.poll() is None:
            used = tree_footprint(self.proc.pid)
            self.peak = max(self.peak, used)
            if used > self.limit:
                return self._kill("memory")
            if self.swap_meter and n % self.SWAP_EVERY == 0 and self._swap_grew_too_much() \
                    and used >= self.limit * self.SWAP_BLAME_FRACTION:
                return self._kill("swap")
            n += 1
            self.done.wait(self.poll_s)

    def stop(self):
        self.done.set()
        self.join(5)
