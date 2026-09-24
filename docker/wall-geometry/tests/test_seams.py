"""Cross-facet consistency: one global gain solve, and seam harmonisation between facet textures."""
import numpy as np

from wallgeometry import exposure, seams

RES = 5.0  # mm per texture px


def _facet(fid, x0):
    return {"id": fid, "origin": [x0, 0.0, 0.0], "u": [1.0, 0.0, 0.0], "v": [0.0, 0.0, 1.0],
            "normal": [0.0, -1.0, 0.0], "extentMm": {"aMin": -50.0, "aMax": 1000.0, "bMin": -50.0, "bMax": 1000.0}}


def _texture(fid, x0, scene):
    """Facet texture over a in [-150, 1100], b in [-150, 1100] with colour scene(x, z)."""
    n = int(1250 / RES)
    a = -150 + (np.arange(n) + 0.5) * RES
    b = 1100 - (np.arange(n) + 0.5) * RES
    X, Z = np.meshgrid(a + x0, b)
    img = np.clip(scene(X, Z), 0, 255).astype(np.uint8)
    return {"facet": fid, "image": img, "mask": np.full((n, n), 255, np.uint8), "mmPerPx": RES,
            "bounds": {"aMin": -150.0, "aMax": 1100.0, "bMin": -150.0, "bMax": 1100.0}}


def _two_facets(scene_a, scene_b):
    facets = {"A": _facet("A", 0.0), "B": _facet("B", 1000.0)}
    return facets, [_texture("A", 0.0, scene_a), _texture("B", 1000.0, scene_b)]


def _strip(r, x0, a0, a1):
    """Mean colour of texture r over facet-plane a in [a0, a1) (mm), wall rows only."""
    c0, c1 = int((a0 + 150) / RES), int((a1 + 150) / RES)
    return r["image"][40:220, c0:c1].astype(float).mean()


def _wood(rng):
    def scene(X, Z):
        base = 140 + 10 * np.sin(X / 37.0) * np.cos(Z / 53.0)
        return (base + rng.normal(0, 3, X.shape))[..., None] * np.array([0.75, 0.95, 1.1])
    return scene


def test_shared_photo_ties_two_facets_into_one_gain_solve():
    rng = np.random.default_rng(3)
    scene_a = rng.uniform(60, 180, (1, 12, 12, 3)).astype(np.float32)
    scene_b = rng.uniform(60, 180, (1, 12, 12, 3)).astype(np.float32)
    truth = np.array([1.0, 1.0, 0.6])  # photo 2 only sees facet B and came out darker
    nan = np.full_like(scene_a[0], np.nan)
    # photo 0: facet A only; photo 1: both facets (the link); photo 2: facet B only
    cells_a = np.stack([scene_a[0], scene_a[0], nan])
    cells_b = np.stack([nan, scene_b[0], scene_b[0] * truth[2]])
    gains, _, npairs = exposure.fit_gains([cells_a, cells_b], min_overlap=10, prior_luma=0.0, prior_chroma=0.0)
    assert npairs == 2
    assert np.allclose(gains[2] / gains[0], 1 / truth[2], rtol=0.02)
    assert np.allclose(gains[1] / gains[0], 1.0, rtol=0.02)
    # regularised (default): corrected part of the way, never beyond, and no colour cast from brightness
    reg, _, _ = exposure.fit_gains([cells_a, cells_b], min_overlap=10)
    ratio = reg[2] / reg[0]
    assert np.all(ratio > 1.15) and np.all(ratio <= 1 / truth[2] + 1e-6)
    assert np.ptp(reg[2]) < 0.01


def test_chroma_is_pulled_harder_towards_one_than_brightness():
    rng = np.random.default_rng(4)
    scene = rng.uniform(60, 180, (12, 12, 3)).astype(np.float32)
    tint = np.array([0.8, 1.0, 1.25], np.float32)  # a photo with a colour cast, same mean log level
    cells = np.stack([scene, scene * tint])
    gains, _, _ = exposure.fit_gains([cells], min_overlap=10)
    full = np.log(gains[1] / gains[0])
    assert np.all(np.abs(full) < 0.5 * np.abs(np.log(1 / tint)) + 1e-3)


def test_seam_offset_between_facets_is_unified():
    rng = np.random.default_rng(5)
    wood = _wood(rng)
    facets, res = _two_facets(wood, lambda X, Z: wood(X, Z) * 0.7)  # facet B 30 % darker
    before = _strip(res[0], 0, 870, 970) / _strip(res[1], 1000, 30, 130)
    rep = seams.harmonise(res, facets)
    after = _strip(res[0], 0, 870, 970) / _strip(res[1], 1000, 30, 130)
    assert before > 1.35 and abs(after - 1) < 0.04, (before, after)
    assert rep["A-B"]["after"] < 0.04 < rep["A-B"]["before"]
    # far from the seam the correction fades: a mild difference stays, the edge itself has no step
    far = _strip(res[0], 0, 0, 100) / _strip(res[1], 1000, 850, 950)
    assert 1.05 < far < before


def test_real_lighting_gradient_across_the_seam_is_kept():
    rng = np.random.default_rng(6)
    wood = _wood(rng)

    def lit(X, Z):  # a lamp to the right: continuous brightening across both facets
        return wood(X, Z) * (0.6 + 0.4 * X / 2000.0)[..., None]
    facets, res = _two_facets(lit, lit)
    orig = [r["image"].astype(float) for r in res]
    seams.harmonise(res, facets)
    for r, o in zip(res, orig):
        rel = r["image"][40:220, 40:220].astype(float).mean() / o[40:220, 40:220].mean()
        assert abs(rel - 1) < 0.03, rel
    assert _strip(res[1], 1000, 800, 900) / _strip(res[0], 0, 0, 100) > 1.5
