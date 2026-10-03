"""The training bundle a 3D runner gets (kind `splat-prepare` builds it, gpurunner unpacks it):

  dataset/images/<group>/<stem>.jpg   the undistorted images (metadata-free: no APPn but JFIF, no COM)
  dataset/sparse/0/*.bin              COLMAP's undistorted sparse model (the layout Brush / gsplat expect)
  train.json                          the quality profile to train (train_doc / profile_from_doc)
  zones.json                          optional, only when the job opted in: the wall zones (zones.spec +
                                      toWorldMm) for gsplat's wall-focused training, as the all-in-one job
                                      writes them (params.wallZones marks the opt-in, zones_opted_in)

Nothing else: no original file names (stems only), no geometry document, no GPS, no photo metadata.
The server rebuilds the zip with the same allow-list (Blocwerk.Core RunnerBundle) before a runner sees it.
"""
import json
import os
import stat
import struct
import zipfile
from dataclasses import fields

from computejobs.child import JobError

from .profiles import QUALITIES, Profile
from .settings import settings

TRAIN_DOC_VERSION = 1
IMAGE_EXT = (".jpg", ".jpeg")
ZONES_FILE = "zones.json"
MAX_ZONES_BYTES = 1 << 20
OPT_IN = "wallZones"  # zones.json params flag: the job opted into the wall zones (zones_opted_in)
# train.json key <-> Profile field (the server's RunnerTrainOptions.AllowedKeys)
PROFILE_KEYS = {"quality": "name", "edge": "edge", "frameEdge": "frame_edge", "minEdge": "min_edge",
                "steps": "steps", "maxSplats": "max_splats", "minSplats": "min_splats",
                "growthStop": "growth_stop", "refineEvery": "refine_every",
                "growthSelectFraction": "growth_select_fraction", "shDegree": "sh_degree",
                "checkpoints": "checkpoints"}
MAX_ENTRIES = 20000


class BundleError(ValueError):
    """A bundle that must not be trained (bad layout, unsafe path, too large, unknown profile)."""


def train_doc(profile):
    return {"version": TRAIN_DOC_VERSION, **{k: getattr(profile, f) for k, f in PROFILE_KEYS.items()}}


def profile_from_doc(doc):
    if not isinstance(doc, dict) or doc.get("version") != TRAIN_DOC_VERSION:
        raise BundleError("train.json: unsupported version")
    try:
        kw = {f: doc[k] for k, f in PROFILE_KEYS.items()}
    except KeyError as e:
        raise BundleError(f"train.json: missing {e}") from e
    types = {f.name: f.type for f in fields(Profile)}
    for name, value in kw.items():
        want = {str: str, "str": str, int: int, "int": int}.get(types[name], (int, float))
        if isinstance(value, bool) or not isinstance(value, want):
            raise BundleError(f"train.json: bad {name}")
    if kw["name"] not in QUALITIES or not 100 <= kw["steps"] <= 100000 \
            or not 256 <= kw["edge"] <= 8192 or not 1000 <= kw["max_splats"] <= 20_000_000:
        raise BundleError("train.json: values out of range")
    return Profile(**kw)


def _segments(data):
    """(marker, start, end) of the JPEG header segments up to (excluding) SOS; raises on a bad file."""
    if data[:2] != b"\xff\xd8":
        raise ValueError("not a JPEG")
    i, out = 2, []
    while i + 4 <= len(data):
        if data[i] != 0xFF:
            raise ValueError("corrupt JPEG header")
        marker = data[i + 1]
        if marker == 0xDA:  # SOS: entropy-coded data follows
            return out, i
        (length,) = struct.unpack(">H", data[i + 2:i + 4])
        out.append((marker, i, i + 2 + length))
        i += 2 + length
    raise ValueError("JPEG without image data")


def _is_metadata(data, marker, start):
    if marker == 0xFE:  # COM
        return True
    if 0xE0 <= marker <= 0xEF:  # APPn: only a JFIF APP0 is harmless
        return not (marker == 0xE0 and data[start + 4:start + 9] == b"JFIF\x00")
    return False


def jpeg_has_metadata(data):
    segs, _ = _segments(data)
    return any(_is_metadata(data, m, s) for m, s, _ in segs)


def strip_jpeg_metadata(data):
    """Drops APPn (but JFIF APP0) and COM segments at marker level; pixels are untouched."""
    segs, sos = _segments(data)
    kept = b"".join(data[s:e] for m, s, e in segs if not _is_metadata(data, m, s))
    return b"\xff\xd8" + kept + data[sos:]


def _entries(dataset):
    """(archive name, path) of the images and the sparse model under a COLMAP undistort output."""
    out = []
    for sub in ("images", os.path.join("sparse", "0")):
        root = os.path.join(dataset, sub)
        for dirpath, dirs, names in os.walk(root):
            dirs.sort()
            for n in sorted(names):
                path = os.path.join(dirpath, n)
                rel = os.path.relpath(path, dataset).replace(os.sep, "/")
                if sub == "images" and not n.lower().endswith(IMAGE_EXT):
                    raise JobError("bundle", f"unexpected training image type: {n}")
                out.append(("dataset/" + rel, path))
    return out


