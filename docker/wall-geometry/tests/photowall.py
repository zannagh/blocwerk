"""A synthetic two-facet wall (a flat panel and one turned 30 deg back) photographed by many cameras.

Every photo is ray-traced from one procedural wall texture, so all photos agree on the wall itself;
each gets its own exposure / white-balance gain, and some have a dark "rafter" in front of the wall
that only they see. Small enough for unit tests, rich enough to exercise the multi-view blend, the
exposure fit and the consensus choice (several views per spot, more than `blendViews` in places).
"""
import cv2
import numpy as np
from synthetic import _look_at

W, H, F = 480, 360, 420.0
TEX_MM = 4.0  # procedural texture resolution
WALL_MM = (3200.0, 2000.0)  # unrolled width (both facets) x height
KINK_A = 1600.0  # facet 1 starts here along the unrolled wall
TURN = np.radians(30.0)


def _facets():
    u1 = np.array([np.cos(TURN), np.sin(TURN), 0.0])
    frames = [("0", np.zeros(3), np.array([1.0, 0, 0]), 0.0, KINK_A),
              ("1", np.array([KINK_A, 0, 0]), u1, 0.0, WALL_MM[0] - KINK_A)]
    out = []
    for fid, O, u, a0, a1 in frames:
        v = np.array([0, 0, 1.0])
        out.append({"id": fid, "origin": O.tolist(), "u": u.tolist(), "v": v.tolist(),
                    "normal": np.cross(u, v).tolist(), "measuredAngleDeg": 0.0, "yawDeg": 0.0,
                    "extentMm": {"aMin": a0, "aMax": a1, "bMin": 0.0, "bMax": WALL_MM[1]}, "markerIds": []})
    return out


def _texture(seed):
    rng = np.random.default_rng(seed)
    w, h = int(WALL_MM[0] / TEX_MM), int(WALL_MM[1] / TEX_MM)
    tex = cv2.GaussianBlur(rng.uniform(60, 200, (h, w, 3)).astype(np.float32), (0, 0), 6)
    tex = (tex - tex.mean()) * 3 + 130
    for _ in range(120):  # "holds": small sharp blobs
        x, y = int(rng.integers(0, w)), int(rng.integers(0, h))
        cv2.circle(tex, (x, y), int(rng.integers(3, 9)), tuple(float(c) for c in rng.uniform(20, 240, 3)), -1)
    return np.clip(tex, 0, 255).astype(np.float32)


def _trace(facets, R, t, tex):
    """Photo (H, W, 3) float32 of the wall seen by camera (R, t)."""
    C = -R.T @ t
    u, v = np.meshgrid(np.arange(W, dtype=np.float64), np.arange(H, dtype=np.float64))
    d = np.stack([(u - (W - 1) / 2) / F, (v - (H - 1) / 2) / F, np.ones_like(u)], -1) @ R
    best = np.full((H, W), np.inf)
    ta = np.full((H, W), -1.0)
    tb = np.full((H, W), -1.0)
    for f in facets:
        O, fu, fv, n = (np.array(f[k]) for k in ("origin", "u", "v", "normal"))
        s = ((O - C) @ n) / np.where(np.abs(d @ n) > 1e-9, d @ n, 1e-9)
        P = C + s[..., None] * d
        a, b = (P - O) @ fu, (P - O) @ fv
        e = f["extentMm"]
        ok = (s > 0) & (s < best) & (a >= e["aMin"]) & (a <= e["aMax"]) & (b >= 0) & (b <= WALL_MM[1])
        best[ok] = s[ok]
        ta[ok] = a[ok] + (KINK_A if f["id"] == "1" else 0.0)
        tb[ok] = b[ok]
    mx = (ta / TEX_MM - 0.5).astype(np.float32)
    my = ((WALL_MM[1] - tb) / TEX_MM - 0.5).astype(np.float32)
    img = cv2.remap(tex, mx, my, cv2.INTER_LINEAR, borderMode=cv2.BORDER_CONSTANT, borderValue=(30, 30, 30))
    img[ta < 0] = 30
    return img


def scene(n_photos=12, seed=0, rafter_every=4, distance=1500.0):
    """(geometry document, {name: BGR uint8 photo}) with `n_photos` cameras along the wall, about
    `distance` mm in front of it."""
    rng = np.random.default_rng(seed)
    facets = _facets()
    tex = _texture(seed)
    cams, photos = [], {}
    for i in range(n_photos):
        x = -100.0 + 3300.0 * i / max(n_photos - 1, 1)
        c = np.array([x, -distance * (1 + 0.2 * (i % 3)), 700.0 + 600.0 * (i % 2)])
        R = _look_at(c, np.array([x + 150.0 * ((i % 5) - 2), 0.0, 1000.0]))
        t = -R @ c
        name = f"PW_{i:03d}"
        img = _trace(facets, R, t, tex) * rng.uniform(0.85, 1.15, 3).astype(np.float32)
        if rafter_every and i % rafter_every == 1:
            y0 = int(rng.integers(40, H - 80))
            img[y0:y0 + 30] = (25, 30, 35)  # something in front of the wall only this photo sees
        img += rng.normal(0, 2, img.shape).astype(np.float32)
        photos[name] = np.clip(img + 0.5, 0, 255).astype(np.uint8)
        K = np.array([[F, 0, (W - 1) / 2], [0, F, (H - 1) / 2], [0, 0, 1]])
        cams.append({"image": name, "width": W, "height": H, "K": K.ravel().tolist(), "dist": [0, 0, 0, 0, 0],
                     "R": R.ravel().tolist(), "t": t.tolist()})
    doc = {"version": 1, "units": "mm", "dictionary": "DICT_4X4_50", "markerSizeMm": 125.0,
           "world": {"origin": "synthetic", "up": [0, 0, 1]},
           "segments": [{"index": 0, "name": "photowall", "facets": facets}], "markers": [], "cameras": cams}
    return doc, photos
