"""sparse.zip: the DISTORTED COLMAP model of a splat-prepare run, for wall-geometry's `solve-sfm`.

  cameras.bin, images.bin, points3D.bin   COLMAP's binary model (the mapper's, before undistortion), with
                                          every image's intrinsics and 2D points rescaled from the size COLMAP
                                          saw (the profile's edge) to the photo's STORED resolution (the size
                                          it was uploaded at), so pixel coordinates of the stored photos (hold
                                          detections, taps) apply as they are. One camera per (COLMAP camera,
                                          stored size). 2D points without a 3D point are dropped (compact).
  stems.json                              {"version", "images": {COLMAP image name: {"stem", "role"
                                          (photo | frame | anchor), "width", "height"}}}

No pixels, no file names beyond the request's stems, no metadata. The model includes the anchor images
(solve-sfm fits the anchor similarity on their camera centres); the training bundle never does.
"""
import json
import os
import struct
import zipfile

import numpy as np

# model id -> number of params; models whose first param is one focal length (the rest: fx, fy first)
N_PARAMS = {0: 3, 1: 4, 2: 4, 3: 5, 4: 8, 5: 8, 6: 12, 7: 5, 8: 4, 9: 5, 10: 12}
SINGLE_FOCAL = {0, 2, 3, 8, 9}
OBS = np.dtype([("x", "<f8"), ("y", "<f8"), ("p", "<i8")])
STEMS_VERSION = 1
FILES = ("cameras.bin", "images.bin", "points3D.bin", "stems.json")


def _read(fh, fmt):
    size = struct.calcsize(fmt)
    data = fh.read(size)
    if len(data) != size:
        raise ValueError("truncated COLMAP model file")
    return struct.unpack(fmt, data)


def read_cameras(path):
    """{id: (model id, w, h, params list)}."""
    out = {}
    with open(path, "rb") as fh:
        (n,) = _read(fh, "<Q")
        for _ in range(n):
            cid, model, w, h = _read(fh, "<iiQQ")
            if model not in N_PARAMS:
                raise ValueError(f"unknown COLMAP camera model id {model}")
            out[cid] = (model, w, h, list(_read(fh, f"<{N_PARAMS[model]}d")))
    return out


def read_images(path):
    """[{"id", "q", "t", "cam", "name", "obs" (structured x, y, point3D id)}] in file order."""
    out = []
    with open(path, "rb") as fh:
        (n,) = _read(fh, "<Q")
        for _ in range(n):
            iid, qw, qx, qy, qz, tx, ty, tz, cid = _read(fh, "<i7di")
            name = b""
            while (ch := fh.read(1)) not in (b"\x00", b""):
                name += ch
            (n2d,) = _read(fh, "<Q")
            raw = fh.read(24 * n2d)
            if len(raw) != 24 * n2d:
                raise ValueError("truncated COLMAP model file")
            out.append({"id": iid, "q": (qw, qx, qy, qz), "t": (tx, ty, tz), "cam": cid,
                        "name": name.decode("utf-8"), "obs": np.frombuffer(raw, OBS).copy()})
    return out


def read_points(path):
    """[(id, xyz, rgb, error, track (L, 2) int32 image id / 2D index)]."""
    out = []
    with open(path, "rb") as fh:
        (n,) = _read(fh, "<Q")
        for _ in range(n):
            vals = _read(fh, "<Q3d3BdQ")
            raw = fh.read(8 * vals[8])
            if len(raw) != 8 * vals[8]:
                raise ValueError("truncated COLMAP model file")
            out.append((vals[0], vals[1:4], vals[4:7], vals[7], np.frombuffer(raw, "<i4").reshape(-1, 2)))
    return out


def scaled_params(model, params, sx, sy):
    """Intrinsics for the image scaled by (sx, sy): focal lengths and principal point scale, distortion
    coefficients (normalised coordinates) do not."""
    p = list(params)
    if model in SINGLE_FOCAL:
        p[0], p[1], p[2] = p[0] * (sx + sy) / 2, p[1] * sx, p[2] * sy
    else:
        p[0], p[1], p[2], p[3] = p[0] * sx, p[1] * sy, p[2] * sx, p[3] * sy
    return p


