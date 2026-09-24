"""Run an external tool, feeding its output line by line to a parser and into a log file.

`use_pty=True` gives the tool a pseudo-terminal: Brush only prints its step counter to a TTY (with a
plain pipe it prints nothing at all). Carriage-return redraws are split into separate lines and ANSI
escapes are stripped. The job's process group is killed by the parent on cancel/timeout, which takes
these tools down with it.
"""
import os
import re
import subprocess
import sys
import threading
from collections import deque

from computejobs.child import JobError, tool_env

from .memguard import OOM_TEXT, Watchdog, address_space_limiter, describe_mb
from .resources import SwapMeter

ANSI = re.compile(rb"\x1b\[[0-9;?]*[ -/]*[@-~]|\x1b[@-_]")
SPLIT = re.compile(rb"[\r\n]+")
_SWAP = SwapMeter()


class MemoryLimitError(JobError):
    """The memory guard stopped the tool: kind "memory" (its footprint) or "swap" (system swap growth).
    The pipeline retries such a step on a cheaper tier."""

    def __init__(self, stage, message, kind):
        super().__init__(stage, message)
        self.kind = kind


class ToolStopped(JobError):
    """The tool was killed on purpose (ToolRun's stop event: a cancelled runner job)."""


class ToolRun:
    def __init__(self, stage, cmd, cwd, log_path, on_line=None, use_pty=False, env=None, log_filter=None,
                 mem_limit_mb=0, limit_address_space=False, name=None, swap_limit_mb=0, stop=None):
        self.stage, self.cmd, self.cwd, self.log_path = stage, cmd, cwd, log_path
        # mem_limit_mb: RSS ceiling (watchdog kill); limit_address_space: also RLIMIT_AS on Linux (not
        # for Brush: GPU drivers reserve huge virtual ranges). name: the tool as users know it.
        self.mem_limit_mb, self.limit_as, self.swap_limit_mb = mem_limit_mb, limit_address_space, swap_limit_mb
        self.name = name or os.path.basename(cmd[0])
        self.watchdog = None
        self.stop = stop  # threading.Event: kill the tool when set (a cancelled runner job)
        self.on_line, self.use_pty, self.env = on_line, use_pty, env
        self.log_filter = log_filter  # line -> bool: whether it goes into the log file (all lines are parsed)
        self.tail, self.recent, self.last = deque(maxlen=40), deque(maxlen=12), None

    def _emit(self, raw, log):
        line = ANSI.sub(b"", raw).decode("utf-8", "replace").strip()
        if not line:
            return
        if self.on_line and line != self.last:
            self.on_line(line)
        self.last = line
        # TTY redraws repeat the same few lines over and over: log each only once per redraw window
        if line in self.recent or (self.log_filter is not None and not self.log_filter(line)):
            return
        self.recent.append(line)
        log.write(line + "\n")
        self.tail.append(line)

    def _pump(self, read, log):
        buf = b""
        while True:
            try:
                chunk = read()
            except OSError:  # EIO on the pty master once the child side closes (Linux)
                chunk = b""
            if not chunk:
                break
            buf += chunk
            *lines, buf = SPLIT.split(buf)
            for ln in lines:
                self._emit(ln, log)
        if buf:
            self._emit(buf, log)

    def _start(self, stdout, stderr):
        preexec = address_space_limiter(self.mem_limit_mb) if self.limit_as else None
        proc = subprocess.Popen(self.cmd, cwd=self.cwd, stdin=subprocess.DEVNULL, stdout=stdout, stderr=stderr,
                                env=tool_env(self.env), close_fds=True, preexec_fn=preexec)
        if self.mem_limit_mb:
            self.watchdog = Watchdog(proc, self.mem_limit_mb, swap_limit_mb=self.swap_limit_mb, swap_meter=_SWAP)
            self.watchdog.start()
        if self.stop is not None:
            threading.Thread(target=self._stop_watch, args=(proc,), daemon=True).start()
        return proc

    def _stop_watch(self, proc):
        while proc.poll() is None:
            if self.stop.wait(0.5):
                proc.kill()
                return

    def run(self):
        """Run to completion; raise JobError(stage, ...) on a non-zero exit."""
        try:
            with open(self.log_path, "a", buffering=1) as log:
                log.write("$ " + " ".join(self.cmd) + "\n")
                try:
                    if self.use_pty:
                        import pty
                        master, slave = pty.openpty()
                        proc = self._start(slave, slave)
                        os.close(slave)
                        try:
                            self._pump(lambda: os.read(master, 65536), log)
                        finally:
                            os.close(master)
                    else:
                        proc = self._start(subprocess.PIPE, subprocess.STDOUT)
                        self._pump(lambda: proc.stdout.read1(65536), log)
                    code = proc.wait()
                finally:
                    if self.watchdog:
                        self.watchdog.stop()
                        log.write(f"# peak memory {self.watchdog.peak_mb} MB (limit {self.mem_limit_mb} MB)"
                                  f"{self._swap_note()}\n")
        except FileNotFoundError as e:
            raise JobError(self.stage, f"tool not found: {os.path.basename(self.cmd[0])} "
                                       "(set BRUSH_BIN / COLMAP_BIN)") from e
        if code != 0 and self.stop is not None and self.stop.is_set():
            raise ToolStopped(self.stage, f"{self.name} was stopped (cancelled)")
        if code != 0:
            kind = self._out_of_memory()
            if kind == "swap":
                raise MemoryLimitError(self.stage, f"{self.name} was stopped: the machine's swap grew by "
                                                   f"{describe_mb(self.watchdog.swap_growth_mb)} within "
                                                   f"{self.watchdog.SWAP_WINDOW_S:.0f} s while it ran (limit "
                                                   f"{describe_mb(self.swap_limit_mb)}); free memory or try fewer "
                                                   "or smaller photos", kind)
            if kind:
                raise MemoryLimitError(self.stage, f"{self.name} exceeded the {describe_mb(self.mem_limit_mb)} "
                                                   "memory limit; try fewer or smaller photos (or raise "
                                                   "SPLAT_MAX_MEMORY_MB)", kind)
            raise JobError(self.stage, f"{os.path.basename(self.cmd[0])} {self._verb()} failed "
                                       f"(exit code {code}): {self.last_error()}")
        return self

    def _swap_note(self):
        if not self.swap_limit_mb or self.watchdog.swap_base is None:
            return ""
        return (f"; swap +{self.watchdog.swap_growth_mb} MB at most per {self.watchdog.SWAP_WINDOW_S:.0f} s "
                f"(limit {self.swap_limit_mb} MB)")

    def _out_of_memory(self):
        """None, or why the tool ran out: "memory" / "swap" (watchdog), "memory" (RLIMIT_AS)."""
        if self.watchdog and self.watchdog.tripped:
            return self.watchdog.tripped
        # RLIMIT_AS hit: the allocation fails inside the tool (std::bad_alloc -> abort)
        if self.limit_as and self.mem_limit_mb and sys.platform.startswith("linux") and \
                any(t in line.lower() for line in self.tail for t in OOM_TEXT):
            return "memory"
        return None

    def _verb(self):
        return self.cmd[1] if len(self.cmd) > 1 and not self.cmd[1].startswith(("/", "-")) else ""

    def last_error(self):
        """The most telling recent line: an error/check line if there is one, else the last line."""
        for line in reversed(self.tail):
            if re.search(r"error|fail|check failed|panick|abort|vulkan|adapter", line, re.I):
                return line[:300]
        return self.tail[-1][:300] if self.tail else "no output"
