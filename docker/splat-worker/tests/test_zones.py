"""The wall zones (zones.py): slab / surroundings box / outside, and the export cut (zone_run.py)."""
import json

import numpy as np
import pytest

from splatworker import zone_run
from splatworker.gsplat_data import eval_split
from splatworker.options import SplatOptions
from splatworker.parsers import GsplatParser
from splatworker.splatio import Splats
from splatworker.zones import OUTSIDE, SURROUND, WALL, ZoneParams, classify, counts, cut_mask, spec


def wall_doc():
    """One vertical facet 2 m wide, 1 m high, facing -y (the room), plus a marker 1.2 m up."""
    return {"segments": [{"facets": [{"id": "0", "origin": [0, 0, 0], "u": [1, 0, 0], "v": [0, 0, 1],
                                      "normal": [0, -1, 0],
                                      "extentMm": {"aMin": 0, "aMax": 2000, "bMin": 0, "bMax": 1000}}]}],
            "markers": [{"cornersWorldMm": [[100, 0, 1200], [200, 0, 1200], [200, 0, 1100], [100, 0, 1100]]}]}


def test_spec_box_reaches_the_margin_and_the_floor_band():
    zs = spec(wall_doc(), ZoneParams(box_margin_mm=400, box_floor_mm=150))
    assert zs["boxLo"] == [-400, -400, -150] and zs["boxHi"] == [2400, 400, 1600]  # top: the marker + 400
    assert zs["floorMm"] == 0
    assert spec({"segments": []}) is None


@pytest.mark.parametrize("p,zone", [((1000, -100, 500), WALL), ((1000, -249, 500), WALL),
                                    ((-90, -100, 500), WALL),  # the outline grown by the slab margin
                                    ((2300, -300, 500), SURROUND), ((1000, 100, 500), SURROUND),
                                    ((1000, -300, -100), SURROUND), ((1000, -300, 400), SURROUND),  # floor band
                                    ((1000, -300, 500), OUTSIDE),  # the air in front of the wall: a post
                                    ((1000, -1000, 500), OUTSIDE),
                                    ((1000, -300, -200), OUTSIDE), ((3000, 0, 500), OUTSIDE)])
def test_classify(p, zone):
    zs = spec(wall_doc())
    assert classify(np.array([p], float), zs)[0] == zone


def splat_set(rows):
    """rows: (world centre, scale mm (3,), alpha); identity rotation."""
    xyz = np.array([r[0] for r in rows], float)
    scale = np.array([r[1] for r in rows], float)
    alpha = np.array([r[2] for r in rows], float)
    return xyz, scale, alpha, np.tile([1.0, 0, 0, 0], (len(rows), 1))


def test_cut_keeps_the_wall_whole_and_tidies_the_surroundings():
    zs = spec(wall_doc())
    xyz, scale, alpha, quat = splat_set([
        ((1000, -100, 500), (200, 10, 1), 0.05),  # WALL: a faint needle, kept (the slab keeps everything)
        ((1000, -100, 600), (10, 10, 1), 0.01),  # WALL: dead (MCMC left it at ~0 opacity): dropped
        ((2300, -300, 500), (10, 10, 10), 0.9),  # SURROUND: a compact splat, kept
        ((2300, -300, 1550), (10, 10, 60), 0.9),  # SURROUND: reaches 120 mm (2 sigma) up, 70 past the top
        ((2300, -300, 800), (10, 10, 100), 0.9),  # SURROUND: a needle
        ((2300, -300, 300), (50, 50, 5), 0.03),  # SURROUND: faint haze
        ((1000, -1000, 500), (10, 10, 10), 0.9),  # OUTSIDE
    ])
    keep, rep = cut_mask(xyz, scale, alpha, quat, np.eye(3), zs)
    assert keep.tolist() == [True, False, True, False, False, False, False]
    assert rep["removed"] == {"outside": 1, "dead": 1, "fray": 1, "needle": 1, "haze": 1}
    assert rep["zones"] == {"wall": 2, "surround": 4, "outside": 1} and rep["kept"] == 2


