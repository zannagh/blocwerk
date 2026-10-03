"""PLY -> .splat / .spz. The .spz writer is checked against Spark 2.2's writeSpz (fixture: the first
500 splats of the feasibility study's wall, and what Spark encoded for them)."""
import os

import numpy as np
from helpers import FIXTURES

from splatworker.splatio import (SH_C0, SPLAT_DTYPE, Splats, crop_mask, read_ply, read_spz, spz_bytes,
                                 write_splat)


def from_splat_records(rec):
    c = rec["c"].astype(float) / 255
    a = np.clip(c[:, 3], 1e-6, 1 - 1e-6)
    return Splats(rec["p"].astype(float), np.log(rec["s"].astype(float)), (c[:, :3] - 0.5) / SH_C0,
                  np.log(a / (1 - a)), (rec["r"].astype(float) - 128) / 128)


def quat(xyz):
    w = np.sqrt(np.clip(1 - (xyz ** 2).sum(1), 0, 1))
    return np.c_[w, xyz]


def test_spz_matches_spark():
    ref = np.load(os.path.join(FIXTURES, "spark_spz_ref.npz"))
    rec = np.frombuffer(ref["splat"].tobytes(), dtype=SPLAT_DTYPE)
    mine = read_spz(spz_bytes(from_splat_records(rec)))
    assert mine["shDegree"] == 0
    assert np.abs(mine["xyz"] - ref["xyz"]).max() <= 2 ** -8  # Spark goes through float16 centres
    assert np.array_equal(mine["alpha"], ref["alpha"])
    assert np.abs(mine["colour"].astype(int) - ref["colour"].astype(int)).max() <= 1
    assert np.abs(mine["log_scale"] - ref["log_scale"]).max() <= 1 / 16 + 1e-9
    dots = np.abs((quat(mine["quat_xyz"]) * quat(ref["quat_xyz"])).sum(1))
    assert np.percentile(dots, 1) > 0.99  # same rotations (w ~ 0 sign flips are the same rotation)


def write_ply(path, n=50, seed=1):
    rng = np.random.default_rng(seed)
    names = ["x", "y", "z", "nx", "ny", "nz"] + [f"f_dc_{i}" for i in range(3)] + [f"f_rest_{i}" for i in range(45)] \
        + ["opacity"] + [f"scale_{i}" for i in range(3)] + [f"rot_{i}" for i in range(4)]
    data = rng.normal(size=(n, len(names))).astype("<f4")
    header = "ply\nformat binary_little_endian 1.0\nelement vertex %d\n%send_header\n" % (
        n, "".join(f"property float {p}\n" for p in names))
    with open(path, "wb") as fh:
        fh.write(header.encode() + data.tobytes())
    return dict(zip(names, data.T))


def test_ply_to_splat_roundtrip_and_crop(tmp_path):
    cols = write_ply(tmp_path / "s.ply")
    s = Splats.from_ply(read_ply(tmp_path / "s.ply"))
    assert np.allclose(s.xyz[:, 0], cols["x"]) and np.allclose(np.linalg.norm(s.rot, axis=1), 1)
    keep = crop_mask(s, np.eye(4).tolist(), [[-1, -1, -1], [1, 1, 1]], min_alpha=0.0)
    inside = np.all(np.abs(s.xyz) <= 1, axis=1)
    assert np.array_equal(keep, inside)
    shifted = np.eye(4)
    shifted[0, 3] = 100.0  # the box is in the transformed frame
    assert not crop_mask(s, shifted.tolist(), [[-1, -1, -1], [1, 1, 1]]).any()
    n = write_splat(s, tmp_path / "w.splat")
    rec = np.fromfile(tmp_path / "w.splat", dtype=SPLAT_DTYPE)
    assert n == len(rec) == 50 and os.path.getsize(tmp_path / "w.splat") == 50 * 32
    imp = rec["s"].astype(float).prod(1) * rec["c"][:, 3]
    assert imp[0] >= np.median(imp)  # sorted by importance


def test_spz_far_coordinates_lower_precision_instead_of_wrapping():
    s = Splats(np.array([[3000.0, -2500.0, 1.0]]), np.zeros((1, 3)), np.zeros((1, 3)), np.zeros(1),
               np.array([[1.0, 0, 0, 0]]))
    back = read_spz(spz_bytes(s))
    assert np.allclose(back["xyz"], s.xyz, atol=0.01)
