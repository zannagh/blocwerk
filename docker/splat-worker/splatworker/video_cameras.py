"""The registered video frames' cameras for frame.json `videoCameras` (the app's coverage report).

The frames are mapped in the same COLMAP run as the photos, so each registered frame has a solved pose
and camera there. frame.json reports them in the wall-geometry world (mm) in the shape of the geometry
document's `cameras`: `image` (the frame's stem), `width`, `height`, `K` (row-major 3x3, px), `dist`
(OpenCV k1, k2, p1, p2) and the world-to-camera pose `x = R X + t`. They are coverage facts only: never
used to align anything (align.py uses the photos).
"""
import os

import numpy as np

from .frames import is_frame


def opencv_intrinsics(cam):
    """(K row-major, OpenCV dist) of a COLMAP camera, or None for a model without an OpenCV equivalent."""
    m, p = cam["model"], cam["params"]
    if m == "SIMPLE_PINHOLE" and len(p) == 3:
        (fx, cx, cy), fy, dist = p, p[0], []
    elif m == "PINHOLE" and len(p) == 4:
        (fx, fy, cx, cy), dist = p, []
    elif m == "SIMPLE_RADIAL" and len(p) == 4:
        fx, fy, cx, cy, dist = p[0], p[0], p[1], p[2], [p[3], 0.0, 0.0, 0.0]
    elif m == "RADIAL" and len(p) == 5:
        fx, fy, cx, cy, dist = p[0], p[0], p[1], p[2], [p[3], p[4], 0.0, 0.0]
    elif m == "OPENCV" and len(p) == 8:
        (fx, fy, cx, cy), dist = p[:4], list(p[4:])
    else:
        return None
    return [fx, 0.0, cx, 0.0, fy, cy, 0.0, 0.0, 1.0], [float(v) for v in dist]


def frame_cameras(model):
    """The registered video frames of a text model (colmap_model.read_text_model), COLMAP frame, by stem,
    in video order: [{image, width, height, K, dist, R (9), t (3)}]. Frames of an unknown camera are left out."""
    cams, out = model.get("cameras") or {}, []
    for name, pose in sorted((model.get("poses") or {}).items(), key=lambda kv: os.path.basename(kv[0])):
        stem = os.path.splitext(os.path.basename(name))[0]
        cam = cams.get(pose["camera"])
        intr = opencv_intrinsics(cam) if is_frame(stem) and cam else None
        if intr is None:
            continue
        out.append({"image": stem, "width": int(cam["width"]), "height": int(cam["height"]),
                    "K": intr[0], "dist": intr[1], "R": np.asarray(pose["R"], float).reshape(9).tolist(),
                    "t": np.asarray(pose["t"], float).reshape(3).tolist()})
    return out


def world_matrix(frame):
    """The splat -> world (mm) 4x4 the app uses (CaptureSplatDocuments.WorldMatrix): an applied refinement
    wins over the camera-centre alignment. None for an unaligned frame."""
    ref = frame.get("refinement") or {}
    m = ref.get("toWorldMm") if ref.get("applied") else None
    m = m if m is not None else frame.get("toWorldMm")
    return None if m is None else np.asarray(m, float).reshape(4, 4)


def to_world(cameras, M):
    """The cameras in the world of the similarity M (X' = s Q X + T): R' = R Q^T, t' = s t - R' T, so the
    projection is unchanged and the camera frame stays metric (mm). Bad cameras or a degenerate M -> []."""
    A, T = M[:3, :3], M[:3, 3]
    det = float(np.linalg.det(A))
    if not np.isfinite(det) or det <= 0:
        return []
    s = det ** (1.0 / 3.0)
    U, _, Vt = np.linalg.svd(A / s)
    Q = U @ Vt  # the nearest rotation (a refined M is a similarity up to rounding)
    out = []
    for c in cameras:
        R = np.asarray(c["R"], float).reshape(3, 3) @ Q.T
        t = s * np.asarray(c["t"], float) - R @ T
        if not (np.isfinite(R).all() and np.isfinite(t).all()):
            continue
        out.append({**c, "K": [round(float(v), 4) for v in c["K"]], "dist": [round(float(v), 6) for v in c["dist"]],
                    "R": [round(float(v), 6) for v in R.reshape(9)], "t": [round(float(v), 2) for v in t]})
    return out


def video_cameras(cameras, frame):
    """frame.json `videoCameras` for an aligned frame, else None (no field)."""
    M = world_matrix(frame) if frame.get("aligned") else None
    if M is None or not cameras:
        return None
    return to_world(cameras, M)