def build_bundle(dataset, out_zip, doc, report, zones=None):
    """Writes the bundle; returns {"images", "bytes", "zones"}. Every JPEG is stripped of metadata.
    zones: the zones spec (trainers.write_zones) or None."""
    entries = _entries(dataset)
    images = 0
    with zipfile.ZipFile(out_zip, "w") as z:
        z.writestr("train.json", json.dumps(doc), compress_type=zipfile.ZIP_DEFLATED)
        if zones is not None:
            z.writestr(ZONES_FILE, json.dumps(zones), compress_type=zipfile.ZIP_DEFLATED)
        for i, (name, path) in enumerate(entries):
            with open(path, "rb") as fh:
                data = fh.read()
            if name.startswith("dataset/images/"):
                data = strip_jpeg_metadata(data)
                images += 1
                z.writestr(name, data, compress_type=zipfile.ZIP_STORED)
            else:
                z.writestr(name, data, compress_type=zipfile.ZIP_DEFLATED)
            report((i + 1) / max(1, len(entries)), f"{i + 1}/{len(entries)} files")
    if not images or not any(n.startswith("dataset/sparse/0/") for n, _ in entries):
        raise JobError("bundle", "the undistorted dataset is incomplete (no images or no sparse model)")
    return {"images": images, "bytes": os.path.getsize(out_zip), "zones": zones is not None}


def _check_member(info):
    name = info.filename
    parts = name.split("/")
    if name.startswith("/") or "\\" in name or ".." in parts or ":" in name or "" in parts[:-1]:
        raise BundleError(f"unsafe path in bundle: {name!r}")
    if stat.S_ISLNK(info.external_attr >> 16):
        raise BundleError(f"link in bundle: {name!r}")
    if not (name in ("train.json", ZONES_FILE) or name.startswith("dataset/images/")
            or name.startswith("dataset/sparse/0/")):
        raise BundleError(f"unexpected file in bundle: {name!r}")


def check_zones(doc):
    """The zones.json of a bundle: the shape gsplat_zones.ZoneMap reads (BundleError otherwise)."""
    ok = isinstance(doc, dict) and isinstance(doc.get("facets"), list) and doc["facets"] \
        and isinstance(doc.get("params"), dict) and all(isinstance(doc.get(k), list) and len(doc[k]) == 3
                                                          for k in ("boxLo", "boxHi")) \
        and isinstance(doc.get("toWorldMm"), list) and len(doc["toWorldMm"]) == 4
    if not ok:
        raise BundleError("zones.json: not a zones document")
    return doc


def _unpack(z, infos, out_dir, max_bytes):
    written = 0
    for info in infos:
        if info.is_dir():
            continue
        dest = os.path.join(out_dir, *info.filename.split("/"))
        os.makedirs(os.path.dirname(dest), exist_ok=True)
        with z.open(info) as src, open(dest, "wb") as dst:
            while chunk := src.read(1 << 20):
                written += len(chunk)
                if written > max_bytes:
                    raise BundleError("bundle too large")
                dst.write(chunk)


def extract_bundle(zip_path, out_dir, max_bytes=8 << 30):
    """Safe unpack (no absolute/.. paths, no links, only the bundle layout, size-capped). Returns
    (dataset dir, Profile from train.json, zones.json path or None)."""
    try:
        z = zipfile.ZipFile(zip_path)
    except zipfile.BadZipFile as e:
        raise BundleError(f"not a zip file: {e}") from e
    with z:
        infos = z.infolist()
        if len(infos) > MAX_ENTRIES or sum(i.file_size for i in infos) > max_bytes:
            raise BundleError("bundle too large")
        for info in infos:
            _check_member(info)
        _unpack(z, infos, out_dir, max_bytes)
        try:
            doc = json.loads(z.read("train.json"))
        except (KeyError, ValueError) as e:
            raise BundleError("train.json missing or not JSON") from e
    dataset = os.path.join(out_dir, "dataset")
    if not os.path.isdir(os.path.join(dataset, "images")) or not os.path.isdir(os.path.join(dataset, "sparse", "0")):
        raise BundleError("bundle without dataset/images or dataset/sparse/0")
    zones = os.path.join(out_dir, ZONES_FILE)
    wanted = False
    if os.path.exists(zones):
        if os.path.getsize(zones) > MAX_ZONES_BYTES:
            raise BundleError("zones.json too large")
        try:
            with open(zones) as fh:
                wanted = zones_opted_in(check_zones(json.load(fh)))
        except json.JSONDecodeError as e:
            raise BundleError(f"zones.json: {e}") from e
    return dataset, profile_from_doc(doc), zones if wanted else None


def zones_opted_in(doc):
    """The wall zones are opt-in on a runner too: its own SPLAT_WALL_ZONES=1, or zones the prepare side wrote
    because the job opted in (params.wallZones, zone_run.write_zones). Older prepare jobs sent zones.json with
    every wall geometry: without either, the runner trains plain gsplat (the bundle's zones are ignored)."""
    return settings.wall_zones or doc["params"].get(OPT_IN) is True
