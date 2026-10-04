"""Wall textures on a 3D runner: the job kind `textures` (Blocwerk server: GpuJobKind.Textures).

The server's geometry worker blends the photos of a wall within 2 GB (4 GiB box); a finer quality, or a bigger wall,
would blend fewer views there. A runner with more memory (a 48 GB Mac) renders them in full instead: it downloads
the job's bundle (textures_bundle.py), runs wallgeometry's renderer on it in a child process (textures_child.py) with
a blend budget derived from the machine, and uploads the result zip: `textures.json` + the facet files, exactly what
the wall-geometry service's `textures` job produces, so the server installs it through its normal texture path.

Capability (hello): `capabilities` gains "textures" and `texturesMemoryMb` is the blend budget, when the wallgeometry
package and its libraries (numpy, scipy, OpenCV) import and the machine has the memory. Environment:
  RUNNER_TEXTURES=0                 never advertise it (the runner then trains splats only, as before)
  TEXTURES_BLEND_MAX_BYTES          the multi-view blend's budget (the service's own variable); default 50 % of the
                                    machine's memory (RUNNER_TEXTURES_MEMORY_FRACTION changes the 0.5)
  RUNNER_TEXTURES_MAX_IMAGE_MP      OpenCV's decode limit per photo (default 200 MP)
The child is killed when it passes 90 % of the machine's memory (a pause or a cancel kills it at once)."""
import importlib.util
import json
import logging
import os
import shutil
import sys
import zipfile

from .. import procs
from ..resources import system_memory
from .client import BundleTooLarge
from .job import BundleDamaged, JobRun, trim_stats, with_retries
from .textures_bundle import TexturesBundleError, extract_textures_bundle
from .textures_child import RESULT

log = logging.getLogger("gpurunner")
KIND = "textures"
CAPABILITY = "textures"
MIN_BUDGET_MB = 3072  # below this the runner has nothing over the server's own 2 GB blend
DEFAULT_FRACTION = 0.5
PROCESS_FRACTION = 0.9  # the child's own ceiling (RSS) against the machine's memory
RESULT_ZIP = "result.zip"
PROGRESS = "PROGRESS "
MODULES = ("wallgeometry.jobrender", "cv2", "scipy", "numpy")


class NotEnoughMemory(Exception):
    """The job needs more than this runner's textures budget now (handed back as a failure of this runner)."""


class TexturesInputError(Exception):
    """The job's inputs are inconsistent (a photo's size differs from its solved camera ...): no runner can fix it."""


def available():
    """Whether wallgeometry and its libraries can be imported here."""
    try:
        return all(importlib.util.find_spec(m) is not None for m in MODULES)
    except (ImportError, ValueError):
        return False


def _fraction(env):
    try:
        return min(0.9, max(0.1, float(env.get("RUNNER_TEXTURES_MEMORY_FRACTION", DEFAULT_FRACTION))))
    except ValueError:
        return DEFAULT_FRACTION


def blend_budget_mb(env=None, info=None):
    """The blend budget in MB (what TEXTURES_BLEND_MAX_BYTES is set to for the render), or None when this machine
    cannot render textures: RUNNER_TEXTURES=0, wallgeometry missing, memory unknown or under MIN_BUDGET_MB."""
    env = os.environ if env is None else env
    if env.get("RUNNER_TEXTURES", "1").strip().lower() in ("0", "false", "no", "off") or not available():
        return None
    explicit = env.get("TEXTURES_BLEND_MAX_BYTES", "").strip()
    if explicit.isdigit():
        mb = int(explicit) // (1 << 20)
    else:
        total = (info if info is not None else system_memory()).get("totalMb")
        mb = int(total * _fraction(env)) if total else 0
    return mb if mb >= MIN_BUDGET_MB else None


def advertise(env=None, info=None):
    """hello's textures part: {"capabilities": [...], "texturesMemoryMb": N}. A runner that does not render textures
    says "splat" only, with no memory (an older server ignores both keys)."""
    mb = blend_budget_mb(env, info)
    return {"capabilities": ["splat"] + (["textures"] if mb else []), "texturesMemoryMb": mb}


