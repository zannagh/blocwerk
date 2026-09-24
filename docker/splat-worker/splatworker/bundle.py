"""The training bundle a 3D runner gets (kind `splat-prepare` builds it, gpurunner unpacks it):

  dataset/images/<group>/<stem>.jpg   the undistorted images (metadata-free: no APPn but JFIF, no COM)
  dataset/sparse/0/*.bin              COLMAP's undistorted sparse model (the layout Brush expects)
  train.json                          the quality profile to train (train_doc / profile_from_doc)

Nothing else: no original file names (stems only), no geometry, no GPS, no photo metadata.
"""
import os
import stat
import struct
import zipfile
from dataclasses import fields

from computejobs.child import JobError

from .profiles import Profile

TRAIN_DOC_VERSION = 1
IMAGE_EXT = (".jpg", ".jpeg")
# train.json key <-> Profile field
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
    if kw["name"] not in ("draft", "high", "max") or not 100 <= kw["steps"] <= 100000 \
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
        for dirpath, _, names in os.walk(root):
            for n in sorted(names):
                path = os.path.join(dirpath, n)
                rel = os.path.relpath(path, dataset).replace(os.sep, "/")
                if sub == "images" and not n.lower().endswith(IMAGE_EXT):
                    raise JobError("bundle", f"unexpected training image type: {n}")
                out.append(("dataset/" + rel, path))
    return out


def build_bundle(dataset, out_zip, doc, report):
    """Writes the bundle; returns {"images", "bytes"}. Every JPEG is checked and stripped if needed."""
    import json
    entries = _entries(dataset)
    images = 0
    with zipfile.ZipFile(out_zip, "w") as z:
        z.writestr("train.json", json.dumps(doc), compress_type=zipfile.ZIP_DEFLATED)
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
    return {"images": images, "bytes": os.path.getsize(out_zip)}


def _check_member(info):
    name = info.filename
    parts = name.split("/")
    if name.startswith("/") or "\\" in name or ".." in parts or ":" in name or "" in parts[:-1]:
        raise BundleError(f"unsafe path in bundle: {name!r}")
    if stat.S_ISLNK(info.external_attr >> 16):
        raise BundleError(f"link in bundle: {name!r}")
    if not (name == "train.json" or name.startswith("dataset/images/") or name.startswith("dataset/sparse/0/")):
        raise BundleError(f"unexpected file in bundle: {name!r}")


def extract_bundle(zip_path, out_dir, max_bytes=8 << 30):
    """Safe unpack (no absolute/.. paths, no links, only the bundle layout, size-capped). Returns
    (dataset dir, Profile from train.json)."""
    import json
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
        try:
            doc = json.loads(z.read("train.json"))
        except (KeyError, ValueError) as e:
            raise BundleError("train.json missing or not JSON") from e
    dataset = os.path.join(out_dir, "dataset")
    if not os.path.isdir(os.path.join(dataset, "images")) or not os.path.isdir(os.path.join(dataset, "sparse", "0")):
        raise BundleError("bundle without dataset/images or dataset/sparse/0")
    return dataset, profile_from_doc(doc)
