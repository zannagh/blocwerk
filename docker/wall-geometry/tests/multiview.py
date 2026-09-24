"""A synthetic two-facet corner (segments 1 and 2, both vertical, 90 deg apart) seen by 9 cameras.

Pinhole, no distortion, 0.3 px corner noise: a clean scene the solver must reproduce to sub-pixel
RMS. Tests corrupt single observations of it to exercise the false-detection rejection.
"""
import copy

import numpy as np
from synthetic import _look_at

W, H, F, SIZE = 4032, 3024, 3000.0, 125.0
OBJ = np.array([[-1, 1, 0], [1, 1, 0], [1, -1, 0], [-1, -1, 0]], float) * SIZE / 2  # TL,TR,BR,BL, +z out


def _markers():
    """id -> (4,3) world corners. Facet 1: plane y=0 facing -y; facet 2: plane x=1600 facing -x."""
    out = {}
    f1 = np.column_stack([[1, 0, 0], [0, 0, 1], [0, -1, 0]]).astype(float)
    f2 = np.column_stack([[0, -1, 0], [0, 0, 1], [-1, 0, 0]]).astype(float)
    for k in range(6):
        a, b = 250 + 450 * (k % 3), 500 + 500 * (k // 3)
        out[6 + k] = OBJ @ f1.T + np.array([a, 0.0, b])
        if k == 5:
            continue  # id 17 stays free for the injected false detection
        out[12 + k] = OBJ @ f2.T + np.array([1600.0, -250 - 400 * (k % 3), b + 100 * (k % 2)])
    return out


def _cameras():
    cams = []
    for i, (cx, cy, cz) in enumerate([(300, -2600, 900), (800, -2500, 1100), (1200, -2400, 800),
                                      (500, -2200, 1300), (1000, -2800, 1000), (200, -2000, 700),
                                      (700, -1900, 1200), (1100, -2100, 900), (600, -2700, 1500)]):
        c = np.array([cx, cy, cz], float)
        R = _look_at(c, np.array([1100.0, -400.0, 1000.0]))
        cams.append((f"SYN_{i:02d}", R, -R @ c))
    return cams


def project(R, t, P):
    pc = P @ R.T + t
    return pc[:, :2] / pc[:, 2:] * F + np.array([(W - 1) / 2, (H - 1) / 2])


def request(seed=0, mk=None):
    """Clean request dict plus the ground-truth marker corners (`mk`: other corners, same layout)."""
    rng = np.random.default_rng(seed)
    mk = _markers() if mk is None else mk
    photos = []
    for name, R, t in _cameras():
        ms = []
        for mid, P in sorted(mk.items()):
            px = project(R, t, P)
            if (px < 20).any() or (px[:, 0] > W - 20).any() or (px[:, 1] > H - 20).any():
                continue
            ms.append({"id": mid, "corners": (px + rng.normal(0, 0.3, px.shape)).tolist()})
        photos.append({"name": name, "width": W, "height": H, "focalPx": F, "cameraGroup": "syn",
                       "markers": ms})
    doc = {"markerSizeMm": SIZE, "dictionary": "DICT_4X4_50", "idScheme": "segment*6+role",
           "segments": [{"index": 1, "name": "front", "declaredAngleDeg": 0.0, "verticalReference": True},
                        {"index": 2, "name": "side", "declaredAngleDeg": 0.0, "verticalReference": True}],
           "levelPairs": [], "options": {"validate": False}, "photos": photos}
    return doc, mk


def leaned(mk, ids, deg, pivot_z=500.0):
    """Markers `ids` of the y=0 facet tilted about the horizontal x axis (a piece that leans out)."""
    a = np.radians(deg)
    R = np.array([[1, 0, 0], [0, np.cos(a), -np.sin(a)], [0, np.sin(a), np.cos(a)]])
    out = dict(mk)
    for m in ids:
        out[m] = (mk[m] - [0, 0, pivot_z]) @ R.T + [0, 0, pivot_z]
    return out


def with_false_single_view(doc, photo="SYN_03", mid=17):
    """A 'marker' seen once that is not a projected square (a hold read as an id)."""
    doc = copy.deepcopy(doc)
    p = next(p for p in doc["photos"] if p["name"] == photo)
    x, y = 1500.0, 1400.0
    p["markers"].append({"id": mid, "corners": [[x, y], [x + 110, y - 30], [x + 150, y + 60], [x + 5, y + 95]]})
    return doc


def with_moved_observation(doc, photo, mid, shift=(60.0, -45.0)):
    """One real marker's detection in one photo displaced (a false detection of a real id)."""
    doc = copy.deepcopy(doc)
    p = next(p for p in doc["photos"] if p["name"] == photo)
    m = next(m for m in p["markers"] if m["id"] == mid)
    m["corners"] = (np.array(m["corners"]) + np.array(shift)).tolist()
    return doc