def _child_env():
    # the allow-listed environment of a tool run has no PYTHONPATH: hand the child this process's import path
    return {"PYTHONPATH": os.pathsep.join(p for p in sys.path if p), "PYTHONUNBUFFERED": "1"}


class TexturesRun(JobRun):
    """A claimed textures job: download -> unpack -> render in a child -> zip -> upload."""

    UPLOAD_DETAIL = "uploading the rendered textures"
    BAD_INPUT = (TexturesBundleError, TexturesInputError)
    CANNOT_HERE = (BundleTooLarge, BundleDamaged, NotEnoughMemory)

    def _produce(self):
        need, have = self.job.get("requiredMemoryMb"), blend_budget_mb()
        if have is None or (need and need > have):
            raise NotEnoughMemory(f"this runner has {have or 0} MB for textures, the job needs {need} MB")
        zip_path = os.path.join(self.dir, "bundle.zip")
        size = with_retries("download", lambda: self._download(zip_path), self.stop)
        log.info("job %s: textures bundle %.1f MB", self.id, size / 1e6)
        self.hb.set(stage="textures", fraction=0.0, detail="unpacking the photos")
        work = os.path.join(self.dir, "t")
        photos, options = extract_textures_bundle(zip_path, work)
        os.remove(zip_path)
        self.hb.set(stage="textures", fraction=0.0, detail=f"{photos} photos, blend budget {have} MB")
        self._render(work, have)
        out = os.path.join(self.dir, RESULT_ZIP)
        files = pack_result(work, out)
        shutil.rmtree(work, ignore_errors=True)
        return out, {"runnerGpu": self.caps.get("gpuName"), "runnerVersion": self.caps.get("runnerVersion"),
                     "runnerPlatform": self.caps.get("platform"), "photos": photos, "files": files,
                     "blendBudgetMb": have, **{k: v for k, v in options.items() if isinstance(v, (int, float))}}

    def _send(self, path, stats):
        return self.client.upload_result(self.id, path, trim_stats(stats), self.stop, compress=False)

    def _render(self, work, budget_mb):
        total = system_memory().get("totalMb") or 0
        max_pixels = int(float(os.environ.get("RUNNER_TEXTURES_MAX_IMAGE_MP", "200")) * 1_000_000)
        cmd = [sys.executable, "-m", "splatworker.gpurunner.textures_child", work, str(budget_mb * (1 << 20)),
               str(max_pixels)]
        procs.ToolRun("textures", cmd, os.getcwd(), os.path.join(self.dir, "textures.log"), on_line=self._on_line,
                      env=_child_env(), mem_limit_mb=int(total * PROCESS_FRACTION), name="the texture renderer",
                      stop=self.stop).run()
        with open(os.path.join(work, RESULT)) as fh:
            result = json.load(fh)
        if not result.get("ok"):
            raise TexturesInputError(result.get("error") or "the textures could not be rendered")

    def _on_line(self, line):
        if line.startswith(PROGRESS):
            parts = line[len(PROGRESS):].split(" ", 1)
            try:
                self.hb.set(stage="textures", fraction=round(float(parts[0]), 4),
                            detail=(parts[1] if len(parts) > 1 else "")[:300] or None)
            except ValueError:
                pass


def pack_result(work, out):
    """Zips the manifest and every file it names (render_job's `files`) flat into `out`; returns the file count."""
    with open(os.path.join(work, "textures.json")) as fh:
        manifest = json.load(fh)
    names = [n for f in manifest.get("facets", []) for n in (f.get("file"), f.get("maskFile"), f.get("sourceFile")) if n]
    with zipfile.ZipFile(out, "w") as zf:
        zf.write(os.path.join(work, "textures.json"), "textures.json", compress_type=zipfile.ZIP_DEFLATED)
        for name in names:
            zf.write(os.path.join(work, name), name, compress_type=zipfile.ZIP_STORED)
    return len(names) + 1
