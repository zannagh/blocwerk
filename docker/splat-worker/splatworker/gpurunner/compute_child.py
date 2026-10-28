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
    """run_job reported an input error (its message is the HTTP job's `error`): the job fails the same way elsewhere.
    A fault of this machine (memory, disk, a broken environment) does not come as this: see _TaggedConn."""


RUNNER_FAULT = "runner"  # the third item of an ("error", message, tag) message: this machine failed, not the input
INTERNAL = "internal error (ref"  # what computejobs.child.run_in_child says for an exception nobody expected


class _Proc:
    """A multiprocessing.Process with the Popen surface memguard.Watchdog uses."""

    def __init__(self, proc):
        self.proc, self.pid = proc, proc.pid

    def poll(self):
        return None if self.proc.is_alive() else self.proc.exitcode

    def kill(self):
        self.proc.kill()


class _TaggedConn:
    """The child's end of the pipe. run_in_child reports every failure as ("error", message); an overrun of this
    machine's memory (MemoryLimitError, after the tier retries) and an unexpected exception (disk full, a broken
    environment) are the machine's, so those go out tagged RUNNER_FAULT and the job can be tried elsewhere."""

    def __init__(self, conn):
        self.conn, self.faulty = conn, False

    def send(self, msg):
        if msg[0] == "error" and (self.faulty or str(msg[1]).startswith(INTERNAL)):
            msg = (*msg, RUNNER_FAULT)
        self.conn.send(msg)

    def close(self):
        self.conn.close()


def _as_runner():
    """This (spawned) child imported the settings afresh from the environment. A 3D runner never overrides the
    quality the server asked for: gpurunner/__main__.py clears SPLAT_PROFILE_OVERRIDE in the parent, so clear it here
    too, or a prepare writes that profile into the training bundle (a draft capture trained as ultra)."""
    from ..settings import settings
    settings.profile_override = ""


def _run_job_tagged(kind, job_dir, conn):
    from .. import runner
    _as_runner()
    tagged, real = _TaggedConn(conn), runner.run_in_child

    def run_in_child(body, job_dir_, conn_, input_errors=()):
        def watched(d, progress):
            try:
                return body(d, progress)
            except MemoryLimitError:
                tagged.faulty = True
                raise
        return real(watched, job_dir_, conn_, input_errors)

    runner.run_in_child = run_in_child  # in this (spawned) process only
    return runner.run_job(kind, job_dir, tagged)


def _entry(kind, job_dir, conn):
    if os.name == "posix":  # lead a process group and take the tools down with it (what the HTTP worker does)
        from computejobs.jobs import _child_entry
        return _child_entry(_run_job_tagged, kind, job_dir, conn)
    return _run_job_tagged(kind, job_dir, conn)


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
            elif len(msg) > 2 and msg[2] == RUNNER_FAULT:
                raise JobError(stage, msg[1])  # retryable: another runner may fit or have the disk
            else:
                raise ChildFailed(msg[1])
        elif not proc.is_alive() and not parent.poll(0):
            raise JobError(stage, f"worker process died (exit code {proc.exitcode})")


def run_child(kind, job_dir, report, stop, limit_mb=0, timeout_s=3600):
    """Runs run_job(kind, job_dir) in a child; report(fraction, stage, detail). Returns (the result dict with its
    `files`, the child tree's peak memory in MB). Raises ChildFailed (the input is at fault), MemoryLimitError or
    JobError (this machine is: an overrun, a timeout, a crash, an internal error) or client.Stopped."""
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
