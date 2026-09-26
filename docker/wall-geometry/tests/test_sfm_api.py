"""Kind solve-sfm over HTTP (multipart request + sparse.zip) and the sparse.zip / request validation."""
import io
import json
import os
import zipfile

import numpy as np
import pytest
import sfm_scene as sc
from test_api import _wait, client  # noqa: F401 - the module-scoped client fixture

from wallgeometry.request import RequestError
from wallgeometry.sfm.colmap_io import FILES, ModelError, extract_sparse, load_model
from wallgeometry.sfm.request import parse_sfm_request


@pytest.fixture(scope="module")
def sparse_zip(tmp_path_factory):
    rng = np.random.default_rng(2)
    X, holds = sc.surfaces(rng)
    cams = sc.cameras(rng, n_photos=16, n_anchors=0)
    d = sc.write_model(str(tmp_path_factory.mktemp("m")), cams, X, sc.rot([1, 1, 0], 30), np.zeros(3), rng)
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
        for name in FILES:
            z.write(os.path.join(d, name), name)
    photos = [{"name": c["stem"], "holds": sc.detections(c, holds, rng).round(1).tolist()} for c in cams]
    return buf.getvalue(), {"photos": photos}


def form(zip_bytes, req, name="sparse.zip"):
    files = [("request", (None, json.dumps(req)))]
    if zip_bytes is not None:
        files.append(("sparse", (name, zip_bytes, "application/zip")))
    return files


def test_solve_sfm_happy_path(client, sparse_zip):  # noqa: F811
    assert "solve-sfm" in client.get("/health").json()["kinds"]
    assert client.get("/v1/info").json()["limits"]["sfmMaxSparseMb"] == 256
    r = client.post("/v1/jobs/solve-sfm", files=form(*sparse_zip))
    assert r.status_code == 202, r.text
    st = _wait(client, r.json()["jobId"])
    assert st["status"] == "succeeded", st["error"]
    doc = st["result"]["geometry"]
    assert doc["markers"] == [] and doc["world"]["frameSource"] == "features" and len(doc["segments"]) >= 1
    assert doc["world"]["gravitySource"] == "floor"  # no device gravity in this request
    assert [f["name"] for f in st["result"]["files"]] == ["wall-geometry.json"]


def test_solve_sfm_validation(client, sparse_zip):  # noqa: F811
    data, req = sparse_zip
    assert client.post("/v1/jobs/solve-sfm", files=form(None, req)).status_code == 422
    assert client.post("/v1/jobs/solve-sfm", files=form(b"not a zip", req)).status_code == 422
    assert client.post("/v1/jobs/solve-sfm", files=form(data, {"photos": "x"})).status_code == 422
    assert client.post("/v1/jobs/solve-sfm", files=form(data, req, name="sparse.tar")).status_code == 400
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as z:
        z.writestr("cameras.bin", b"x")
    assert client.post("/v1/jobs/solve-sfm", files=form(buf.getvalue(), req)).status_code == 422
    r = client.post("/v1/jobs/solve-sfm", json=req)
    assert r.status_code == 400


def test_extract_refuses_other_files_and_bombs(tmp_path):
    def write(names, size=10):
        p = tmp_path / f"{len(names)}-{size}.zip"
        with zipfile.ZipFile(p, "w", zipfile.ZIP_DEFLATED) as z:
            for n in names:
                z.writestr(n, b"\0" * size)
        return str(p)

    with pytest.raises(ModelError, match="exactly"):
        extract_sparse(write([*FILES, "../evil"]), str(tmp_path / "o1"), 1 << 20)
    with pytest.raises(ModelError, match="exactly"):
        extract_sparse(write(FILES[:3]), str(tmp_path / "o2"), 1 << 20)
    with pytest.raises(ModelError, match="too large"):
        extract_sparse(write(FILES, size=1 << 20), str(tmp_path / "o3"), 1 << 20)
    assert not (tmp_path / "evil").exists()


def test_a_model_without_stems_is_refused(tmp_path, sparse_zip):
    zipfile.ZipFile(io.BytesIO(sparse_zip[0])).extractall(tmp_path)
    (tmp_path / "stems.json").write_text(json.dumps({"version": 1, "images": {}}))
    with pytest.raises(ModelError, match="no valid entry"):
        load_model(str(tmp_path))


@pytest.mark.parametrize("bad", [
    {"photos": [{"name": "p1", "deviceGravity": [0, 1]}]}, {"photos": [{"name": "p1"}, {"name": "p1"}]},
    {"measuredDistance": {"photo": "p1", "a": [0, 0], "b": [1, 1], "mm": -3}}, {"anchors": {"a00": "p1"}},
    {"anchors": {"a00": "nope"}, "reference": {"cameras": [{"image": "p1", "R": [1, 0, 0, 0, 1, 0, 0, 0, 1],
                                                            "t": [0, 0, 0]}], "segments": []}},
    {"options": {"seed": 1, "fast": True}}, {"segments": [{"index": 0, "declaredAngleDeg": 120}]}])
def test_request_validation(bad):
    with pytest.raises(RequestError):
        parse_sfm_request(bad)
