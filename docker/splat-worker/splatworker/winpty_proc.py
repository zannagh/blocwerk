"""A pseudo-console for a tool on Windows (procs.ToolRun use_pty=True): Brush prints its step counter only to a
terminal, and Python's `pty` module is POSIX-only. pywinpty (requirements-windows.txt, put in by
run-runner-native.ps1) gives the tool a ConPTY; WinPtyTool wraps its PtyProcess in the few Popen methods ToolRun and
the memory watchdog use (pid, poll, wait, kill).

Without pywinpty (or with RUNNER_WINPTY=0) procs.py falls back to a plain pipe: the training still runs and its
result is still found, but the progress bar stays at the start until Brush exits (heartbeats keep the lease)."""
import os
import time

COLUMNS = 400  # wide: a step line must not wrap into two
ROWS = 50
READ_TIMEOUT_S = 2.0


def available(env=None):
    env = os.environ if env is None else env
    if env.get("RUNNER_WINPTY", "1").strip().lower() in ("0", "false", "no", "off"):
        return False
    try:
        import winpty  # noqa: F401
    except ImportError:
        return False
    return True


class WinPtyTool:
    """A running tool behind a ConPTY. read() returns bytes (b"" at the end) like os.read on a POSIX pty master."""

    def __init__(self, cmd, cwd, env):
        from winpty import PtyProcess
        self.proc = PtyProcess.spawn(list(cmd), cwd=cwd, env=env, dimensions=(ROWS, COLUMNS))
        self.pid = self.proc.pid
        try:  # pywinpty reads through a local socket: never block on it forever should its EOF not come
            self.proc.fileobj.settimeout(READ_TIMEOUT_S)
        except AttributeError:
            pass

    def read(self, size=65536, idle_s=0.05):
        """The next output; b"" only once the tool is gone (an empty read while it lives is waited out)."""
        while True:
            try:
                text = self.proc.read(size)
            except TimeoutError:
                if self.proc.isalive():
                    continue
                return b""
            except (EOFError, OSError):
                return b""
            if text:
                return text.encode("utf-8", "replace") if isinstance(text, str) else bytes(text)
            if not self.proc.isalive():
                return b""
            time.sleep(idle_s)

    def poll(self):
        return None if self.proc.isalive() else self._code()

    def _code(self):
        code = self.proc.exitstatus
        return 1 if code is None else code

    def wait(self, poll_s=0.2):
        while self.proc.isalive():
            time.sleep(poll_s)
        return self._code()

    def kill(self):
        try:
            self.proc.terminate(force=True)
        except (OSError, EOFError):
            pass

    def close(self):
        try:
            self.proc.close(force=True)
        except (OSError, EOFError, AttributeError):
            pass
