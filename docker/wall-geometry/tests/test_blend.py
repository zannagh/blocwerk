"""Multi-view texture blending: occluder rejection, exposure gains, consensus choice."""
import numpy as np
import synthetic

from wallgeometry import blend, consensus, exposure
from wallgeometry.textures import render_textures


def _wall(h=32, w=32, seed=0):
    rng = np.random.default_rng(seed)
    base = np.full((h, w, 3), (120, 160, 200), np.float32)
    return np.clip(base + rng.normal(0, 3, base.shape), 0, 255).astype(np.uint8)


def test_occluder_in_one_of_four_views_is_rejected():
    wall = _wall()
    rgb = np.stack([wall] * 4)
    rgb[0, 8:24, 8:24] = (40, 40, 40)  # a dark rafter, seen by the sharpest view only
    wt = np.array([4.0, 1.0, 1.0, 1.0], np.float32)[:, None, None] * np.ones((4, 32, 32), np.float32)
    out, keep = blend.robust_combine(rgb, wt, delta_e=12.0, blur=0.0)
    assert not keep[0, 12:20, 12:20].any()
    assert np.abs(out[12:20, 12:20] - wall[12:20, 12:20]).max() < 12
    assert keep[0, :4, :4].all()  # outside the rafter the best view is used


def test_view_weights_fade_out_at_the_top_n_boundary():
    S = np.array([[[4.0]], [[3.0]], [[2.0]], [[1.0]]])
    W = blend.view_weights(S, 2, 0.0)[:, 0, 0]
    assert W[0] > W[1] > 0 and W[2] == 0 and W[3] == 0


def test_known_gain_is_recovered():
    rng = np.random.default_rng(1)
    scene = rng.uniform(40, 200, (1, 20, 30, 3)).astype(np.float32)
    truth = np.array([[1.0, 1.0, 1.0], [0.8, 1.1, 1.25], [1.3, 1.0, 0.9]])
    cells = scene / truth[:, None, None, :]  # photo i sees scene / gain_i
    cells[2, :, :10] = np.nan  # partial overlap
    gains, ref, npairs = exposure.fit_gains([cells.astype(np.float32)], min_overlap=10, prior_luma=0.0,
                                           prior_chroma=0.0)
    rel = gains / gains[ref]
    want = truth / truth[ref]
    assert npairs == 3
    assert np.allclose(rel, want, atol=0.02)


def test_gain_luts_apply_per_photo():
    lut = exposure.gain_luts(np.array([[1.0, 1.0, 1.0], [2.0, 0.5, 1.0]]))
    rgb = np.full((2, 1, 1, 3), 100, np.uint8)
    cam = np.array([[[0]], [[1]]], np.int16)
    out = exposure.apply_luts(rgb, cam, lut)
    assert out[0, 0, 0].tolist() == [100, 100, 100] and out[1, 0, 0].tolist() == [200, 50, 100]


def test_consensus_penalises_the_disagreeing_photo():
    cells = np.full((4, 6, 6, 3), 150.0, np.float32)
    cells[1, 2:4, 2:4] = (30.0, 30.0, 30.0)
    S = np.ones((4, 6, 6))
    S[1] = 2.0  # the occluded photo is the sharpest
    P = consensus.penalised_scores(S, cells, np.ones((4, 3)),
                                     {**consensus.CONSENSUS_DEFAULTS, "consensusBlurCells": 1})
    assert P[1, 2, 2] < P[0, 2, 2] and P[1, 0, 0] == 2.0


def test_default_render_matches_marker_geometry_and_single_mode_still_works():
    doc, photo = synthetic.scene()
    for params in ({}, {"blendViews": 1}, {"blendMode": "blend"}):
        r = render_textures(doc, lambda n: photo, {"SYN_1"}, params)[0]
        assert r["markerCheck"]["detected"] == 1, params
        assert abs(r["markerCheck"]["markers"][0]["sideErrMm"]) < 1.0, params
        assert r["coverage"] > 0.2
