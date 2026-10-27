"""COLMAP for `register-panels` (panels.py): localise panel photos against the capture's solved cameras.

1. feature_extractor over every photo and panel (GPU SIFT when the build has CUDA), one RADIAL camera per image;
2. the capture cameras are written as a known-pose model (world mm) with matching database ids, their intrinsics fixed;
3. matches_importer on chosen pairs: each photo with its nearest posed neighbours, every panel with every photo;
4. point_triangulator (poses and intrinsics fixed) -> 3D points in the wall-geometry world;
5. image_registrator with the existing frames fixed: each panel gets a pose and a refined focal / k1, k2.
Every COLMAP line goes to <work>/colmap.log; stage lines and errors also go to the runner's log, so a failure stays
readable after the job directory is deleted."""
import logging
import os
import sqlite3
import subprocess

import numpy as np

log = logging.getLogger("gpurunner")
NEIGHBOURS = 12
MAX_NEIGHBOUR_DEG = 50
MAX_IMAGE_SIZE = 3200
MAX_FEATURES = 16000
LOGGED = ("Registering image", "Image sees", "Elapsed time", "ERROR", "Error", "Check failed", "Triangulated")
RADIAL = 3


class PanelsInputError(Exception):
    """The job's inputs cannot be registered by any runner (bad bundle, mismatched photo sizes)."""


def quaternion(R):
    """COLMAP's (w, x, y, z) of a rotation matrix."""
    R = np.asarray(R, float).reshape(3, 3)
    w = np.sqrt(max(0.0, 1 + R[0, 0] + R[1, 1] + R[2, 2])) / 2
    x = np.copysign(np.sqrt(max(0.0, 1 + R[0, 0] - R[1, 1] - R[2, 2])) / 2, R[2, 1] - R[1, 2])
    y = np.copysign(np.sqrt(max(0.0, 1 - R[0, 0] + R[1, 1] - R[2, 2])) / 2, R[0, 2] - R[2, 0])
    z = np.copysign(np.sqrt(max(0.0, 1 - R[0, 0] - R[1, 1] + R[2, 2])) / 2, R[1, 0] - R[0, 1])
    q = np.array([w, x, y, z])
    return q / np.linalg.norm(q)


def rotation(q):
    w, x, y, z = q
    return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                     [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                     [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])


def pairs(cameras, photo_names, panel_names):
    """Each photo with its NEIGHBOURS nearest posed photos looking within MAX_NEIGHBOUR_DEG; every panel with every photo."""
    centres = np.array([-np.reshape(c["R"], (3, 3)).T @ np.array(c["t"], float) for c in cameras])
    axes = np.array([np.reshape(c["R"], (3, 3))[2] for c in cameras])
    out = set()
    for i in range(len(cameras)):
        near = [j for j in np.argsort(np.linalg.norm(centres - centres[i], axis=1))
                if j != i and axes[j] @ axes[i] > np.cos(np.radians(MAX_NEIGHBOUR_DEG))][:NEIGHBOURS]
        out.update(tuple(sorted((photo_names[i], photo_names[j]))) for j in near)
    out.update((p, n) for p in panel_names for n in photo_names)
    return sorted(out)


def write_known_model(db_path, model_dir, cameras, photo_names):
    """The capture cameras as a COLMAP text model under the database's ids; their database intrinsics set to match."""
    db = sqlite3.connect(db_path)
    ids = {n: (i, c) for i, n, c in db.execute("select image_id, name, camera_id from images")}
    cams, imgs = [], []
    for cam, name in zip(cameras, photo_names):
        image_id, camera_id = ids[name]
        K, d = cam["K"], list(cam.get("dist") or []) + [0.0, 0.0]
        params = [K[0], K[2], K[5], d[0], d[1]]
        size = db.execute("select width, height from cameras where camera_id=?", (camera_id,)).fetchone()
        if tuple(size) != (cam["width"], cam["height"]):
            raise PanelsInputError(f"photo {cam['name']} is {size[0]}x{size[1]}, its camera {cam['width']}x{cam['height']}")
        db.execute("update cameras set model=?, params=?, prior_focal_length=1 where camera_id=?",
                   (RADIAL, np.array(params, np.float64).tobytes(), camera_id))
        cams.append(f"{camera_id} RADIAL {cam['width']} {cam['height']} " + " ".join(f"{p:.10g}" for p in params))
        q, t = quaternion(cam["R"]), cam["t"]
        imgs.append(f"{image_id} {' '.join(f'{v:.12g}' for v in q)} {' '.join(f'{v:.10g}' for v in t)} {camera_id} {name}\n")
    db.commit()
    db.close()
    os.makedirs(model_dir, exist_ok=True)
    with open(os.path.join(model_dir, "cameras.txt"), "w") as fh:
        fh.write("\n".join(cams) + "\n")
    with open(os.path.join(model_dir, "images.txt"), "w") as fh:
        fh.write("".join(i + "\n" for i in imgs))
    open(os.path.join(model_dir, "points3D.txt"), "w").close()


