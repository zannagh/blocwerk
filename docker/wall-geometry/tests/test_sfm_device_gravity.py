"""Device gravity as the app sends it: per photo `deviceGravity` (the iPhone AccelerationVector, g) and `imageSize`
(the stored image, which picks the portrait/landscape axis mapping) -> gravitySource "device"."""
import numpy as np
import pytest
import sfm_scene as sc
from test_sfm import angle, by_tilt, scene  # noqa: F401  (the module-scoped synthetic scene)

from wallgeometry.sfm import solve_sfm_document
from wallgeometry.sfm.gravity import device_up
from wallgeometry.sfm.request import parse_sfm_request

UPRIGHT = np.array([[1.0, 0, 0], [0, 0, -1], [0, 1, 0]])  # world -> camera, looking along +y, world z = image up


def app_request(scene):
    """What CaptureSfmDocuments.BuildRequest sends: name, imageSize [w, h], deviceGravity [aX, aY, aZ], holds."""
    size = {c["stem"]: [c["w"], c["h"]] for c in scene["cams"]}
    return {"photos": [{"name": p["name"], "imageSize": size[p["name"]], "deviceGravity": p["deviceGravity"],
                        "holds": p["holds"]} for p in scene["photos"]], "markerSizeMm": 125}


def test_the_apps_request_parses_and_the_solver_uses_the_sensor(scene):  # noqa: F811
    req = parse_sfm_request(app_request(scene))
    assert len(req.gravity) == len(req.sizes) == 24
    doc, _ = solve_sfm_document(app_request(scene), scene["dir"])
    assert (doc["world"]["gravitySource"], doc["world"]["gravityKnown"]) == ("device", True)
    assert doc["quality"]["gravityDetail"]["device"]["photos"] == 24
    main, _, side = by_tilt(doc)
    assert main["measuredAngleDeg"] == pytest.approx(45.0, abs=0.4) and side["measuredAngleDeg"] == pytest.approx(0, abs=0.6)


@pytest.mark.parametrize("holding", sc.HOLDING)
def test_the_stored_size_picks_the_mapping(holding):
    """A square COLMAP camera says nothing about the shot's orientation; the request's imageSize does."""
    rng = np.random.default_rng(3)
    portrait = holding.startswith("portrait")
    images, vectors, sizes = [], {}, {}
    for i in range(4):
        R = sc.rot(rng.normal(size=3), rng.uniform(5, 30)) @ UPRIGHT
        up_world = np.array([0.1, -0.2, 1.0]) / np.linalg.norm([0.1, -0.2, 1.0])
        stem = f"p{i + 1:02d}"
        images.append({"stem": stem, "role": "photo", "cam": 1, "R": R})
        vectors[stem] = sc.accelerometer(R @ up_world, holding)
        sizes[stem] = (3024, 4032) if portrait else (4032, 3024)
    model = {"images": images, "cams": {1: {"width": 1000, "height": 1000}}}
    up, info = device_up(model, vectors, sizes)
    assert info["photos"] == 4 and angle(up, [0.1, -0.2, 1.0] / np.linalg.norm([0.1, -0.2, 1.0])) < 1e-6
    if portrait:
        wrong, _ = device_up(model, vectors)  # the square camera read as landscape
        assert angle(wrong, up) > 10
