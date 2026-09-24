"""COLMAP binary sparse model reader (cameras.bin, images.bin, points3D.bin), numpy only.

Shared by the gsplat trainer script (which runs in its own Python, gsplat_train.py) and the worker
(gsplat_trainer.check_frame). Poses stay exactly as COLMAP wrote them: world-to-camera R|t in COLMAP's
frame, so whatever is trained from them stays in that frame too (frame.json and the alignment assume it).
"""
import os
import struct

import numpy as np

# model id -> (name, number of params); COLMAP src/colmap/sensor/models.h
CAMERA_MODELS = {0: ("SIMPLE_PINHOLE", 3), 1: ("PINHOLE", 4), 2: ("SIMPLE_RADIAL", 4), 3: ("RADIAL", 5),
                 4: ("OPENCV", 8), 5: ("OPENCV_FISHEYE", 8), 6: ("FULL_OPENCV", 12), 7: ("FOV", 5),
                 8: ("SIMPLE_RADIAL_FISHEYE", 4), 9: ("RADIAL_FISHEYE", 5), 10: ("THIN_PRISM_FISHEYE", 12)}
PINHOLE_MODELS = ("SIMPLE_PINHOLE", "PINHOLE")


def _read(fh, fmt):
    size = struct.calcsize(fmt)
    data = fh.read(size)
    if len(data) != size:
        raise ValueError("truncated COLMAP model file")
    return struct.unpack(fmt, data)


def read_cameras(path):
    """{camera_id: {"model", "width", "height", "params"}}."""
    cams = {}
    with open(path, "rb") as fh:
        (n,) = _read(fh, "<Q")
        for _ in range(n):
            cam_id, model_id, w, h = _read(fh, "<iiQQ")
            name, n_params = CAMERA_MODELS.get(model_id, (f"model {model_id}", None))
            if n_params is None:
                raise ValueError(f"unknown COLMAP camera model id {model_id}")
            cams[cam_id] = {"model": name, "width": w, "height": h, "params": list(_read(fh, f"<{n_params}d"))}
    return cams


def read_images(path):
    """[{"id", "name", "camera_id", "qvec" (w, x, y, z), "tvec"}] in file order (world-to-camera)."""
    images = []
    with open(path, "rb") as fh:
        (n,) = _read(fh, "<Q")
        for _ in range(n):
            img_id, qw, qx, qy, qz, tx, ty, tz, cam_id = _read(fh, "<i7di")
            name = b""
            while (ch := fh.read(1)) not in (b"\x00", b""):
                name += ch
            (n2d,) = _read(fh, "<Q")
            fh.seek(24 * n2d, os.SEEK_CUR)  # x, y (double) + point3D id (int64) each
            images.append({"id": img_id, "name": name.decode("utf-8"), "camera_id": cam_id,
                           "qvec": (qw, qx, qy, qz), "tvec": (tx, ty, tz)})
    return images


def read_points(path):
    """(xyz float64 (n, 3), rgb uint8 (n, 3))."""
    with open(path, "rb") as fh:
        (n,) = _read(fh, "<Q")
        xyz, rgb = np.empty((n, 3)), np.empty((n, 3), np.uint8)
        for i in range(n):
            vals = _read(fh, "<Q3d3BdQ")
            xyz[i], rgb[i] = vals[1:4], vals[4:7]
            fh.seek(8 * vals[8], os.SEEK_CUR)  # track: (image id, point2D idx) int32 pairs
    return xyz, rgb


def qvec_to_rotmat(q):
    w, x, y, z = q
    return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y)],
                     [2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x)],
                     [2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y)]])


def viewmat(image):
    """4x4 world-to-camera matrix of an image (COLMAP / OpenCV camera: x right, y down, z forward)."""
    m = np.eye(4)
    m[:3, :3], m[:3, 3] = qvec_to_rotmat(image["qvec"]), image["tvec"]
    return m


def camera_centre(image):
    return -qvec_to_rotmat(image["qvec"]).T @ np.asarray(image["tvec"], float)


def intrinsics(cam):
    """(fx, fy, cx, cy) of a pinhole camera; ValueError for any model with distortion."""
    p = cam["params"]
    if cam["model"] == "SIMPLE_PINHOLE":
        return p[0], p[0], p[1], p[2]
    if cam["model"] == "PINHOLE":
        return p[0], p[1], p[2], p[3]
    raise ValueError(f"camera model {cam['model']} has distortion: undistort first (colmap image_undistorter)")


def model_dir(dataset_dir):
    """<dataset>/sparse/0 (Brush's layout, colmap.Colmap.undistort) or <dataset>/sparse."""
    for d in (os.path.join(dataset_dir, "sparse", "0"), os.path.join(dataset_dir, "sparse")):
        if os.path.exists(os.path.join(d, "cameras.bin")):
            return d
    raise FileNotFoundError(f"no COLMAP binary model under {dataset_dir}/sparse")


def read_model(dataset_dir):
    d = model_dir(dataset_dir)
    return (read_cameras(os.path.join(d, "cameras.bin")), read_images(os.path.join(d, "images.bin")),
            *read_points(os.path.join(d, "points3D.bin")))


def read_text_model(txt_dir):
    """{'images': {name: camera centre (3,)}, 'points': n, 'meanReprojErrorPx': float|None,
    'meanTrackLength': float|None (images observing a point, on average)}."""
    images = {}
    with open(os.path.join(txt_dir, "images.txt")) as fh:
        lines = [ln for ln in fh.read().split("\n") if not ln.startswith("#")]
    for ln in lines[0::2]:
        p = ln.split()
        if len(p) < 10:
            continue
        R, t = qvec_to_rotmat([float(v) for v in p[1:5]]), np.array([float(v) for v in p[5:8]])
        images[p[9]] = -R.T @ t
    n_points, err_sum, track_sum = 0, 0.0, 0
    with open(os.path.join(txt_dir, "points3D.txt")) as fh:
        for ln in fh:
            if ln.startswith("#") or not ln.strip():
                continue
            n_points += 1
            parts = ln.split()
            err_sum += float(parts[7])
            track_sum += (len(parts) - 8) // 2  # IMAGE_ID, POINT2D_IDX pairs
    return {"images": images, "points": n_points,
            "meanReprojErrorPx": round(err_sum / n_points, 3) if n_points else None,
            "meanTrackLength": round(track_sum / n_points, 2) if n_points else None}