def read_panels(model_dir, panel_files):
    """{panel name: pose document} for the registered panels of a text model, with their reprojection errors."""
    cameras = {}
    for line in open(os.path.join(model_dir, "cameras.txt")):
        if line.strip() and not line.startswith("#"):
            p = line.split()
            cameras[int(p[0])] = (int(p[2]), int(p[3]), [float(v) for v in p[4:]])
    points = {}
    for line in open(os.path.join(model_dir, "points3D.txt")):
        if line.strip() and not line.startswith("#"):
            p = line.split()
            points[int(p[0])] = np.array([float(v) for v in p[1:4]])
    lines = [line for line in open(os.path.join(model_dir, "images.txt")) if not line.startswith("#")]
    out = {}
    for head, obs in zip(lines[0::2], lines[1::2]):
        p = head.split()
        if len(p) < 10 or p[9] not in panel_files:
            continue
        R, t = rotation([float(v) for v in p[1:5]]), np.array([float(v) for v in p[5:8]])
        width, height, params = cameras[int(p[8])]
        errors = observation_errors(obs.split(), points, R, t, params)
        if not np.isfinite(errors).all():
            continue
        out[panel_files[p[9]]] = {"width": width, "height": height, "params": [round(v, 9) for v in params],
                                  "R": [round(v, 12) for v in R.ravel()], "t": [round(v, 6) for v in t],
                                  "observations": len(errors), "rmsPx": round(float(np.sqrt(np.mean(errors ** 2))), 3),
                                  "medianPx": round(float(np.median(errors)), 3)}
    return out


def observation_errors(obs, points, R, t, params):
    """Pixel distances between each observation with a 3D point and that point's projection (RADIAL camera)."""
    f, cx, cy, k1, k2 = (params + [0.0, 0.0])[:5]
    errors = []
    for k in range(0, len(obs) - 2, 3):
        X = points.get(int(obs[k + 2]))
        if X is None:
            continue
        c = R @ X + t
        x, y = c[0] / c[2], c[1] / c[2]
        r2 = x * x + y * y
        s = 1 + k1 * r2 + k2 * r2 * r2
        errors.append(np.hypot(f * x * s + cx - float(obs[k]), f * y * s + cy - float(obs[k + 1])))
    return np.array(errors) if errors else np.array([np.inf])


def run(args, log_path, stop):
    """Runs one COLMAP command; every line to log_path, the telling ones to the runner's log; raises on failure."""
    log.info("colmap %s", args[1])
    tail = []
    with open(log_path, "a") as out:
        proc = subprocess.Popen(args, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, errors="replace")
        for line in proc.stdout:
            out.write(line)
            tail = (tail + [line.rstrip()])[-15:]
            if any(k in line for k in LOGGED):
                log.info("colmap: %s", line.rstrip()[-200:])
            if stop.is_set():
                proc.kill()
        code = proc.wait()
    if stop.is_set():
        from .job import JobAbandoned
        raise JobAbandoned("stopped")
    if code != 0:
        log.error("colmap %s failed (%s): %s", args[1], code, " | ".join(tail))
        raise RuntimeError(f"colmap {args[1]} failed with exit code {code}: {tail[-1] if tail else ''}")


def register_panels(work, job, panels, progress, stop, env=None):
    """The whole pipeline in `work` (images/photos, images/panels); returns {panel: pose} for the registered ones."""
    env = os.environ if env is None else env
    colmap = env.get("COLMAP_BIN", "colmap")
    images = os.path.join(work, "images")
    found = {n: os.path.join(k, f) for k in ("photos", "panels") for f in os.listdir(os.path.join(images, k))
             for n in [os.path.splitext(f)[0]]}
    cameras = job["cameras"]
    photo_names = [found[c["name"]] for c in cameras]
    panel_names = [found[p] for p in panels]
    db, log_path = os.path.join(work, "db.db"), os.path.join(work, "colmap.log")
    with open(os.path.join(work, "images.txt"), "w") as fh:
        fh.write("\n".join(photo_names + panel_names) + "\n")
    with open(os.path.join(work, "pairs.txt"), "w") as fh:
        fh.write("".join(f"{a} {b}\n" for a, b in pairs(cameras, photo_names, panel_names)))
    steps = [
        ([colmap, "feature_extractor", "--database_path", db, "--image_path", images, "--image_list_path",
          os.path.join(work, "images.txt"), "--ImageReader.single_camera_per_image", "1", "--ImageReader.camera_model",
          "RADIAL", "--FeatureExtraction.max_image_size", str(MAX_IMAGE_SIZE), "--SiftExtraction.max_num_features",
          str(MAX_FEATURES)], "features"),
        (None, "known poses"),
        ([colmap, "matches_importer", "--database_path", db, "--match_list_path", os.path.join(work, "pairs.txt"),
          "--match_type", "pairs"], "matching"),
        ([colmap, "point_triangulator", "--database_path", db, "--image_path", images, "--input_path",
          os.path.join(work, "known"), "--output_path", os.path.join(work, "tri"), "--Mapper.ba_refine_focal_length", "0",
          "--Mapper.ba_refine_extra_params", "0", "--Mapper.ba_refine_principal_point", "0"], "triangulation"),
        ([colmap, "image_registrator", "--database_path", db, "--input_path", os.path.join(work, "tri"), "--output_path",
          os.path.join(work, "reg"), "--Mapper.fix_existing_frames", "1"], "registration"),
        ([colmap, "model_converter", "--input_path", os.path.join(work, "reg"), "--output_path",
          os.path.join(work, "txt"), "--output_type", "TXT"], "export"),
    ]
    for n, (args, label) in enumerate(steps):
        progress(n / len(steps), label)
        for d in ("tri", "reg", "txt"):
            os.makedirs(os.path.join(work, d), exist_ok=True)
        if args is None:
            write_known_model(db, os.path.join(work, "known"), cameras, photo_names)
        else:
            run(args, log_path, stop)
    return read_panels(os.path.join(work, "txt"), {found[p]: p for p in panels})
