"""Hold detection on a 3D runner: the job kind `detect-holds` (Blocwerk server: GpuJobKind.DetectHolds).

After a capture the server searches every capture photo for holds it does not know yet (the hold proposals). On the
server that is the app's YOLO detector on the CPU, photo after photo (34 minutes for 425 photos); a runner that
advertises `detect-holds` does it instead: it downloads the job's bundle (`holds-job.json` with the app's detector
settings, `model.onnx` = the app's own model, `photos/<camera>.jpg|png`), runs the same tiled detection on ONNX Runtime
(holds_detect.py) and uploads one JSON document, {"version": 1, "photos": {camera: {"width", "height", "boxes":
[[left, top, right, bottom, confidence, label], ...]}}}. A photo it cannot read is left out (the server detects it).
The server waits a while for a runner to claim the job and otherwise detects in the app, so a runner without
ONNX Runtime (or an older runner) simply never advertises the kind and nothing changes.

Environment:
  RUNNER_HOLDS=0                 never advertise it
  RUNNER_HOLDS_THREADS           ONNX Runtime's intra-op threads (default: its own choice, every core)
  RUNNER_HOLDS_MAX_IMAGE_MP      the largest photo it decodes (default 200 MP)"""
import ast
import importlib.util
import io
import json
import logging
import os
import re
import zipfile

from .client import BundleTooLarge
from .holds_detect import Detector
from .job import BundleDamaged, JobAbandoned, JobRun, with_retries

log = logging.getLogger("gpurunner")
KIND = CAPABILITY = "detect-holds"
JOB_FILE, MODEL_FILE = "holds-job.json", "model.onnx"
PHOTO = re.compile(r"^photos/([A-Za-z0-9_.\-]{1,100})\.(jpg|jpeg|png)$")
MAX_ENTRIES = 20000
MAX_BYTES = 64 << 30
MAX_MODEL_BYTES = 1 << 30
MAX_DOC_BYTES = 1 << 20
MODULES = ("onnxruntime", "numpy", "PIL")
RESULT = "holds.json"


class HoldsBundleError(Exception):
    """The bundle is not a hold detection bundle (reported as a failure that no retry fixes)."""


def available():
    """Whether ONNX Runtime, numpy and Pillow can be imported here."""
    try:
        return all(importlib.util.find_spec(m) is not None for m in MODULES)
    except (ImportError, ValueError):
        return False


def advertise(env=None):
    """hello's part: ["detect-holds"] when this runner can find holds, else []."""
    env = os.environ if env is None else env
    off = env.get("RUNNER_HOLDS", "1").strip().lower() in ("0", "false", "no", "off")
    return [] if off or not available() else [CAPABILITY]


def read_bundle(zf):
    """Validates the open bundle; returns (job document, model bytes, [(camera name, ZipInfo)])."""
    infos = [i for i in zf.infolist() if not i.is_dir()]
    if len(infos) > MAX_ENTRIES:
        raise HoldsBundleError(f"the bundle has more than {MAX_ENTRIES} entries")
    names = {i.filename: i for i in infos}
    if JOB_FILE not in names or MODEL_FILE not in names:
        raise HoldsBundleError(f"the bundle lacks {JOB_FILE} or {MODEL_FILE}")
    photos, total = [], 0
    for info in infos:
        total += info.file_size
        if total > MAX_BYTES:
            raise HoldsBundleError("the bundle is too large")
        if info.filename == JOB_FILE and info.file_size > MAX_DOC_BYTES or \
                info.filename == MODEL_FILE and info.file_size > MAX_MODEL_BYTES:
            raise HoldsBundleError(f"{info.filename} is too large")
        if info.filename in (JOB_FILE, MODEL_FILE):
            continue
        if (m := PHOTO.match(info.filename)) is None or ".." in info.filename:
            raise HoldsBundleError(f"the bundle has an unexpected file: {info.filename[:80]}")
        photos.append((m.group(1), info))
    job = json.loads(zf.read(JOB_FILE))
    if not isinstance(job, dict) or job.get("version") != 1:
        raise HoldsBundleError("the bundle's job document is not version 1")
    if not photos:
        raise HoldsBundleError("the bundle has no photos")
    return job, zf.read(MODEL_FILE), photos


