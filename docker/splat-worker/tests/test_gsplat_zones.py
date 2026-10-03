"""The opaque-facet penalty's pieces (gsplat_zones: facet_hit, behind, see_through) in the trainer's Python
(torch + gsplat; skipped where they are missing)."""
import json

import numpy as np
import pytest

from test_zones import corner_doc
from splatworker.zones import spec

torch = pytest.importorskip("torch")
pytest.importorskip("gsplat")


def zone_map(tmp_path):
    from splatworker.gsplat_zones import ZoneMap
    zs = spec(corner_doc())
    zs["toWorldMm"] = np.eye(4).tolist()
    path = tmp_path / "zones.json"
    path.write_text(json.dumps(zs))
    return ZoneMap(str(path), torch.device("cpu"))


def camera(centre):
    """Looking along +y (at the main wall) from `centre`: K, world-to-camera 4x4 for a 40 x 30 image."""
    R = torch.tensor([[1.0, 0, 0], [0, 0, -1], [0, 1, 0]])
    vm = torch.eye(4)
    vm[:3, :3], vm[:3, 3] = R, -R @ torch.tensor(centre)
    return torch.tensor([[20.0, 0, 20], [0, 20, 15], [0, 0, 1]]), vm


def test_facet_hit_takes_the_nearest_facet_and_strides(tmp_path):
    zm = zone_map(tmp_path)
    K, vm = camera([1000.0, -2000, 500])
    hit = zm.facet_hit(K, vm, 40, 30)
    assert hit[15, 20] == 0  # straight ahead: the main wall
    assert hit[15, 0] == 1  # far left: the side wall (x = 0) comes first
    assert (zm.facet_hit(K, vm, 40, 30, 4) == hit[2::4, 2::4]).all()
    assert hit[15, 39] == -1  # far right: past the main wall's edge
    assert (zm.wall_pixels(K, vm, 40, 30) == (hit >= 0)).all()


def test_behind_and_see_through(tmp_path):
    zm = zone_map(tmp_path)
    pts = torch.tensor([[1000.0, 300, 500], [1000.0, 40, 500], [500.0, -300, 500]])
    assert zm.behind(pts).tolist() == [[1, 1, 0], [1, 0, 0], [1, 0, 0]]  # 300 mm behind the main wall only
    from splatworker.gsplat_zones import see_through
    img = torch.zeros(8, 8, 3)
    img[..., 0] = 1.0  # opaque everywhere ...
    img[..., 1] = 0.5  # ... half of it from behind facet 0, none from behind facet 1
    hit = torch.full((8, 8), -1, dtype=torch.long)
    hit[:4], hit[4:6] = 0, 1  # 32 pixels on facet 0, 16 on facet 1, 16 on no facet
    assert float(see_through(img, hit)) == pytest.approx(0.5 * 32 / 48)
    assert float(see_through(img, hit[1::2, 1::2], 2)) == pytest.approx(0.5 * 8 / 12)
    img[..., 0] = 0.8  # 20 % background shows through everywhere, too
    assert float(see_through(img, hit)) == pytest.approx(0.2 + 0.5 * 32 / 48)


def test_air_mask_is_the_surroundings_and_behind_a_facet(tmp_path):
    zm = zone_map(tmp_path)
    pts = torch.tensor([[1000.0, -5, 500], [1000, 40, 500], [1000, -300, 300], [1000, -150, 500]])
    assert zm.air_mask(pts).tolist() == [False, True, True, False]


def test_needle_penalty_is_stronger_in_the_air():
    from types import SimpleNamespace
    from splatworker.gsplat_model import regularisers
    a = SimpleNamespace(opacity_reg=0.0, scale_reg=0.0, aniso_reg=0.1, aniso_max=6.0, aniso_air_reg=1.0, aniso_air_max=3.0)
    params = {"opacities": torch.zeros(2), "scales": torch.log(torch.tensor([[40.0, 10, 1], [40.0, 10, 1]]))}
    assert float(regularisers(params, a)) == 0.0  # ratio 4: free on the wall
    air = float(regularisers(params, a, torch.tensor([True, False])))
    assert air == pytest.approx(np.log(4 / 3) / 2, rel=1e-4)


def test_air_needle_penalty_shortens_never_fattens():
    """The air penalty must not grow the middle axis: a fatter splat gets more of MCMC's covariance-scaled
    position noise and diffuses out of the box (capture 0cc5e3ae: 18 % of the splats outside)."""
    from types import SimpleNamespace
    from splatworker.gsplat_model import regularisers
    a = SimpleNamespace(opacity_reg=0.0, scale_reg=0.0, aniso_reg=0.1, aniso_max=6.0, aniso_air_reg=1.0, aniso_air_max=3.0)
    scales = torch.log(torch.tensor([[40.0, 10, 1], [40.0, 10, 1]])).requires_grad_()
    regularisers({"opacities": torch.zeros(2), "scales": scales}, a, torch.tensor([True, False])).backward()
    g = scales.grad[0]
    assert float(g[0]) > 0  # gradient descent shortens the longest axis
    assert float(g[1]) == 0.0 and float(g[2]) == 0.0  # and leaves the others alone
    assert scales.grad[1].abs().sum() == 0  # ratio 4 on the wall: free


def test_air_mask_is_refreshed_right_after_the_mcmc_relocation():
    """gsplat's MCMC relocates at the end of every step % refine_every == 0 (same splat count): the mask has
    to follow on the next step, not 99 steps later."""
    from splatworker.gsplat_train import air_due
    mask = torch.zeros(10, dtype=torch.bool)
    assert air_due(0, None, 10, 100)
    assert air_due(101, mask, 10, 100) and air_due(201, mask, 10, 100)
    assert not air_due(100, mask, 10, 100) and not air_due(150, mask, 10, 100)
    assert air_due(150, mask, 12, 100)  # growth appended splats
