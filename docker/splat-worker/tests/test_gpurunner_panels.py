"""Panel photo registration on the 3D runner: the capability, the bundle it accepts, the known-pose model it hands COLMAP
and the poses it reads back (COLMAP itself is not run here)."""
import io
import json
import sqlite3
import zipfile

import numpy as np
import pytest

from splatworker.gpurunner import panels
from splatworker.gpurunner import panels_colmap as pc
from splatworker.gpurunner.panels_colmap import PanelsInputError


def rot(yaw_deg):
    a = np.radians(yaw_deg)
    return np.array([[np.cos(a), 0, np.sin(a)], [0, 1, 0], [-np.sin(a), 0, np.cos(a)]])


def camera(name, x, yaw=0.0):
    R = rot(yaw)
    return {"name": name, "width": 400, "height": 300, "K": [300, 0, 200, 0, 300, 150, 0, 0, 1], "dist": [0.01, -0.002],
            "R": R.ravel().tolist(), "t": (-R @ np.array([x, 0.0, -3000.0])).tolist()}


def bundle(cameras, panel_names=("abc",), extra=()):
    out = io.BytesIO()
    with zipfile.ZipFile(out, "w") as zf:
        zf.writestr("register-job.json", json.dumps({"version": 1, "cameras": cameras, "panels": list(panel_names)}))
        for c in cameras:
            zf.writestr(f"photos/{c['name']}.jpg", b"jpg")
        for p in panel_names:
            zf.writestr(f"panels/{p}.jpg", b"jpg")
        for name, data in extra:
            zf.writestr(name, data)
    return zipfile.ZipFile(io.BytesIO(out.getvalue()))


def test_it_advertises_only_with_colmap_and_not_when_switched_off(monkeypatch):
    monkeypatch.setattr(panels.shutil, "which", lambda b: "/usr/bin/colmap")
    assert panels.advertise({}) == ["register-panels"]
    assert panels.advertise({"RUNNER_PANELS": "0"}) == []
    monkeypatch.setattr(panels.shutil, "which", lambda b: None)
    assert panels.advertise({}) == []


def test_the_bundle_needs_two_posed_photos_and_a_panel():
    job, files = panels.read_bundle(bundle([camera("p01", 0), camera("p02", 500)]))
    assert [c["name"] for c in job["cameras"]] == ["p01", "p02"] and list(files["panels"]) == ["abc"]
    with pytest.raises(PanelsInputError):
        panels.read_bundle(bundle([camera("p01", 0)]))
    with pytest.raises(PanelsInputError):
        panels.read_bundle(bundle([camera("p01", 0), camera("p02", 500)], panel_names=()))


def test_the_bundle_refuses_unexpected_files():
    with pytest.raises(PanelsInputError):
        panels.read_bundle(bundle([camera("p01", 0), camera("p02", 500)], extra=[("../evil.jpg", b"x")]))


def test_quaternions_round_trip():
    R = rot(37) @ np.array([[1, 0, 0], [0, np.cos(0.3), -np.sin(0.3)], [0, np.sin(0.3), np.cos(0.3)]])
    assert np.allclose(pc.rotation(pc.quaternion(R)), R, atol=1e-9)


def test_pairs_join_near_photos_and_every_panel_with_every_photo():
    cams = [camera(f"p{i:02d}", 400 * i) for i in range(20)] + [camera("away", 0, yaw=120)]
    names = [f"photos/{c['name']}.jpg" for c in cams]
    out = pc.pairs(cams, names, ["panels/abc.jpg"])
    assert ("photos/p00.jpg", "photos/p01.jpg") in out
    assert not any("away" in a and "photos/p" in b or "away" in b and "photos/p" in a for a, b in out)
    assert sum(1 for a, _ in out if a == "panels/abc.jpg") == len(names)


def test_the_known_model_uses_the_database_ids_and_fixes_the_intrinsics(tmp_path):
    db = tmp_path / "db.db"
    con = sqlite3.connect(db)
    con.execute("create table cameras (camera_id integer, model integer, width integer, height integer, params blob, "
                "prior_focal_length integer)")
    con.execute("create table images (image_id integer, name text, camera_id integer)")
    for i, n in ((7, "photos/p01.jpg"), (9, "photos/p02.jpg")):
        con.execute("insert into cameras values (?, 2, 400, 300, x'', 0)", (i + 1,))
        con.execute("insert into images values (?, ?, ?)", (i, n, i + 1))
    con.commit()
    con.close()
    cams = [camera("p01", 0), camera("p02", 500)]
    pc.write_known_model(str(db), str(tmp_path / "known"), cams, ["photos/p01.jpg", "photos/p02.jpg"])
    lines = (tmp_path / "known" / "images.txt").read_text().splitlines()
    assert lines[0].split()[0] == "7" and lines[0].split()[-2:] == ["8", "photos/p01.jpg"]
    row = sqlite3.connect(db).execute("select model, params, prior_focal_length from cameras where camera_id=8").fetchone()
    assert row[0] == pc.RADIAL and row[2] == 1 and np.allclose(np.frombuffer(row[1]), [300, 200, 150, 0.01, -0.002])


def test_a_photo_of_another_size_than_its_camera_is_refused(tmp_path):
    db = tmp_path / "db.db"
    con = sqlite3.connect(db)
    con.execute("create table cameras (camera_id integer, model integer, width integer, height integer, params blob, "
                "prior_focal_length integer)")
    con.execute("create table images (image_id integer, name text, camera_id integer)")
    con.execute("insert into cameras values (1, 2, 300, 400, x'', 0)")
    con.execute("insert into images values (1, 'photos/p01.jpg', 1)")
    con.commit()
    con.close()
    with pytest.raises(PanelsInputError):
        pc.write_known_model(str(db), str(tmp_path / "k"), [camera("p01", 0)], ["photos/p01.jpg"])


def test_the_registered_panels_are_read_with_their_reprojection_error(tmp_path):
    R, t, params = rot(10), np.array([100.0, -20.0, 3000.0]), [310.0, 200.0, 150.0, 0.0, 0.0]
    pts = {i + 1: np.array([x, y, 0.0]) for i, (x, y) in enumerate([(0, 0), (300, 100), (-200, 50), (100, -150)])}
    obs = []
    for pid, X in pts.items():
        c = R @ X + t
        obs += [f"{params[0] * c[0] / c[2] + params[1]:.6f}", f"{params[0] * c[1] / c[2] + params[2]:.6f}", str(pid)]
    q = pc.quaternion(R)
    (tmp_path / "cameras.txt").write_text("1 RADIAL 400 300 " + " ".join(map(str, params)) + "\n")
    (tmp_path / "points3D.txt").write_text("".join(f"{i} {X[0]} {X[1]} {X[2]} 0 0 0 0.5\n" for i, X in pts.items()))
    (tmp_path / "images.txt").write_text(
        "# header\n" + f"5 {' '.join(map(str, q))} {' '.join(map(str, t))} 1 panels/abc.jpg\n" + " ".join(obs) + "\n"
        + "6 1 0 0 0 0 0 0 1 photos/p01.jpg\n\n")
    out = pc.read_panels(str(tmp_path), {"panels/abc.jpg": "abc"})
    assert list(out) == ["abc"]
    assert out["abc"]["observations"] == 4 and out["abc"]["rmsPx"] < 1e-3
    assert np.allclose(np.reshape(out["abc"]["R"], (3, 3)), R, atol=1e-6)
