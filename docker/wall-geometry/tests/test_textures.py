"""Orthophoto textures: marker squares must come out at their real size, in the right place."""
import json
import os

import cv2
import pytest
import synthetic

from wallgeometry.textures import TextureError, render_textures

PNG_DIR = os.environ.get("GLYPH_PNG_DIR", "")


def test_synthetic_marker_measures_125mm():
    doc, photo = synthetic.scene()
    res = render_textures(doc, lambda n: photo, {"SYN_1"})
    assert len(res) == 1
    r = res[0]
    assert r["mmPerPx"] == 2.0
    chk = r["markerCheck"]
    assert chk["detected"] == 1
    assert abs(chk["markers"][0]["sideErrMm"]) < 1.0
    assert chk["maxPositionErrMm"] < 2.0
    b = r["bounds"]
    assert b["aMin"] < 300 and b["aMax"] > 900 and b["bMin"] < 200 and b["bMax"] > 800


def test_photo_size_mismatch_is_rejected():
    doc, photo = synthetic.scene()
    with pytest.raises(TextureError, match="solved as"):
        render_textures(doc, lambda n: cv2.resize(photo, (800, 600)), {"SYN_1"})


@pytest.mark.skipif(not os.path.isdir(PNG_DIR), reason="set GLYPH_PNG_DIR to capture 1's PNGs")
def test_capture1_textures_marker_sizes():
    here = os.path.dirname(os.path.abspath(__file__))
    path = os.path.join(here, "..", "..", "..", "tools", "glyph", "geometry", "out", "wall-geometry.json")
    with open(path) as fh:
        doc = json.load(fh)
    names = {f[:-4] for f in os.listdir(PNG_DIR) if f.endswith(".png")}
    res = render_textures(doc, lambda n: cv2.imread(os.path.join(PNG_DIR, n + ".png")), names)
    for r in res:
        if r["markerCheck"]["detected"]:
            assert r["markerCheck"]["sideRmsErrMm"] < 4.0, r["facet"]
