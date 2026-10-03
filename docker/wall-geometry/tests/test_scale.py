"""Texture constants that are sizes on the wall (wallgeometry/scale.py): unchanged at 2 mm/px, physical
at any other resolution."""
import numpy as np
import pytest
import synthetic

from wallgeometry import scale
from wallgeometry.textures import DEFAULTS, render_textures


def test_the_default_resolution_gives_exactly_the_old_pixel_values():
    p = scale.at_resolution(dict(DEFAULTS))
    assert DEFAULTS["mmPerPx"] == scale.REFERENCE_MM_PER_PX
    for key in scale.PHYSICAL_MM:
        assert p[key] == DEFAULTS[key] and type(p[key]) is type(DEFAULTS[key]), key
    assert p == DEFAULTS


def test_every_physical_size_is_its_pixel_default_at_the_reference():
    for key, mm in scale.PHYSICAL_MM.items():
        assert mm == DEFAULTS[key] * scale.REFERENCE_MM_PER_PX, key


@pytest.mark.parametrize("res,cell,feather,mask", [(1.0, 16, 10, 8.0), (0.5, 32, 20, 16.0), (4.0, 4, 2, 2.0),
                                                   (50.0, 1, 1, 0.16)])
def test_finer_textures_get_more_pixels_for_the_same_size(res, cell, feather, mask):
    p = scale.at_resolution({**DEFAULTS, "mmPerPx": res})
    assert p["labelCellPx"] == cell and p["seamFeatherPx"] == feather and p["maskFeatherPx"] == pytest.approx(mask)
    assert isinstance(p["labelCellPx"], int) and isinstance(p["flattenDownscale"], int)
    # counts of cells and photo-pixel sizes are not scaled
    assert p["modeFilterCells"] == DEFAULTS["modeFilterCells"] and p["borderRampPx"] == DEFAULTS["borderRampPx"]


def test_a_value_the_caller_set_is_kept():
    given = {"mmPerPx": 1.0, "labelCellPx": 5}
    assert scale.at_resolution({**DEFAULTS, **given}, given)["labelCellPx"] == 5


def test_default_render_is_unchanged_by_the_scaling():
    doc, photo = synthetic.scene()
    old_px = {k: DEFAULTS[k] for k in scale.PHYSICAL_MM}
    a = render_textures(doc, lambda n: photo, {"SYN_1"})[0]
    b = render_textures(doc, lambda n: photo, {"SYN_1"}, old_px)[0]
    assert np.array_equal(a["image"], b["image"]) and np.array_equal(a["mask"], b["mask"])
    assert a["source"] == b["source"] and a["source"]["cellMm"] == 16.0


def test_the_source_map_cell_stays_16mm_at_1mm_per_px():
    doc, photo = synthetic.scene()
    r = render_textures(doc, lambda n: photo, {"SYN_1"}, {"mmPerPx": 1.0})[0]
    assert r["mmPerPx"] == 1.0 and r["source"]["cellMm"] == 16.0
    assert r["source"]["cols"] * 16 >= r["widthPx"]
