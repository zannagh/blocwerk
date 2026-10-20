"""Windows memory readings through ctypes (stdlib only; nothing here is imported for its side effects elsewhere):

- memory_status(): GlobalMemoryStatusEx -> total / available physical memory and the page file (resources.py).
- keep_awake(): no idle sleep while the runner runs (gpurunner/__main__.py; RUNNER_KEEP_AWAKE=0 turns it off).
- process_table(): {pid: (ppid, bytes)} of every process, for the footprint watchdog (memguard.py). The parent comes
  from a Toolhelp32 snapshot, the size is the working set (K32GetProcessMemoryInfo; the resident pages, like Linux's
  VmRSS). Not PrivateUsage (the commit charge): under WDDM the GPU driver commits staging memory into the process, so
  a Brush run would read far above what it really holds and the guard would kill it. The price: pages Windows paged
  out are not counted (the guard reads low under memory pressure). A process we may not open is left out.
The pure parts (page file -> swap figures, the process tree) are tested on every OS (tests/test_winsys.py)."""
import ctypes
import sys

MB = 1024 * 1024
TH32CS_SNAPPROCESS = 0x00000002
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
PROCESS_VM_READ = 0x0010
INVALID_HANDLE = ctypes.c_void_p(-1).value
MAX_PATH = 260
ES_CONTINUOUS, ES_SYSTEM_REQUIRED = 0x80000000, 0x00000001


class MemoryStatusEx(ctypes.Structure):
    _fields_ = [("dwLength", ctypes.c_uint32), ("dwMemoryLoad", ctypes.c_uint32),
                ("ullTotalPhys", ctypes.c_uint64), ("ullAvailPhys", ctypes.c_uint64),
                ("ullTotalPageFile", ctypes.c_uint64), ("ullAvailPageFile", ctypes.c_uint64),
                ("ullTotalVirtual", ctypes.c_uint64), ("ullAvailVirtual", ctypes.c_uint64),
                ("ullAvailExtendedVirtual", ctypes.c_uint64)]


class ProcessEntry32(ctypes.Structure):
    _fields_ = [("dwSize", ctypes.c_uint32), ("cntUsage", ctypes.c_uint32), ("th32ProcessID", ctypes.c_uint32),
                ("th32DefaultHeapID", ctypes.c_size_t), ("th32ModuleID", ctypes.c_uint32),
                ("cntThreads", ctypes.c_uint32), ("th32ParentProcessID", ctypes.c_uint32),
                ("pcPriClassBase", ctypes.c_long), ("dwFlags", ctypes.c_uint32), ("szExeFile", ctypes.c_char * MAX_PATH)]


class ProcessMemoryCountersEx(ctypes.Structure):
    _fields_ = [("cb", ctypes.c_uint32), ("PageFaultCount", ctypes.c_uint32),
                ("PeakWorkingSetSize", ctypes.c_size_t), ("WorkingSetSize", ctypes.c_size_t),
                ("QuotaPeakPagedPoolUsage", ctypes.c_size_t), ("QuotaPagedPoolUsage", ctypes.c_size_t),
                ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t), ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
                ("PagefileUsage", ctypes.c_size_t), ("PeakPagefileUsage", ctypes.c_size_t),
                ("PrivateUsage", ctypes.c_size_t)]


def _kernel32():
    if sys.platform != "win32":
        return None
    try:
        k = ctypes.WinDLL("kernel32", use_last_error=True)
        k.CreateToolhelp32Snapshot.restype = ctypes.c_void_p
        k.CreateToolhelp32Snapshot.argtypes = [ctypes.c_uint32, ctypes.c_uint32]
        k.Process32First.argtypes = k.Process32Next.argtypes = [ctypes.c_void_p, ctypes.POINTER(ProcessEntry32)]
        k.OpenProcess.restype = ctypes.c_void_p
        k.OpenProcess.argtypes = [ctypes.c_uint32, ctypes.c_int, ctypes.c_uint32]
        k.CloseHandle.argtypes = [ctypes.c_void_p]
        k.K32GetProcessMemoryInfo.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint32]
        return k
    except (OSError, AttributeError):
        return None


KERNEL32 = _kernel32()


def swap_from_status(total_phys, total_page_file, avail_page_file, avail_phys):
    """(swap total MB, swap used MB) from GlobalMemoryStatusEx: the commit limit is RAM + page file, so the page file
    alone is the difference; "used" is the commit charge beyond RAM, never below 0."""
    swap_total = max(0, total_page_file - total_phys)
    committed = total_page_file - avail_page_file
    in_ram = total_phys - avail_phys
    return swap_total / MB, max(0, min(swap_total, committed - in_ram)) / MB