def test_cut_measures_the_fray_along_the_rotated_axes():
    zs = spec(wall_doc())
    # 60 mm along x (2 sigma = 120 mm): rotated 90 degrees about y it points up, out of the top by 70 mm
    xyz, scale, alpha, _ = splat_set([((2300, -300, 1550), (60, 10, 10), 0.9)])
    quat = np.array([[np.cos(np.pi / 4), 0, np.sin(np.pi / 4), 0]])
    zp = ZoneParams(needle_ratio=100)  # not a needle here: only the fray counts
    zs["params"] = zp.to_dict()
    assert not cut_mask(xyz, scale, alpha, quat, np.eye(3), zs)[0][0]
    assert cut_mask(xyz, scale, alpha, np.array([[1.0, 0, 0, 0]]), np.eye(3), zs)[0][0]  # lying flat: inside


def test_write_zones_aligns_the_photos_and_export_cut_sets_the_crop(tmp_path):
    doc = wall_doc()
    # photo cameras in the world, and the same centres in a COLMAP frame scaled 1/1000 and shifted
    cams = {"p01": (0, -2000, 500), "p02": (2000, -2000, 500), "p03": (1000, -2500, 1500), "p04": (500, -1800, 0)}
    doc["cameras"] = [{"image": k, "R": np.eye(3).flatten().tolist(), "t": (-np.array(c, float)).tolist()}
                      for k, c in cams.items()]
    model = {"images": {f"g/{k}.jpg": (np.array(c, float) / 1000 + 5) for k, c in cams.items()}}
    model["images"]["v/vf_0001.jpg"] = np.array([99.0, 99, 99])  # frames never align
    path = str(tmp_path / "train" / "zones.json")
    frame, zs = zone_run.write_zones(path, model, doc, zone_run.zone_params(SplatOptions()))
    saved = json.load(open(path))
    assert np.allclose(np.array(saved["toWorldMm"])[:3, :3], np.eye(3) * 1000, atol=1e-6)
    assert frame["alignment"]["cameras"] == 4
    colmap = np.array([[1.0, -0.1, 0.5], [1.0, -1.0, 0.5]]) + 5  # world (1000, -100, 500) and (1000, -1000, 500)
    s = Splats(colmap, np.log(np.full((2, 3), 0.01)), np.zeros((2, 3)), np.full(2, 3.0), np.tile([1.0, 0, 0, 0], (2, 1)))
    keep, rep = zone_run.export_cut(s, frame, zs, doc)
    assert keep.tolist() == [True, False] and rep["zones"]["outside"] == 1
    lo, hi = frame["crop"]  # viewer metres, origin = the reference facet's centre (1000, 0, 500)
    assert np.allclose(lo, [-1.4, -0.65, -0.4]) and np.allclose(hi, [1.4, 1.1, 0.4])


def test_counts():
    assert counts(np.array([0, 0, 1, 2], np.int8)) == {"wall": 2, "surround": 1, "outside": 1}


@pytest.mark.parametrize("names,every,held", [
    ([f"g/p{i:02d}.jpg" for i in range(48)], 8, [0, 8, 16, 24, 32, 40]),
    ([f"g/p{i:02d}.jpg" for i in range(10)], 0, []), (["g/p01.jpg"], 8, []),
    ([f"g/p{i}.jpg" for i in range(5)], 1, [0, 1, 2, 3]), ([f"g/p{i}.jpg" for i in range(7)], 8, [0]),
    # photos only: the frames (sorted after the photos' groups here) always train
    ([f"geo/p{i:02d}.jpg" for i in range(10)] + [f"video/vf_{i:04d}.jpg" for i in range(30)], 4, [0, 4, 8]),
    ([f"video/vf_{i:04d}.jpg" for i in range(10)], 5, [0, 5]),  # frames only: every 5th frame
])
def test_eval_split_holds_out_photos_only(names, every, held):
    train, test = eval_split(names, every)
    assert test == held and sorted(train + test) == list(range(len(names))) and train


def test_parser_reads_the_wall_scores_and_zone_counts():
    p = GsplatParser()
    p("eval psnr 27.412 ssim 0.8631 views 6")
    p("eval wall psnr 25.100 ssim 0.8100 views 5")
    p('zones {"wall": 900, "surround": 90, "outside": 10}')
    assert p.eval == {"psnr": 27.412, "ssim": 0.8631, "views": 6,
                      "wall": {"psnr": 25.1, "ssim": 0.81, "views": 5}}
    assert p.zones == {"wall": 900, "surround": 90, "outside": 10}
