"""The child process of a `splat-prepare` / `splat-finish` job: the splat-worker's own `run_job(kind, job_dir, conn)`
(runner.py) in a spawned process, exactly as computejobs.jobs.JobManager runs it for the HTTP API (own process group,
progress and the outcome over a Pipe), plus the runner's stop event and a memory watchdog over the child's whole tree
(memguard.Watchdog; COLMAP and the like are guarded inside it as always). A child, so a pause, a cancel or an overrun
kills it and everything it started."""
import multiprocessing as mp
import os
import time

from computejobs.child import JobError

from ..memguard import Watchdog
from ..procs import MemoryLimitError
from .client import Stopped

POLL_S = 0.5


class ChildFailed(Exception):
    """run_job reported an error (its message is the HTTP job's `error`): the job fails the same way elsewhere."""


class _Proc:
    """A multiprocessing.Process with the Popen surface memguard.Watchdog uses."""

    def __init__(self, proc):
        self.proc, self.pid = proc, proc.pid

    def poll(self):
        return None if self.proc.is_alive() else self.proc.exitcode

    def kill(self):
        self.proc.kill()


def _entry(kind, job_dir, conn):
    from ..runner import run_job
    if os.name == "posix":  # lead a process group and take the tools down with it (what the HTTP worker does)
        from computejobs.jobs import _child_entry
        return _child_entry(run_job, kind, job_dir, conn)
    return run_job(kind, job_dir, conn)


def _kill_tree(proc):
    if os.name == "posix":
        from computejobs.jobs import _kill_tree as kill
        return kill(proc)
    proc.kill()
    proc.join(5)


def _outcome(parent, proc, guard, stop, timeout_s, report):
    """The ("done", result) payload of the child; raises for an error, a stop, a timeout or an overrun."""
    stage, t0 = "starting", time.monotonic()
    while True:
        if stop.is_set():
            raise Stopped("stopped")
        if guard is not None and guard.tripped:
            raise MemoryLimitError(stage, f"the job needs more memory than this runner allows ({guard.peak_mb} MB)",
                                   guard.tripped)
        if time.monotonic() - t0 > timeout_s:
            raise JobError(stage, f"timed out after {timeout_s} s")
        if parent.poll(POLL_S):
            try:
                msg = parent.recv()
            except EOFError:
                raise JobError(stage, f"worker process died (exit code {proc.exitcode})") from None
            if msg[0] == "progress":
                stage = msg[2]
                report(msg[1], stage, msg[3] if len(msg) > 3 else None)
            elif msg[0] == "done":
                return msg[1]
            else:
                raise ChildFailed(msg[1])
        elif not proc.is_alive() and not parent.poll(0):
            raise JobError(stage, f"worker process died (exit code {proc.exitcode})")


def run_child(kind, job_dir, report, stop, limit_mb=0, timeout_s=3600):
    """Runs run_job(kind, job_dir) in a child; report(fraction, stage, detail). Returns (the result dict with its
    `files`, the child tree's peak memory in MB). Raises ChildFailed, MemoryLimitError, JobError (timeout, crash)
    or client.Stopped."""
    ctx = mp.get_context("spawn")
    parent, child = ctx.Pipe(duplex=False)
    proc = ctx.Process(target=_entry, args=(kind, job_dir, child), daemon=True)
    proc.start()
    child.close()
    guard = Watchdog(_Proc(proc), limit_mb) if limit_mb else None
    if guard is not None:
        guard.start()
    try:
        return _outcome(parent, proc, guard, stop, timeout_s, report), (guard.peak_mb if guard else None)
    finally:
        if guard is not None:
            guard.done.set()
        _kill_tree(proc)
        parent.close()