def memory_status():
    """{totalMb, availableMb, swapTotalMb, swapUsedMb} or None off Windows / on a failed call."""
    if KERNEL32 is None:
        return None
    st = MemoryStatusEx()
    st.dwLength = ctypes.sizeof(MemoryStatusEx)
    if not KERNEL32.GlobalMemoryStatusEx(ctypes.byref(st)):
        return None
    swap_total, swap_used = swap_from_status(st.ullTotalPhys, st.ullTotalPageFile, st.ullAvailPageFile,
                                             st.ullAvailPhys)
    return {"totalMb": st.ullTotalPhys // MB, "availableMb": st.ullAvailPhys // MB, "swapTotalMb": swap_total,
            "swapUsedMb": swap_used}


CLOSE_EVENTS = (2, 5, 6)  # CTRL_CLOSE_EVENT, CTRL_LOGOFF_EVENT, CTRL_SHUTDOWN_EVENT
CLOSE_GRACE_S = 4.5  # Windows ends the process ~5 s after a close event, whatever the handler does
_HANDLERS = []  # the ctypes callbacks must outlive the registration


def close_event_handler(on_close, grace_s=CLOSE_GRACE_S):
    """The console control handler: for a close / log-off / shutdown it calls on_close(grace_s) (which hands the job
    back and returns once that is done or the grace is over) and claims the event; Ctrl+C / Ctrl+Break pass on to
    Python's own handler (SIGINT / SIGBREAK). Pure: tested on every OS."""
    def handler(event):
        if event not in CLOSE_EVENTS:
            return False
        try:
            on_close(grace_s)
        except Exception:  # noqa: BLE001 - nothing may escape into the console thread
            pass
        return True
    return handler


def on_console_close(on_close):
    """Registers close_event_handler(on_close) with SetConsoleCtrlHandler; whether it took (False off Windows)."""
    if KERNEL32 is None:
        return False
    try:
        proto = ctypes.WINFUNCTYPE(ctypes.c_int, ctypes.c_uint32)
        callback = proto(close_event_handler(on_close))
        KERNEL32.SetConsoleCtrlHandler.argtypes = [proto, ctypes.c_int]
        if not KERNEL32.SetConsoleCtrlHandler(callback, True):
            return False
    except (AttributeError, TypeError, OSError):
        return False
    _HANDLERS.append(callback)
    return True


def keep_awake():
    """The Windows `caffeinate -i`: no idle sleep while this process runs (ES_CONTINUOUS | ES_SYSTEM_REQUIRED; the
    display may still turn off). Returns whether it took; a no-op off Windows."""
    if KERNEL32 is None:
        return False
    try:
        KERNEL32.SetThreadExecutionState.restype = ctypes.c_uint32
        KERNEL32.SetThreadExecutionState.argtypes = [ctypes.c_uint32]
        return KERNEL32.SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED) != 0
    except AttributeError:
        return False


def _parents():
    """{pid: ppid} of every process (Toolhelp32 snapshot)."""
    snap = KERNEL32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    if not snap or snap == INVALID_HANDLE:
        return {}
    out = {}
    try:
        entry = ProcessEntry32()
        entry.dwSize = ctypes.sizeof(ProcessEntry32)
        ok = KERNEL32.Process32First(snap, ctypes.byref(entry))
        while ok:
            out[entry.th32ProcessID] = entry.th32ParentProcessID
            ok = KERNEL32.Process32Next(snap, ctypes.byref(entry))
    finally:
        KERNEL32.CloseHandle(snap)
    return out


def _process_bytes(pid):
    handle = KERNEL32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, False, pid)
    if not handle:
        handle = KERNEL32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not handle:
        return None
    try:
        pmc = ProcessMemoryCountersEx()
        pmc.cb = ctypes.sizeof(ProcessMemoryCountersEx)
        if not KERNEL32.K32GetProcessMemoryInfo(handle, ctypes.byref(pmc), pmc.cb):
            return None
        return pmc.WorkingSetSize
    finally:
        KERNEL32.CloseHandle(handle)


def process_table(only=None):
    """{pid: (ppid, bytes)}; `only`: a pid whose tree is all that is wanted (the others get 0 bytes, unread)."""
    if KERNEL32 is None:
        return {}
    parents = _parents()
    wanted = tree_pids(parents, only) if only is not None else set(parents)
    table = {}
    for pid, ppid in parents.items():
        size = _process_bytes(pid) if pid in wanted else 0
        if size is not None:
            table[pid] = (ppid, size)
    return table


def tree_pids(parents, root):
    """The pids of `root` and all its descendants in {pid: ppid}; each pid is visited once and a pid that is its own
    parent (0, the idle process) is no child. Windows keeps a dead parent's pid in its children and reuses pids, so an
    unrelated process can show up as a "descendant" of a tool whose pid it inherited: rare, and it only makes the
    watchdog read high (it kills early, never late)."""
    children = {}
    for pid, ppid in parents.items():
        if pid != ppid:
            children.setdefault(ppid, []).append(pid)
    seen, todo = set(), [root]
    while todo:
        p = todo.pop()
        if p in seen:
            continue
        seen.add(p)
        todo.extend(children.get(p, ()))
    return seen
