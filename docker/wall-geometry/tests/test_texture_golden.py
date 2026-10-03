"""Frozen texture output of the synthetic photo wall (tests/photowall.py), guarding what the dense
reference (dense_reference.py) cannot: it shares blended._result, blend.finish and the source map.

The golden values were generated on branch fix-texture-memory-and-consensus at the follow-up commit
to ac6871b (select mode: identical to ac6871b; blend mode: with the dominant photo ranked by the
combine's real weights). Regenerate deliberately with `PYTHONPATH=.:tests python tests/test_texture_golden.py`
and say why in the commit.

Source maps and photo shares are compared exactly as decoded cells / rounded shares; a few cells or a
share's last digit may differ across CPU architectures, so up to 0.5 % of cells and 0.002 per share
are tolerated. The image is compared through 32 x 32 px block means (within 1 grey level): OpenCV's
SIMD paths may round single pixels differently per platform, so a raw byte hash would be flaky.
"""
import json
import os

import numpy as np
import photowall

from wallgeometry import sourcemap
from wallgeometry import textures as tx

FIXTURE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "fixtures", "photowall-golden.json")
CONFIGS = {"select": {"mmPerPx": 8.0, "blendViews": 3}, "blend": {"mmPerPx": 8.0, "blendMode": "blend"}}
BLOCK = 32


def _fingerprint(img):
    h, w = img.shape[0] // BLOCK * BLOCK, img.shape[1] // BLOCK * BLOCK
    b = img[:h, :w].astype(np.float64).reshape(h // BLOCK, BLOCK, w // BLOCK, BLOCK, 3).mean((1, 3))
    return np.round(b, 1)


def _render():
    doc, photos = photowall.scene(30)
    out = {}
    for name, params in CONFIGS.items():
        res = tx.render_textures(doc, photos.__getitem__, set(photos), params)
        out[name] = [{"facet": r["facet"], "source": r["source"], "photosUsed": r["photosUsed"],
                      "image": _fingerprint(r["image"]).tolist()} for r in res]
    return out


def _cells(src):
    return np.array([""] + src["cameras"])[sourcemap.decode(src)]


def test_texture_output_matches_the_frozen_golden():
    with open(FIXTURE) as fh:
        want = json.load(fh)
    got = _render()
    for name in CONFIGS:
        assert len(got[name]) == len(want[name])
        for g, w in zip(got[name], want[name]):
            assert g["facet"] == w["facet"]
            assert (_cells(g["source"]) != _cells(w["source"])).mean() <= 0.005, (name, g["facet"])
            gu, wu = g["photosUsed"], w["photosUsed"]
            assert all(abs(gu.get(k, 0.0) - wu.get(k, 0.0)) <= 0.002 for k in gu.keys() | wu.keys())
            assert np.abs(np.array(g["image"]) - np.array(w["image"])).max() <= 1.0, (name, g["facet"])


if __name__ == "__main__":
    with open(FIXTURE, "w") as fh:
        json.dump(_render(), fh, separators=(",", ":"))
