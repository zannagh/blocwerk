"""A synthetic climbing wall as a COLMAP sparse model (the shape of splat-prepare's sparse.zip) for solve-sfm.

World (mm, z up, the climber at y < 0): a 45 deg overhang (4 x 3.5 m) above a 300 mm vertical kickboard, a
vertical side panel on its left (normal +x), rafters up right (37 deg, no holds), the floor, hold slabs
(120 mm patches 40-80 mm in front of their facet), a 900 x 700 mm volume slab 150 mm in front of the main wall,
12 mm noise. Photos (portrait and landscape, held all four ways) with the phone's accelerometer vector, hold
detections in the stored photos' pixels, and anchor photos known in a reference document of the true world.
The model is written in a random similarity frame (COLMAP knows neither scale nor orientation).
"""
import json
import os
import struct

import numpy as np

from wallgeometry.sfm.cameras import project

S45 = np.sqrt(0.5)
MAIN_N = np.array([0.0, -S45, -S45])
SIDE_N, KICK_N = np.array([1.0, 0, 0]), np.array([0.0, -1, 0])
MAIN_O, MAIN_U, MAIN_V = np.array([0.0, 0, 300]), np.array([1.0, 0, 0]), np.array([0.0, -S45, S45])
S0 = 450.0  # mm per model unit


def rot(axis, deg):
    a = np.asarray(axis, float) / np.linalg.norm(axis)
    K = np.array([[0, -a[2], a[1]], [a[2], 0, -a[0]], [-a[1], a[0], 0]])
    t = np.radians(deg)
    return np.eye(3) + np.sin(t) * K + (1 - np.cos(t)) * K @ K


def _patch(rng, o, u, v, a, b, density, n, noise):
    k = int(density * (a[1] - a[0]) * (b[1] - b[0]) / 1e6)
    ab = np.c_[rng.uniform(*a, k), rng.uniform(*b, k)]
    return o + ab[:, :1] * u + ab[:, 1:] * v + rng.normal(0, noise, (k, 1)) * n


def surfaces(rng, noise=12.0, floor=True):
    """(points (n, 3), hold centres (k, 3)) of the scene."""
    parts = [_patch(rng, MAIN_O, MAIN_U, MAIN_V, (0, 4000), (0, 3500), 1500, MAIN_N, noise),
             _patch(rng, np.zeros(3), MAIN_U, np.array([0.0, 0, 1]), (0, 4000), (0, 300), 1500, KICK_N, noise),
             _patch(rng, np.array([4300.0, 800, 2800]), MAIN_U, np.array([0.0, -0.6, 0.8]), (0, 1700), (0, 1500), 600,
                    np.array([0.0, -0.8, -0.6]), noise)]
    side = _patch(rng, np.zeros(3), np.array([0.0, -1, 0]), np.array([0.0, 0, 1]), (0, 2400), (0, 2700), 1500,
                  SIDE_N, noise)
    parts.append(side[side[:, 2] <= 300 - side[:, 1]])  # below the overhang's left edge
    if floor:
        parts.append(_patch(rng, np.array([-500.0, -4500, 0]), MAIN_U, np.array([0.0, 1, 0]), (0, 5500), (0, 4400),
                            150, np.array([0.0, 0, 1]), noise))
    holds = []
    for facet, count in (("main", 50), ("side", 12), ("kick", 8)):
        for _ in range(count):
            if facet == "main":
                o, u, v, n = MAIN_O + rng.uniform(200, 3800) * MAIN_U + rng.uniform(200, 3300) * MAIN_V, MAIN_U, MAIN_V, MAIN_N
            elif facet == "side":
                y = rng.uniform(300, 2200)
                o, u, v, n = np.array([0.0, -y, rng.uniform(100, 250 + y)]), np.array([0.0, -1, 0]), np.array([0.0, 0, 1]), SIDE_N
            else:
                o, u, v, n = np.array([rng.uniform(300, 3700), 0, 150.0]), MAIN_U, np.array([0.0, 0, 1]), KICK_N
            h = rng.uniform(40, 80)
            parts.append(_patch(rng, o + h * n - 60 * u - 60 * v, u, v, (0, 120), (0, 120), 2800, n, 4.0))
            holds.append(o + h * n)
    vol = MAIN_O + 1500 * MAIN_U + 1200 * MAIN_V + 150 * MAIN_N
    parts.append(_patch(rng, vol, MAIN_U, MAIN_V, (0, 900), (0, 700), 1500, MAIN_N, noise))
    return np.vstack(parts), np.array(holds)


def look_at(centre, target):
    z = (target - centre) / np.linalg.norm(target - centre)
    x = np.cross(z, [0, 0, 1.0])
    x /= np.linalg.norm(x)
    return np.vstack([x, np.cross(z, x), z])  # rows: image right, image down, forward


HOLDING = ("portrait", "landscape-left", "landscape-right", "portrait-upside-down")


def accelerometer(up_cam, holding):
    """The inverse of gravity.device_up_cam: Apple's vector for an up direction in the stored image's frame."""
    x, y, z = up_cam
    return {"portrait": (-x, y, z), "portrait-upside-down": (x, -y, z),
            "landscape-left": (y, x, z), "landscape-right": (-y, -x, z)}[holding]


