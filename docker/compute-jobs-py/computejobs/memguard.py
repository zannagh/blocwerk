"""Per-job memory guard (JOB_MAX_MEMORY_MB): a job that outgrows the server's memory ends as a *resource* failure.

The parent samples the resident memory of the job's whole process group (the child and every tool it started) while
it waits (jobs.py) and kills the job above the limit. A spike between two samples can still hit the container's own
limit first; the child then volunteers as the kernel's OOM victim (oom_score_adj), so the server survives and the
SIGKILLed job is reported the same way (when the cgroup's OOM kill count says it was the kernel). A resource failure is not the input's fault: the client may run it on a machine
with more memory (`failureKind: "resource"`, compute-jobs-protocol.md).
"""
import os
import signal

RESOURCE = "resource"
SAMPLE_S = 0.25  # how often the parent samples a running job
_PAGE = os.sysconf("SC_PAGE_SIZE") if hasattr(os, "sysconf") else 4096


def volunteer_for_oom():
    """In the child: let the kernel's OOM killer pick this job (and what it starts) before the server."""
    try:
        with open("/proc/self/oom_score_adj", "w") as fh:
            fh.write("1000")
    except OSError:
        pass


def group_rss_bytes(pgid):
    """Resident bytes of every process in process group `pgid`; None where /proc is not available (macOS, Windows)."""
    try:
        pids = [p for p in os.listdir("/proc") if p.isdigit()]
    except OSError:
        return None
    total = 0
    for pid in pids:
        try:
            with open(f"/proc/{pid}/stat") as fh:
                # the 5th field after the ")" that closes the command name is the process group
                fields = fh.read().rsplit(")", 1)[1].split()
            if int(fields[2]) != pgid:
                continue
            with open(f"/proc/{pid}/statm") as fh:
                total += int(fh.read().split()[1]) * _PAGE
        except (OSError, IndexError, ValueError):
            continue  # gone meanwhile
    return total


def over_limit(pgid, limit_mb):
    """The group's resident MB when it exceeds `limit_mb` (> 0), else None."""
    if limit_mb <= 0:
        return None
    rss = group_rss_bytes(pgid)
    return rss >> 20 if rss is not None and rss > limit_mb << 20 else None


def oom_kills():
    """How often the kernel's OOM killer struck in this container's cgroup (v2 memory.events, v1 oom_control); None unknown."""
    for path in ("/sys/fs/cgroup/memory.events", "/sys/fs/cgroup/memory/memory.oom_control"):
        try:
            with open(path) as fh:
                for line in fh:
                    key, _, value = line.partition(" ")
                    if key == "oom_kill":
                        return int(value)
        except (OSError, ValueError):
            continue
    return None


def killed_by_oom(exitcode, kills_before):
    """A child SIGKILLed while the cgroup's OOM kill count went up: the container's memory limit, not a crash."""
    if exitcode != -getattr(signal, "SIGKILL", 9) or kills_before is None:  # no SIGKILL on Windows
        return False
    now = oom_kills()
    return now is not None and now > kills_before


def message(stage, limit_mb):
    """limit_mb: the guard's limit, or 0 when the container's own limit ended the job."""
    limit = f" ({limit_mb} MB)" if limit_mb else ""
    return (f"{stage}: needs more memory than this server allows for one job{limit}; "
            "it can run on a machine with more memory")
