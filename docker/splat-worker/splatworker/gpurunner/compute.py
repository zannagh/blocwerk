"""The splat-worker's CPU half on a 3D runner: the job kinds `splat-prepare` and `splat-finish` (Blocwerk server:
GpuJobKind.SplatPrepare / SplatFinish), so the server need not run splat-cpu itself.

The runner downloads the job's bundle, a compute envelope (compute_envelope.py), unpacks it into the job directory the
HTTP upload of the same kind builds, runs the same `run_job` child under the memory guard (compute_child.py) and uploads
a zip of the finished job's `status.json` and the files it lists. A job the child reports as failed because of its input
is handed back with its message (a failure no retry fixes); a fault of this machine (memory past RUNNER_COMPUTE_MEMORY_FRACTION
or SPLAT_MAX_MEMORY_MB, a full disk, a broken environment, a timeout) is a failure another runner may do better.

Capabilities (hello): `capabilities` gains
  "splat-prepare"  when COLMAP is here (COLMAP_BIN) and RUNNER_SPLAT_CPU is not 0
  "splat-finish"   when the package imports
Environment:
  RUNNER_SPLAT_CPU=0               never advertise splat-prepare (COLMAP is the heavy half: this runner only trains)
  RUNNER_COMPUTE_MEMORY_FRACTION   the child's ceiling (RSS of its whole tree) against the machine's memory; default 0.9
plus the splat-worker's own variables (SPLAT_MAX_MEMORY_MB, COLMAP_*, SPLAT_TIMEOUT_S ...), which the child reads."""
import importlib
import logging
import os
import shutil
import time

from ..resources import system_memory
from ..settings import settings
from .client import BundleTooLarge
from .compute_child import ChildFailed, run_child
from .compute_envelope import EnvelopeError, now_iso, pack_result, status_doc, unpack
from .job import BundleDamaged, JobRun, trim_stats, with_retries
from .textures import NotEnoughMemory

log = logging.getLogger("gpurunner")
PREPARE, FINISH = "splat-prepare", "splat-finish"
KINDS = (PREPARE, FINISH)
RESULT_ZIP = "result.zip"
DEFAULT_FRACTION = 0.9


def _importable(module):
    try:
        importlib.import_module(module)
        return True
    except ImportError:
        return False


def advertise(env=None):
    """hello's part: the compute capabilities this runner has (a subset of KINDS)."""
    env = os.environ if env is None else env
    cpu_off = env.get("RUNNER_SPLAT_CPU", "1").strip().lower() in ("0", "false", "no", "off")
    found = []
    if not cpu_off and shutil.which(env.get("COLMAP_BIN", settings.colmap_bin)) and _importable("splatworker.prepare"):
        found.append(PREPARE)
    if _importable("splatworker.finish"):
        found.append(FINISH)
    return found


def _limit_mb(env=None):
    env = os.environ if env is None else env
    try:
        fraction = min(1.0, max(0.1, float(env.get("RUNNER_COMPUTE_MEMORY_FRACTION", DEFAULT_FRACTION))))
    except ValueError:
        fraction = DEFAULT_FRACTION
    return int((system_memory().get("totalMb") or 0) * fraction)


class ComputeRun(JobRun):
    """A claimed splat-prepare / splat-finish job: download -> unpack -> run_job in a child -> zip -> upload."""

    UPLOAD_DETAIL = "uploading the result"
    BAD_INPUT = (EnvelopeError, ChildFailed)
    CANNOT_HERE = (BundleTooLarge, BundleDamaged, NotEnoughMemory)

    def _produce(self):
        kind, need, have = self.job["kind"], self.job.get("requiredMemoryMb"), self.caps.get("memoryBudgetMb")
        if need and have is not None and need > have:
            raise NotEnoughMemory(f"this runner has {have} MB for {kind}, the job needs {need} MB")
        created = now_iso()
        zip_path = os.path.join(self.dir, "bundle.zip")
        size = with_retries("download", lambda: self._download(zip_path), self.stop)
        log.info("job %s: %s envelope %.1f MB", self.id, kind, size / 1e6)
        self.hb.set(stage="unpack", fraction=0.0, detail="unpacking the bundle")
        work = os.path.join(self.dir, "c")
        photos = unpack(zip_path, work, kind)
        os.remove(zip_path)
        t0 = time.monotonic()
        payload, peak = self._child(kind, work)
        status = status_doc(self.id, kind, payload, created)
        out = os.path.join(self.dir, RESULT_ZIP)
        files = pack_result(work, status, out)
        shutil.rmtree(work, ignore_errors=True)
        return out, {"runnerGpu": self.caps.get("gpuName"), "runnerVersion": self.caps.get("runnerVersion"),
                     "runnerPlatform": self.caps.get("platform"), "kind": kind, "photos": photos, "files": files,
                     "seconds": round(time.monotonic() - t0, 1), "peakMemoryMb": peak}

    def _child(self, kind, work):
        return run_child(kind, work, self._report, self.stop, _limit_mb(), settings.splat_timeout_s)

    def _report(self, fraction, stage, detail=None):
        self.hb.set(stage=str(stage), fraction=round(float(fraction), 4), detail=(detail or "")[:300] or None)

    def _send(self, path, stats):
        return self.client.upload_result(self.id, path, trim_stats(stats), self.stop, compress=False)
