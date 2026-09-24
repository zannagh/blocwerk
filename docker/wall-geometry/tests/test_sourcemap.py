"""The per-facet source-view map names the photo that painted each label cell."""
import numpy as np
import pytest
import synthetic

from wallgeometry import sourcemap
from wallgeometry.textures import render_textures


def test_from_cells_roundtrip_uses_only_the_used_photos():
    g = {"res": 2.0, "aMin": -10.0, "bMax": 50.0}
    lab = np.array([[-1, 2, 2], [0, 2, -1]])
    doc = sourcemap.from_cells(lab, ["A", "B", "C"], g, 8)
    assert doc["cameras"] == ["A", "C"] and doc["cellMm"] == 16.0 and doc["bits"] == 8
    assert (sourcemap.decode(doc) == [[0, 2, 2], [1, 2, 0]]).all()


def test_many_photos_switch_to_16_bit():
    names = [f"P{i}" for i in range(300)]
    lab = np.arange(300).reshape(15, 20)
    doc = sourcemap.from_cells(lab, names, {"res": 1.0, "aMin": 0.0, "bMax": 0.0}, 4)
    assert doc["bits"] == 16 and (sourcemap.decode(doc) == lab + 1).all()


@pytest.mark.parametrize("params", [{}, {"blendViews": 1}, {"blendMode": "blend"}])
def test_rendered_map_covers_the_painted_texture(params):
    doc, photo = synthetic.scene()
    r = render_textures(doc, lambda n: photo, {"SYN_1"}, params)[0]
    src = r["source"]
    cells = sourcemap.decode(src)
    assert src["cameras"] == ["SYN_1"]
    assert cells.shape == (-(-r["heightPx"] // 8), -(-r["widthPx"] // 8))
    assert src["aMin"] == pytest.approx(r["bounds"]["aMin"], abs=0.01)
    # painted cells name the photo; the map is no coarser than the texture's coverage
    cover = (r["mask"] > 0)[::8, ::8]
    assert (cells[cover] == 1).mean() > 0.95
