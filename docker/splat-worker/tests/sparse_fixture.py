"""A tiny consistent COLMAP model (RADIAL cameras, 2D observations that ARE the projections of the 3D points,
tracks pointing back at them) and a fake COLMAP that maps, deletes images and undistorts with it."""
import os
import shutil
import struct

import numpy as np

from splatworker.sparse_export import OBS, read_cameras, read_images, read_points


def project(params, X_cam):
    """RADIAL (f, cx, cy, k1, k2) projection of camera-frame points (COLMAP pixel convention)."""
    f, cx, cy, k1, k2 = params
    u, v = X_cam[:, 0] / X_cam[:, 2], X_cam[:, 1] / X_cam[:, 2]
    r2 = u * u + v * v
    d = 1 + k1 * r2 + k2 * r2 * r2
    return np.stack([f * u * d + cx, f * v * d + cy], 1)


def write_raw(out_dir, cams, images, points):
    """cams {id: (model, w, h, params)}; images [{"id", "q", "t", "cam", "name", "obs"}];
    points [(id, xyz, rgb, err, track (L, 2))]."""
    os.makedirs(out_dir, exist_ok=True)
    with open(os.path.join(out_dir, "cameras.bin"), "wb") as fh:
        fh.write(struct.pack("<Q", len(cams)))
        for cid, (model, w, h, p) in cams.items():
            fh.write(struct.pack("<iiQQ", cid, model, w, h) + struct.pack(f"<{len(p)}d", *p))
    with open(os.path.join(out_dir, "images.bin"), "wb") as fh:
        fh.write(struct.pack("<Q", len(images)))
        for im in images:
            fh.write(struct.pack("<i7di", im["id"], *im["q"], *im["t"], im["cam"]) + im["name"].encode() + b"\0")
            fh.write(struct.pack("<Q", len(im["obs"])) + im["obs"].astype(OBS).tobytes())
    with open(os.path.join(out_dir, "points3D.bin"), "wb") as fh:
        fh.write(struct.pack("<Q", len(points)))
        for pid, xyz, rgb, err, tr in points:
            tr = np.asarray(tr, "<i4").reshape(-1, 2)
            fh.write(struct.pack("<Q3d3BdQ", pid, *xyz, *rgb, err, len(tr)) + tr.tobytes())


def synthetic_model(out_dir, names, sizes, n_points=30, seed=1):
    """names: COLMAP image names; sizes {name: (w, h)} (one RADIAL camera per size). Every image sees every
    point; each image also gets 5 keypoints without a 3D point. Cameras look along +z from z = -4."""
    rng = np.random.default_rng(seed)
    cams, cam_of = {}, {}
    for name in names:
        wh = sizes[name]
        if wh not in cam_of:
            cam_of[wh] = len(cam_of) + 1
            cams[cam_of[wh]] = (3, wh[0], wh[1], [0.9 * max(wh), wh[0] / 2, wh[1] / 2, 0.05, -0.01])
    X = np.c_[rng.uniform(-1, 1, n_points), rng.uniform(-0.7, 0.7, n_points), rng.uniform(-0.2, 0.2, n_points)]
    images, tracks = [], {i: [] for i in range(n_points)}
    for k, name in enumerate(names):
        cid = cam_of[sizes[name]]
        t = np.array([0.3 * k - 0.5, 0.1 * k, 4.0])  # R = I, centre (-t)
        xy = project(cams[cid][3], X + t)
        obs = np.zeros(n_points + 5, OBS)
        order = rng.permutation(n_points + 5)  # observed and unobserved keypoints interleaved
        for j, slot in enumerate(order):
            if j < n_points:
                obs[slot] = (xy[j, 0], xy[j, 1], j + 1)
                tracks[j].append((k + 1, slot))
            else:
                obs[slot] = (rng.uniform(0, 10), rng.uniform(0, 10), -1)
        images.append({"id": k + 1, "q": (1.0, 0, 0, 0), "t": tuple(t), "cam": cid, "name": name, "obs": obs})
    points = [(j + 1, tuple(X[j]), (1, 2, 3), 0.4, tracks[j]) for j in range(n_points)]
    write_raw(out_dir, cams, images, points)
    return cams, images, points


def text_model(model_dir):
    """What Colmap.to_text returns (camera centres by name, counts) from a binary model."""
    images = read_images(os.path.join(model_dir, "images.bin"))
    points = read_points(os.path.join(model_dir, "points3D.bin"))
    return {"images": {im["name"]: -np.array(im["t"]) for im in images}, "points": len(points),
            "meanReprojErrorPx": 0.4, "meanTrackLength": float(np.mean([len(p[4]) for p in points]))}


class FakeColmap:
    """Stands in for colmap.Colmap: registers every ingested image, and records the call order."""
    calls = []
    gpu_extraction = False

    def __init__(self, *a, caps=None, **kw):
        self.caps = dict(caps or {})

    def extract(self, *a, **kw):
        FakeColmap.calls.append("extract")

    def feature_counts(self, db):
        return [9000] * 8

    def match(self, *a, **kw):
        FakeColmap.calls.append("match")

    match_pairs = match

    def map(self, db, image_dir, out_dir, n, report, mapper="incremental"):
        FakeColmap.calls.append("map")
        names = sorted(os.path.relpath(os.path.join(r, f), image_dir).replace(os.sep, "/")
                       for r, _, fs in os.walk(image_dir) for f in fs)
        synthetic_model(os.path.join(out_dir, "0"), names, {n: (80, 60) for n in names})

    def best_model(self, sparse):
        path = os.path.join(sparse, "0")
        return path, text_model(path)

    def triangulate(self, db, image_dir, model_dir, out_dir):
        shutil.copytree(model_dir, out_dir, dirs_exist_ok=True)

    def to_text(self, model_dir, out_dir):
        return text_model(model_dir)

    def delete_images(self, model_dir, out_dir, names):
        FakeColmap.calls.append(("delete", sorted(names)))
        cams = read_cameras(os.path.join(model_dir, "cameras.bin"))
        images = read_images(os.path.join(model_dir, "images.bin"))
        points = read_points(os.path.join(model_dir, "points3D.bin"))
        gone = {im["id"] for im in images if im["name"] in names}
        points = [(i, x, c, e, t[~np.isin(t[:, 0], list(gone))]) for i, x, c, e, t in points]
        write_raw(out_dir, cams, [im for im in images if im["id"] not in gone], points)

    def undistort(self, image_dir, model_dir, out_dir, max_size, report):
        FakeColmap.calls.append("undistort")
        for name in text_model(model_dir)["images"]:
            os.makedirs(os.path.dirname(os.path.join(out_dir, "images", name)), exist_ok=True)
            shutil.copy(os.path.join(image_dir, name), os.path.join(out_dir, "images", name))
        shutil.copytree(model_dir, os.path.join(out_dir, "sparse", "0"))
