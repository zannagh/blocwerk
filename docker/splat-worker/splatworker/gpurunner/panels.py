"""Panel photo registration on a 3D runner: the job kind `register-panels` (Blocwerk server: GpuJobKind.RegisterPanels).

A wall's panel photos (the 2D hold overlays are drawn on them) have no camera pose. To place their holds on the photo-real
surface the server needs one, in the wall-geometry world. The runner downloads the job's bundle:
  register-job.json   {"version": 1, "cameras": [{"name", "width", "height", "K", "dist", "R", "t"}, ...], "panels": [name, ...]}
                      the capture's solved cameras (world mm, world-to-camera R|t, OpenCV k1, k2 distortion)
  photos/<camera>.jpg the capture photos, panels/<panel>.jpg the panel photos
It runs COLMAP (panels_colmap.py): SIFT, matching (capture neighbours + every panel against every photo), triangulation
with the capture poses FIXED, then registers the panel photos into that model. It uploads one JSON document:
  {"version": 1, "panels": {name: {"width", "height", "params": [f, cx, cy, k1, k2], "R": [9], "t": [3],
                                   "observations", "rmsPx", "medianPx"}}}
A panel COLMAP could not register is left out. Environment: RUNNER_PANELS=0 never advertises it; COLMAP_BIN the binary."""
import importlib.util
import json
import logging
import os
import re
import shutil
import zipfile

from .client import BundleTooLarge
from .job import BundleDamaged, JobRun, with_retries
from .panels_colmap import PanelsInputError, register_panels

log = logging.getLogger("gpurunner")
KIND = CAPABILITY = "register-panels"
JOB_FILE = "register-job.json"
ENTRY = re.compile(r"^(photos|panels)/([A-Za-z0-9_\-]{1,100})\.(jpg|jpeg|png)$")
MAX_ENTRIES = 20000
MAX_BYTES = 64 << 30
MAX_DOC_BYTES = 8 << 20
RESULT = "panels.json"


def available(env=None):
    """Whether COLMAP and numpy are here."""
    env = os.environ if env is None else env
    try:
        numpy = importlib.util.find_spec("numpy") is not None
    except (ImportError, ValueError):
        numpy = False
    return numpy and shutil.which(env.get("COLMAP_BIN", "colmap")) is not None


def advertise(env=None):
    """hello's part: ["register-panels"] when this runner can register panel photos, else []."""
    env = os.environ if env is None else env
    off = env.get("RUNNER_PANELS", "1").strip().lower() in ("0", "false", "no", "off")
    return [] if off or not available(env) else [CAPABILITY]


def read_bundle(zf):
    """Validates the open bundle; returns (job document, {"photos": {name: info}, "panels": {name: info}})."""
    infos = [i for i in zf.infolist() if not i.is_dir()]
    if len(infos) > MAX_ENTRIES:
        raise PanelsInputError(f"the bundle has more than {MAX_ENTRIES} entries")
    if sum(i.file_size for i in infos) > MAX_BYTES:
        raise PanelsInputError("the bundle is too large")
    names = {i.filename: i for i in infos}
    if JOB_FILE not in names or names[JOB_FILE].file_size > MAX_DOC_BYTES:
        raise PanelsInputError(f"the bundle lacks {JOB_FILE} or it is too large")
    files = {"photos": {}, "panels": {}}
    for info in infos:
        if info.filename == JOB_FILE:
            continue
        m = ENTRY.match(info.filename)
        if m is None:
            raise PanelsInputError(f"the bundle has an unexpected file: {info.filename[:80]}")
        files[m.group(1)][m.group(2)] = info
    job = json.loads(zf.read(JOB_FILE))
    if not isinstance(job, dict) or job.get("version") != 1:
        raise PanelsInputError("the bundle's job document is not version 1")
    cameras = [c for c in job.get("cameras") or [] if c.get("name") in files["photos"]]
    if len(cameras) < 2 or not files["panels"]:
        raise PanelsInputError("the bundle needs at least two posed photos and one panel photo")
    job["cameras"] = cameras
    return job, files


def extract(zip_path, out_dir):
    """Unpacks photos/ and panels/ under out_dir/images; returns the job document and the panel names."""
    try:
        with zipfile.ZipFile(zip_path) as zf:
            job, files = read_bundle(zf)
            for kind, entries in files.items():
                os.makedirs(os.path.join(out_dir, "images", kind), exist_ok=True)
                for name, info in entries.items():
                    ext = os.path.splitext(info.filename)[1]
                    with zf.open(info) as src, open(os.path.join(out_dir, "images", kind, name + ext), "wb") as dst:
                        shutil.copyfileobj(src, dst)
    except (zipfile.BadZipFile, ValueError, KeyError) as e:
        raise PanelsInputError(f"bad panel registration bundle: {e}") from e
    return job, sorted(files["panels"])


class PanelsRun(JobRun):
    """A claimed panel registration job: download -> COLMAP -> upload the poses (JSON)."""

    UPLOAD_DETAIL = "uploading the panel poses"
    BAD_INPUT = (PanelsInputError,)
    CANNOT_HERE = (BundleTooLarge, BundleDamaged)

    def _produce(self):
        zip_path = os.path.join(self.dir, "bundle.zip")
        size = with_retries("download", lambda: self._download(zip_path), self.stop)
        log.info("job %s: panel registration bundle %.1f MB", self.id, size / 1e6)
        work = os.path.join(self.dir, "w")
        job, panels = extract(zip_path, work)
        os.remove(zip_path)
        poses = register_panels(work, job, panels, self._progress, self.stop)
        out = os.path.join(self.dir, RESULT)
        with open(out, "w") as fh:
            json.dump({"version": 1, "panels": poses}, fh, separators=(",", ":"))
        shutil.rmtree(work, ignore_errors=True)
        log.info("job %s: registered %d of %d panel photos", self.id, len(poses), len(panels))
        return out, {"runnerVersion": self.caps.get("runnerVersion"), "runnerPlatform": self.caps.get("platform"),
                     "panels": len(panels), "registered": len(poses)}

    def _progress(self, fraction, detail):
        self.hb.set(stage="register", fraction=round(float(fraction), 4), detail=detail)
