"""Texture memory: sparse per-photo views (views.py) must give exactly the old dense output, with
memory that follows the wall's coverage instead of photos x label cells."""
import tracemalloc

import numpy as np
import pytest
import photowall
import dense_reference

from wallgeometry import exposure, views
from wallgeometry import textures as tx


@pytest.fixture(scope="module")
def wall12():
    return photowall.scene(12)


def _same(a, b):
    assert a.keys() == b.keys()
    for k in a:
        if isinstance(a[k], np.ndarray):
            assert a[k].dtype == b[k].dtype and np.array_equal(a[k], b[k]), k
        else:
            assert a[k] == b[k], k


@pytest.mark.parametrize("params", [{}, {"blendViews": 3}, {"blendMode": "blend"}, {"blendViews": 1},
                                    {"blendViews": 4, "mmPerPx": 6.0}])
@pytest.mark.parametrize("tile_cells", [views.TILE_CELLS, 50])
def test_sparse_views_render_bit_identical_to_the_dense_reference(wall12, params, tile_cells, monkeypatch):
    # bitwise: same image, mask, source map, photo shares and exposure report as the dense code; tiny
    # tiles (one or two rows plus halo) check that the tiled steps see their neighbours like before
    monkeypatch.setattr(views, "TILE_CELLS", tile_cells)
    doc, photos = wall12
    params = {"mmPerPx": 8.0, **params}
    want = dense_reference.render_textures(doc, photos.__getitem__, set(photos), params)
    got = tx.render_textures(doc, photos.__getitem__, set(photos), params)
    assert len(want) == len(got) == 2
    for a, b in zip(want, got):
        _same(a, b)


def test_views_keep_only_what_each_photo_sees():
    doc, photos = photowall.scene(50, distance=800.0)
    p = tx.scale.at_resolution({**tx.DEFAULTS, "mmPerPx": 8.0}, {"mmPerPx": 8.0})
    cams = {c["image"]: tx._cam(c) for c in doc["cameras"]}
    names = sorted(cams)
    facets = list(tx._facets(doc))
    jobs = tx._score_facets(facets, cams, names, p, tx.occluders(facets, doc))
    dense = len(names) * sum(j["views"].shape[0] * j["views"].shape[1] for j in jobs)
    held = sum(j["views"].cells() for j in jobs)
    assert held < 0.3 * dense
    # the blend budget counts them
    pixels = [j["g"]["W"] * j["g"]["H"] for j in jobs]
    assert tx.blend_bytes(jobs, p) == ((p["blendViews"] + 2) * 7 * sum(pixels) + 4 * max(pixels)
                                       + views.BYTES_PER_CELL * held)


def _peak(fn, *args):
    tracemalloc.start()
    try:
        fn(*args)
        return tracemalloc.get_traced_memory()[1]
    finally:
        tracemalloc.stop()


def test_peak_allocation_50_photos_well_below_dense():
    doc, photos = photowall.scene(50, distance=800.0)
    args = (doc, photos.__getitem__, set(photos), {"mmPerPx": 8.0})
    dense = _peak(dense_reference.render_textures, *args)
    sparse = _peak(tx.render_textures, *args)
    assert sparse < 0.5 * dense, (sparse, dense)


def test_gain_fit_subsampling_bounds_the_pair_work_and_stays_close():
    rng = np.random.default_rng(3)
    scene = rng.uniform(40, 200, (60, 80, 3)).astype(np.float32)
    truth = rng.uniform(0.85, 1.15, (8, 3))
    blocks = []
    for c in range(8):  # photo c sees columns 8c .. 8c + 24
        x0 = 8 * c
        col = scene[:, x0:x0 + 24] / truth[c]
        blocks.append((c, 0, x0, exposure.log_samples(col * rng.normal(1, 0.01, col.shape).astype(np.float32))))
    full, _, n_full = exposure.fit_gains_blocks([blocks], 8, 10, 0.0, 0.0, max_cells=0)
    sub, _, n_sub = exposure.fit_gains_blocks([blocks], 8, 10, 0.0, 0.0, max_cells=2000)
    assert n_full == n_sub == 7 + 6  # only overlapping crops are paired (neighbours and next-but-one)
    rel = lambda g: g / g[0]
    assert not np.array_equal(sub, full)  # the subset was really used
    assert np.allclose(rel(sub), rel(full), atol=0.005)
    assert np.allclose(rel(full), rel(truth), atol=0.02)
