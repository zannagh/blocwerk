"""Orthophoto textures: marker squares must come out at their real size, in the right place."""
import json
import os

import cv2
import numpy as np
import pytest
import synthetic

from wallgeometry.textures import TextureError, coverage_mask, encode_png, render_textures

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


def test_mask_marks_what_the_photo_covered():
    doc, photo = synthetic.scene()
    # a 2 m margin reaches well outside the one photo, so most of the grid is uncovered
    r = render_textures(doc, lambda n: photo, {"SYN_1"}, {"extraMarginMm": 2000})[0]
    mask, img = r["mask"], r["image"]
    assert mask.shape == img.shape[:2] and mask.dtype == np.uint8
    assert r["coverage"] < 0.5
    # nothing drawn -> alpha 0; the black fill is (almost) all masked out
    assert (img[mask == 0] == 0).all()
    assert (mask[img.max(2) == 0] == 0).mean() > 0.99
    assert abs((mask > 0).mean() - r["coverage"]) < 0.02
    assert (mask == 255).any()


def test_mask_feather_ramps_inside_the_covered_area():
    filled = np.zeros((40, 40), bool)
    filled[:, 10:] = True
    m = coverage_mask(filled, 4)
    assert (m[:, :10] == 0).all()                  # never alpha outside the photo
    row = m[20, 10:20].astype(int)
    assert row[0] < 128 and (np.diff(row) >= 0).all() and row[-1] == 255
    assert (coverage_mask(filled, 0)[filled] == 255).all()
    assert not coverage_mask(np.zeros((5, 5), bool)).any()


def test_mask_png_roundtrip_is_small():
    filled = np.zeros((1000, 2000), bool)
    filled[100:900, 50:1900] = True
    png = encode_png(coverage_mask(filled))
    back = cv2.imdecode(np.frombuffer(png, np.uint8), cv2.IMREAD_UNCHANGED)
    assert back.shape == filled.shape and len(png) < 20_000
