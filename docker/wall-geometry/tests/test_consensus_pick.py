"""The consensus choice only picks photos that were rendered, and the source map names who painted."""
import numpy as np
import photowall

from wallgeometry import blend, sourcemap, views
from wallgeometry import textures as tx


def test_label_ignores_photos_without_rendered_pixels():
    fv = views.FacetViews((4, 4))
    for c, s in enumerate((1.0, 2.0, 5.0)):
        fv.add(c, np.full((4, 4), s))
    for v, w in zip(fv.views, (1.0, 1.0, 0.0)):  # photo 2 scores best but is outside the top-N: no slot
        v.fields["W"] = np.full(v.fields["S"].shape, w, np.float32)
    assert (fv.labels("S", 3) == 2).all()
    assert (fv.labels("S", 3, mask_key="W") == 1).all()


def test_drawn_cells_take_the_centre_pixel_else_any_painted_one():
    drawn = np.full((8, 12), -1, np.int16)
    drawn[:4, :4] = 3
    drawn[1, 1] = 7  # centre of cell (0, 0) at cell = 4 is pixel (2, 2): still photo 3
    drawn[4:, 4:8] = 5
    drawn[0, 9] = 2  # cell (0, 2): only a corner pixel is painted
    cells = sourcemap.drawn_cells(drawn, 4)
    assert cells.tolist() == [[3, -1, 2], [-1, 5, -1]]


def test_rendered_select_texture_paints_the_chosen_photos_and_says_so(monkeypatch):
    seen = []
    finish = blend.finish

    def spy(acc, gains, p, label=None):
        r = finish(acc, gains, p, label)
        slot = ((acc.cam == label[None]) & (acc.wt > 0)).any(0)
        seen.append(((label >= 0) & ~slot).sum() / max((label >= 0).sum(), 1))
        seen.append((r[3], p["labelCellPx"]))
        return r

    monkeypatch.setattr(blend, "finish", spy)
    doc, photos = photowall.scene(30)
    # few slots per pixel: the consensus pick often lies outside the raw top-N views
    res = tx.render_textures(doc, photos.__getitem__, set(photos), {"mmPerPx": 8.0, "blendViews": 3})
    for r, (missing, (drawn, cell)) in zip(res, zip(seen[::2], seen[1::2])):
        assert missing < 0.001, missing  # only slot overflow at a few seam pixels is left
        src = sourcemap.decode(r["source"])
        names = np.array([""] + r["source"]["cameras"])[src]
        want = np.array([""] + sorted(photos))[sourcemap.drawn_cells(drawn, cell) + 1]
        assert (names == want).all()