def model_labels(session):
    """The class names the model was exported with (Ultralytics' `names` metadata, "{0: 'hold', 1: 'volume'}")."""
    names = session.get_modelmeta().custom_metadata_map.get("names")
    try:
        parsed = ast.literal_eval(names) if names else None
    except (ValueError, SyntaxError):
        parsed = None
    if not isinstance(parsed, dict) or not parsed:
        raise HoldsBundleError("the model names no classes")
    return [str(parsed[k]) for k in sorted(parsed)]


def open_session(model, env=None):
    """An ONNX Runtime session on the CPU (the app's detector runs there too, so the numbers match)."""
    import onnxruntime as ort
    env = os.environ if env is None else env
    opts = ort.SessionOptions()
    threads = env.get("RUNNER_HOLDS_THREADS", "").strip()
    if threads.isdigit() and int(threads) > 0:
        opts.intra_op_num_threads = int(threads)
    return ort.InferenceSession(model, sess_options=opts, providers=["CPUExecutionProvider"])


def decode_photo(data, max_pixels):
    """The photo as an RGB array (no EXIF rotation: the raw pixel grid the server solved), or None when unreadable."""
    import numpy as np
    from PIL import Image
    Image.MAX_IMAGE_PIXELS = max_pixels
    try:
        with Image.open(io.BytesIO(data)) as im:
            return np.asarray(im.convert("RGB"))
    except (OSError, ValueError, Image.DecompressionBombError) as e:
        log.warning("a photo could not be read (%s); the server detects it itself", e)
        return None


def detect_bundle(zip_path, report, stop, env=None):
    """Runs the detector over every photo of the bundle; returns the result document's `photos`."""
    env = os.environ if env is None else env
    max_pixels = int(float(env.get("RUNNER_HOLDS_MAX_IMAGE_MP", "200")) * 1_000_000)
    try:
        with zipfile.ZipFile(zip_path) as zf:
            job, model, photos = read_bundle(zf)
            session = open_session(model, env)
            shape = session.get_inputs()[0].shape
            size = int(shape[2]) if len(shape) == 4 and isinstance(shape[2], int) else 640
            detector = Detector(session, model_labels(session), size, job)
            del model
            found = {}
            for n, (name, info) in enumerate(photos):
                if stop.is_set():
                    raise JobAbandoned("stopped")
                report(n / len(photos), f"photo {n + 1}/{len(photos)}")
                rgb = decode_photo(zf.read(info), max_pixels)
                if rgb is not None:
                    found[name] = {"width": int(rgb.shape[1]), "height": int(rgb.shape[0]),
                                   "boxes": [[int(b[0]), int(b[1]), int(b[2]), int(b[3]), round(b[4], 6), b[5]]
                                             for b in detector.detect(rgb)]}
            return found
    except (zipfile.BadZipFile, ValueError, KeyError) as e:
        raise HoldsBundleError(f"bad hold detection bundle: {e}") from e


class HoldsRun(JobRun):
    """A claimed hold detection job: download -> detect photo by photo -> upload the JSON (gzip)."""

    UPLOAD_DETAIL = "uploading the holds it found"
    BAD_INPUT = (HoldsBundleError,)
    CANNOT_HERE = (BundleTooLarge, BundleDamaged)

    def _produce(self):
        zip_path = os.path.join(self.dir, "bundle.zip")
        size = with_retries("download", lambda: self._download(zip_path), self.stop)
        log.info("job %s: hold detection bundle %.1f MB", self.id, size / 1e6)
        photos = detect_bundle(zip_path, self._progress, self.stop)
        os.remove(zip_path)
        out = os.path.join(self.dir, RESULT)
        with open(out, "w") as fh:
            json.dump({"version": 1, "photos": photos}, fh, separators=(",", ":"))
        return out, {"runnerVersion": self.caps.get("runnerVersion"), "runnerPlatform": self.caps.get("platform"),
                     "photos": len(photos), "holds": sum(len(p["boxes"]) for p in photos.values())}

    def _progress(self, fraction, detail):
        self.hb.set(stage="detect", fraction=round(float(fraction), 4), detail=detail)