def cameras(rng, n_photos=24, n_anchors=8):
    """[{stem, role, R (world->cam), C (world mm), w, h, params (RADIAL, COLMAP convention), holding}]."""
    cams = []
    for i in range(n_photos + n_anchors):
        anchor = i >= n_photos
        C = np.array([rng.uniform(300, 3700), rng.uniform(-3900, -2600), rng.uniform(1150, 1750)])
        target = np.array([rng.uniform(500, 3500), rng.uniform(-1500, -500), rng.uniform(900, 2000)])
        if i % 6 == 5:  # some look at the side panel and the kickboard
            target = np.array([0.0, -1200, 900]) if i % 12 == 5 else np.array([2000.0, 0, 200])
        R = rot(rng.normal(size=3), rng.normal(0, 2)) @ look_at(C, target)
        holding = HOLDING[i % 4]
        w, h = (3024, 4032) if holding.startswith("portrait") else (4032, 3024)
        f = 0.75 * max(w, h)
        cams.append({"stem": f"a{i - n_photos:02d}" if anchor else f"p{i + 1:02d}", "role": "anchor" if anchor else "photo",
                     "R": R, "C": C, "w": w, "h": h, "params": [f, w / 2, h / 2, 0.02, -0.005], "holding": holding})
    return cams


def detections(cam, X, rng, sigma=2.0):
    """Pixels (OpenCV convention) of world points X visible in the photo, with noise."""
    fake = {"model": 3, "params": np.array(cam["params"])}
    px, z = project(fake, cam["R"], -cam["R"] @ cam["C"], X)
    ok = (z > 300) & (px[:, 0] > 0) & (px[:, 0] < cam["w"]) & (px[:, 1] > 0) & (px[:, 1] < cam["h"])
    return px[ok] + rng.normal(0, sigma, (ok.sum(), 2))


def write_model(out_dir, cams, X, R0, T0, rng):
    """The model in the COLMAP frame X_col = R0^T (X - T0) / S0, with stems.json."""
    os.makedirs(out_dir, exist_ok=True)
    Xc = (X - T0) @ R0 / S0
    with open(os.path.join(out_dir, "cameras.bin"), "wb") as fh:
        fh.write(struct.pack("<Q", len(cams)))
        for i, c in enumerate(cams):
            fh.write(struct.pack("<iiQQ", i + 1, 3, c["w"], c["h"]) + struct.pack("<5d", *c["params"]))
    stems = {}
    with open(os.path.join(out_dir, "images.bin"), "wb") as fh:
        fh.write(struct.pack("<Q", len(cams)))
        for i, c in enumerate(cams):
            Rc = c["R"] @ R0
            tc = (c["R"] @ T0 - c["R"] @ c["C"]) / S0
            q = _quat(Rc)
            name = f"g/{c['stem']}.jpg"
            fh.write(struct.pack("<i7di", i + 1, *q, *tc, i + 1) + name.encode() + b"\0" + struct.pack("<Q", 0))
            stems[name] = {"stem": c["stem"], "role": c["role"], "width": c["w"], "height": c["h"]}
    with open(os.path.join(out_dir, "points3D.bin"), "wb") as fh:
        fh.write(struct.pack("<Q", len(Xc)))
        for j, x in enumerate(Xc):
            L = int(rng.integers(3, 7))
            fh.write(struct.pack("<Q3d3BdQ", j + 1, *x, 128, 128, 128, 0.5, L) + struct.pack(f"<{2 * L}i", *([1, 0] * L)))
    with open(os.path.join(out_dir, "stems.json"), "w") as fh:
        json.dump({"version": 1, "images": stems}, fh)
    return out_dir


def _quat(R):
    w = np.sqrt(max(1e-12, 1 + R[0, 0] + R[1, 1] + R[2, 2])) / 2
    q = np.array([w, (R[2, 1] - R[1, 2]) / (4 * w), (R[0, 2] - R[2, 0]) / (4 * w), (R[1, 0] - R[0, 1]) / (4 * w)])
    return q / np.linalg.norm(q)


def reference(cams):
    """A reference geometry document in the true world: the anchors' cameras (as p9x) and the true facets."""
    def facet(fid, o, u, v, n, a, b):
        return {"id": fid, "origin": list(o), "u": list(u), "v": list(v), "normal": list(n),
                "extentMm": {"aMin": 0, "aMax": a, "bMin": 0, "bMax": b}}
    segs = [{"index": 0, "facets": [facet("0", MAIN_O, MAIN_U, MAIN_V, MAIN_N, 4000, 3500)]},
            {"index": 1, "facets": [facet("1", np.zeros(3), MAIN_U, [0, 0, 1], KICK_N, 4000, 300)]}]
    refcams = [{"image": f"p9{c['stem'][1:]}", "R": c["R"].ravel().tolist(), "t": list(-c["R"] @ c["C"])}
               for c in cams if c["role"] == "anchor"]
    return {"version": 1, "world": {"up": [0, 0, 1], "gravityKnown": True}, "segments": segs, "cameras": refcams}
