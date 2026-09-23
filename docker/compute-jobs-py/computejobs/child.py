"""Helpers for a job body running in the child process (see jobs.py for the pipe messages)."""
import logging
import os
import re
import traceback
import uuid

log = logging.getLogger("computejobs.child")
MAX_ERROR_CHARS = 400
ABS_PATH = re.compile(r"(?<![\w.])/(?:[\w.@+-]+/)+([\w.@+-]*)")

# What external tools (COLMAP, Brush, ...) get to see of the environment: nothing else, in particular
# no COMPUTE_* secret (settings.py also scrubs those from os.environ at startup).
TOOL_ENV_NAMES = {"PATH", "HOME", "TMPDIR", "TMP", "TEMP", "LANG", "LC_ALL", "LC_CTYPE", "TZ", "USER",
                  "LOGNAME", "SHELL", "TERM", "LD_LIBRARY_PATH", "DYLD_LIBRARY_PATH",
                  "DYLD_FALLBACK_LIBRARY_PATH", "DISPLAY", "WAYLAND_DISPLAY", "RUST_LOG", "RUST_BACKTRACE",
                  "OMP_NUM_THREADS", "OPENBLAS_NUM_THREADS", "MKL_NUM_THREADS"}
TOOL_ENV_PREFIXES = ("XDG_", "VK_", "WGPU_", "NVIDIA_", "CUDA_", "MESA_", "__GLX_", "__NV_", "__EGL_",
                     "QT_", "GALLIUM_", "LIBGL_", "LP_")


def tool_env(extra=None):
    """Minimal explicit environment for a subprocess (allow-list, never the secrets)."""
    env = {k: v for k, v in os.environ.items()
           if (k in TOOL_ENV_NAMES or k.startswith(TOOL_ENV_PREFIXES)) and not k.startswith("COMPUTE_")}
    env.update(extra or {})
    return env


def safe_message(msg, job_dir=None):
    """Client-facing error text: no absolute paths (the job dir and anything else under /), bounded."""
    text = str(msg)
    if job_dir:
        text = text.replace(os.path.realpath(job_dir), "<job>").replace(job_dir, "<job>")
    text = ABS_PATH.sub(lambda m: m.group(1) or "<path>", text)
    text = " ".join(text.split())
    return text if len(text) <= MAX_ERROR_CHARS else text[:MAX_ERROR_CHARS - 1] + "…"


class JobError(Exception):
    """A failure to report as-is: `stage: message` (e.g. what the user should do differently)."""

    def __init__(self, stage, message):
        super().__init__(f"{stage}: {message}")
        self.stage, self.message = stage, message


def progress_sender(conn):
    """progress(fraction, stage, detail=None) -> sends ("progress", ...) to the parent."""
    def progress(fraction, stage, detail=None):
        conn.send(("progress", min(1.0, max(0.0, float(fraction))), str(stage),
                   None if detail is None else str(detail)))
    return progress


def run_in_child(body, job_dir, conn, input_errors=()):
    """Run body(job_dir, progress) and send exactly one ("done", result) / ("error", message).

    Unexpected exceptions are logged here in full (worker log) and reported to the client only as
    `internal error (ref <id>)`; expected ones are path-scrubbed and truncated."""
    try:
        conn.send(("done", body(job_dir, progress_sender(conn))))
    except JobError as e:
        conn.send(("error", safe_message(e, job_dir)))
    except input_errors as e:
        conn.send(("error", safe_message(f"invalid input: {e}", job_dir)))
    except Exception:  # noqa: BLE001
        ref = uuid.uuid4().hex[:8]
        log.error("job %s internal error (ref %s)\n%s", os.path.basename(job_dir), ref, traceback.format_exc())
        conn.send(("error", f"internal error (ref {ref}; details in the worker log)"))
    finally:
        conn.close()
