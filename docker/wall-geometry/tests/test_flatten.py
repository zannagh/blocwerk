"""Even shading per overhang group (flatten.py)."""
import math

import numpy as np

from wallgeometry import flatten

RES = 5.0  # mm per texture px


def _facet(fid, x0, overhang_deg):
    t = math.radians(overhang_deg)
    return {"id": fid, "origin": [x0, 0.0, 0.0], "u": [1.0, 0.0, 0.0],
            "v": [0.0, -math.sin(t), math.cos(t)], "normal": [0.0, -math.cos(t), -math.sin(t)],
            "extentMm": {"aMin": 0.0, "aMax": 1500.0, "bMin": 0.0, "bMax": 1000.0}}


def _texture(fid, x0, light, holds=True, seed=0):
    """Plywood (tan, low chroma) under `light(x)`, with saturated red holds every 250 mm."""
    rng = np.random.default_rng(seed)
    W, H = int(1500 / RES), int(1000 / RES)
    x = x0 + (np.arange(W) + 0.5) * RES
    img = np.empty((H, W, 3), np.float32)
    img[:] = np.array([120.0, 160.0, 185.0])  # BGR plywood
    img *= light(x)[None, :, None]
    img += rng.normal(0, 2, img.shape)
    if holds:
        for cx in range(25, W, 50):
            for cy in range(25, H, 50):
                img[cy - 6:cy + 6, cx - 6:cx + 6] = np.array([40.0, 40.0, 200.0]) * light(x[cx])
    return {"facet": fid, "image": np.clip(img, 0, 255).astype(np.uint8),
            "mask": np.full((H, W), 255, np.uint8), "mmPerPx": RES,
            "bounds": {"aMin": 0.0, "aMax": 1500.0, "bMin": 0.0, "bMax": 1000.0}}


def _wood_level(r, a0, a1):
    img = r["image"].astype(float)
    c0, c1 = int(a0 / RES), int(a1 / RES)
    patch = img[:, c0:c1].reshape(-1, 3)
    wood = patch[patch[:, 2] < 1.4 * patch[:, 1]]  # not the red holds
    return wood.mean()


def test_groups_by_overhang_angle():
    fs = [_facet("a", 0, 45.4), _facet("b", 0, 45.2), _facet("c", 0, 0.0), _facet("d", 0, 20.0)]
    got = [sorted(f["id"] for f in g) for g in flatten.groups(fs, 3.0)]
    assert got == [["c"], ["d"], ["a", "b"]]


def test_same_overhang_facets_are_shaded_evenly_holds_keep_their_colour():
    lamp = lambda x: 1.15 - 0.45 * x / 3000.0  # bright left, dim far right corner
    facets = {"A": _facet("A", 0.0, 45.4), "B": _facet("B", 1500.0, 45.2)}
    res = [_texture("A", 0.0, lamp, seed=1), _texture("B", 1500.0, lamp, seed=2)]
    before = _wood_level(res[0], 100, 400) / _wood_level(res[1], 1100, 1400)
    hold_ratio = res[1]["image"][125, 225].astype(float) / res[1]["image"][100, 200].astype(float)
    flatten.flatten(res, facets)
    after = _wood_level(res[0], 100, 400) / _wood_level(res[1], 1100, 1400)
    assert before > 1.4 and abs(after - 1) < 0.06, (before, after)
    # a grey gain of only the lowest frequencies: a hold keeps its colour relative to the wood next to it
    new_ratio = res[1]["image"][125, 225].astype(float) / res[1]["image"][100, 200].astype(float)
    assert np.allclose(new_ratio, hold_ratio, rtol=0.06)


def test_groups_keep_their_natural_relative_brightness():
    facets = {"O": _facet("O", 0.0, 45.0), "V": _facet("V", 1500.0, 0.0)}
    res = [_texture("O", 0.0, lambda x: np.full_like(x, 0.7), seed=3),
           _texture("V", 1500.0, lambda x: np.full_like(x, 1.0), seed=4)]
    before = _wood_level(res[1], 200, 1300) / _wood_level(res[0], 200, 1300)
    flatten.flatten(res, facets)
    after = _wood_level(res[1], 200, 1300) / _wood_level(res[0], 200, 1300)
    assert abs(after / before - 1) < 0.03