def _rescale(cameras, images, sizes):
    """New cameras (one per COLMAP camera x stored size) and each image's camera id + 2D scale."""
    new_cams, key_to_id, per_image = {}, {}, {}
    for im in images:
        model, w, h, params = cameras[im["cam"]]
        W, H = sizes.get(im["name"]) or (w, h)
        key = (im["cam"], W, H)
        if key not in key_to_id:
            key_to_id[key] = len(key_to_id) + 1
            new_cams[key_to_id[key]] = (model, W, H, scaled_params(model, params, W / w, H / h))
        per_image[im["id"]] = (key_to_id[key], W / w, H / h)
    return new_cams, per_image


def _compact(images, points):
    """Keep the 2D points that see a 3D point; returns ({image id: old index -> new index}, kept obs)."""
    known = {p[0] for p in points}
    remap, kept = {}, {}
    for im in images:
        obs = im["obs"]
        keep = (obs["p"] >= 0) & np.isin(obs["p"], np.fromiter(known, np.int64, len(known)))
        m = np.full(len(obs), -1, np.int64)
        m[keep] = np.arange(int(keep.sum()))
        remap[im["id"]], kept[im["id"]] = m, obs[keep]
    return remap, kept


def write_model(out_dir, cameras, images, points, per_image, remap, kept):
    os.makedirs(out_dir, exist_ok=True)
    with open(os.path.join(out_dir, "cameras.bin"), "wb") as fh:
        fh.write(struct.pack("<Q", len(cameras)))
        for cid, (model, w, h, params) in sorted(cameras.items()):
            fh.write(struct.pack("<iiQQ", cid, model, w, h) + struct.pack(f"<{len(params)}d", *params))
    with open(os.path.join(out_dir, "images.bin"), "wb") as fh:
        fh.write(struct.pack("<Q", len(images)))
        for im in images:
            cam, sx, sy = per_image[im["id"]]
            obs = kept[im["id"]].copy()
            obs["x"] *= sx
            obs["y"] *= sy
            fh.write(struct.pack("<i7di", im["id"], *im["q"], *im["t"], cam) + im["name"].encode() + b"\x00")
            fh.write(struct.pack("<Q", len(obs)) + obs.tobytes())
    with open(os.path.join(out_dir, "points3D.bin"), "wb") as fh:
        fh.write(struct.pack("<Q", len(points)))
        for pid, xyz, rgb, err, track in points:
            idx = np.array([remap[i][j] if i in remap and 0 <= j < len(remap[i]) else -1 for i, j in track], np.int64)
            ok = idx >= 0
            tr = np.stack([track[ok, 0], idx[ok]], 1).astype("<i4") if ok.any() else np.zeros((0, 2), "<i4")
            fh.write(struct.pack("<Q3d3BdQ", pid, *xyz, *rgb, err, len(tr)) + tr.tobytes())


def stems_doc(images, sizes, role_of):
    """stems.json: every image's stem, role and stored size."""
    out = {}
    for im in images:
        stem = os.path.splitext(os.path.basename(im["name"]))[0]
        w, h = sizes[im["name"]]
        out[im["name"]] = {"stem": stem, "role": role_of(stem), "width": int(w), "height": int(h)}
    return {"version": STEMS_VERSION, "images": out}


def export_sparse(model_dir, out_zip, stored_sizes, role_of, work_dir):
    """model_dir: the mapper's binary model; stored_sizes: {stem: (w, h)} (missing: COLMAP's size);
    role_of(stem) -> "photo" | "frame" | "anchor". Returns {"images", "points", "bytes"}."""
    cameras = read_cameras(os.path.join(model_dir, "cameras.bin"))
    images = read_images(os.path.join(model_dir, "images.bin"))
    points = read_points(os.path.join(model_dir, "points3D.bin"))
    sizes = {}
    for im in images:
        stem = os.path.splitext(os.path.basename(im["name"]))[0]
        _, w, h, _ = cameras[im["cam"]]
        sizes[im["name"]] = tuple(stored_sizes.get(stem) or (w, h))
    new_cams, per_image = _rescale(cameras, images, sizes)
    remap, kept = _compact(images, points)
    out_dir = os.path.join(work_dir, "sparse-export")
    write_model(out_dir, new_cams, images, points, per_image, remap, kept)
    with open(os.path.join(out_dir, "stems.json"), "w") as fh:
        json.dump(stems_doc(images, sizes, role_of), fh)
    with zipfile.ZipFile(out_zip, "w", zipfile.ZIP_DEFLATED) as z:
        for name in FILES:
            z.write(os.path.join(out_dir, name), name)
    return {"images": len(images), "points": len(points), "bytes": os.path.getsize(out_zip)}
