"""splat-prepare's sparse.zip: the distorted COLMAP binary model + stems.json (splat-worker sparse_export.py).

Everything here is in COLMAP's own frame and units. Camera intrinsics are at each photo's STORED resolution.
Pixel coordinates given to this package (hold detections, taps) use the OpenCV convention of the geometry
document (the centre of the top-left pixel is (0, 0)); COLMAP's is (0.5, 0.5).
"""
import json
import os
import struct
import zipfile

import numpy as np

N_PARAMS = {0: 3, 1: 4, 2: 4, 3: 5, 4: 8, 5: 8, 6: 12, 7: 5, 8: 4, 9: 5, 10: 12}
SUPPORTED = {0: "SIMPLE_PINHOLE", 1: "PINHOLE", 2: "SIMPLE_RADIAL", 3: "RADIAL", 4: "OPENCV"}
FILES = ("cameras.bin", "images.bin", "points3D.bin", "stems.json")
ROLES = ("photo", "frame", "anchor")
MAX_IMAGES = 5000
MAX_POINTS = 3_000_000
MAX_OBS = 200_000  # 2D points per image
OBS = np.dtype([("x", "<f8"), ("y", "<f8"), ("p", "<i8")])


class ModelError(ValueError):
    """The sparse model is unusable; the message is safe to return to the client."""


def _read(fh, fmt):
    size = struct.calcsize(fmt)
    data = fh.read(size)
    if len(data) != size:
        raise ModelError("truncated COLMAP model file")
    return struct.unpack(fmt, data)


def qvec_to_rotmat(q):
    w, x, y, z = np.asarray(q, float) / np.linalg.norm(q)
    return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y)],
                     [2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x)],
                     [2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y)]])


def read_cameras(path):
    cams = {}
    with open(path, "rb") as fh:
        (n,) = _read(fh, "<Q")
        if n > MAX_IMAGES:
            raise ModelError("too many cameras")
        for _ in range(n):
            cid, model, w, h = _read(fh, "<iiQQ")
            if model not in SUPPORTED:
                raise ModelError(f"camera model id {model} is not supported (expected one of {sorted(SUPPORTED)})")
            params = np.array(_read(fh, f"<{N_PARAMS[model]}d"))
            if not np.isfinite(params).all() or not (16 <= w <= 20000 and 16 <= h <= 20000):
                raise ModelError(f"camera {cid}: bad size or parameters")
            cams[cid] = {"model": model, "width": int(w), "height": int(h), "params": params}
    return cams


def read_images(path):
    """[{"id", "name", "cam", "R" (world->cam), "t", "C" (centre), "obs" (x, y, point id)}]."""
    out = []
    with open(path, "rb") as fh:
        (n,) = _read(fh, "<Q")
        if n > MAX_IMAGES:
            raise ModelError(f"more than {MAX_IMAGES} images")
        for _ in range(n):
            iid, qw, qx, qy, qz, tx, ty, tz, cid = _read(fh, "<i7di")
            name = b""
            while (ch := fh.read(1)) not in (b"\x00", b""):
                name += ch
                if len(name) > 512:
                    raise ModelError("image name too long")
            (n2d,) = _read(fh, "<Q")
            if n2d > MAX_OBS:
                raise ModelError("too many 2D points in one image")
            raw = fh.read(24 * n2d)
            if len(raw) != 24 * n2d:
                raise ModelError("truncated COLMAP model file")
            R, t = qvec_to_rotmat((qw, qx, qy, qz)), np.array([tx, ty, tz])
            out.append({"id": iid, "name": name.decode("utf-8", "replace"), "cam": cid, "R": R, "t": t,
                        "C": -R.T @ t, "obs": np.frombuffer(raw, OBS)})
    return out


def read_points(path):
    """(ids (n,), xyz (n, 3), error px (n,), track length (n,))."""
    with open(path, "rb") as fh:
        (n,) = _read(fh, "<Q")
        if n > MAX_POINTS:
            raise ModelError(f"more than {MAX_POINTS} points")
        ids, xyz, err, tl = np.empty(n, np.int64), np.empty((n, 3)), np.empty(n), np.empty(n, np.int64)
        head = struct.Struct("<Q3d3BdQ")
        for i in range(n):
            data = fh.read(head.size)
            if len(data) != head.size:
                raise ModelError("truncated COLMAP model file")
            v = head.unpack(data)
            ids[i], xyz[i], err[i], tl[i] = v[0], v[1:4], v[7], v[8]
            fh.seek(8 * v[8], os.SEEK_CUR)
    if not np.isfinite(xyz).all():
        raise ModelError("non-finite 3D point")
    return ids, xyz, err, tl


def read_stems(path, images):
    try:
        with open(path) as fh:
            doc = json.load(fh)
        table = doc["images"]
        assert isinstance(table, dict)
    except (OSError, ValueError, KeyError, AssertionError) as e:
        raise ModelError("stems.json is missing or not a stems document") from e
    out = {}
    for im in images:
        e = table.get(im["name"])
        if not isinstance(e, dict) or e.get("role") not in ROLES or not isinstance(e.get("stem"), str) \
                or not 0 < len(e["stem"]) <= 128:
            raise ModelError(f"stems.json has no valid entry for image {im['name'][:80]!r}")
        out[im["name"]] = {"stem": e["stem"], "role": e["role"], "width": e.get("width"), "height": e.get("height")}
    return out


def load_model(model_dir, stems=None):
    """The model as one dict: cams, images (each with stem/role), points (ids, xyz, err, tl)."""
    cams = read_cameras(os.path.join(model_dir, "cameras.bin"))
    images = read_images(os.path.join(model_dir, "images.bin"))
    stems = stems or read_stems(os.path.join(model_dir, "stems.json"), images)
    for im in images:
        if im["cam"] not in cams:
            raise ModelError(f"image {im['name'][:80]!r} has an unknown camera")
        im.update(stem=stems[im["name"]]["stem"], role=stems[im["name"]]["role"])
    ids, xyz, err, tl = read_points(os.path.join(model_dir, "points3D.bin"))
    return {"cams": cams, "images": images, "ids": ids, "xyz": xyz, "err": err, "tl": tl}


def extract_sparse(zip_path, out_dir, max_bytes):
    """Unpack sparse.zip (only the four known files, size-capped) into out_dir."""
    try:
        z = zipfile.ZipFile(zip_path)
    except zipfile.BadZipFile as e:
        raise ModelError("'sparse' is not a zip file") from e
    with z:
        infos = {i.filename: i for i in z.infolist()}
        if set(infos) != set(FILES):
            raise ModelError(f"sparse.zip must contain exactly {', '.join(FILES)}")
        if sum(i.file_size for i in infos.values()) > max_bytes:
            raise ModelError("sparse.zip is too large when unpacked")
        os.makedirs(out_dir, exist_ok=True)
        for name, info in infos.items():
            written = 0
            with z.open(info) as src, open(os.path.join(out_dir, name), "wb") as dst:
                while chunk := src.read(1 << 20):
                    written += len(chunk)
                    if written > max_bytes:
                        raise ModelError("sparse.zip is too large when unpacked")
                    dst.write(chunk)
    return out_dir
