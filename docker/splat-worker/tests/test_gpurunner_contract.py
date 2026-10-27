"""The runner envelope contract, checked against the other side's real output (docker/compute-jobs-protocol.md).

The Blocwerk server (C#, Runners/RunnerComputeEnvelope.cs and RunnerComputeResult.cs) and this runner (Python) each write
one half and read the other. Neither may test only against its own fixtures, so the files in
compute-jobs-py/tests/contract/ are checked in:
  envelope-prepare.zip, envelope-finish.zip   written by the server; unpacked here, and the server's test (GpuRunnerContractTests)
                                              fails when its writer stops producing exactly these
  result-prepare.zip, result-finish.zip       written by this runner (pack_result); the server's test validates and reads
                                              them, and the test here fails when pack_result stops producing exactly these
A change to either writer is a change to the contract: regenerate with BLOCWERK_UPDATE_CONTRACT=1 on both sides
(pytest here, `dotnet test --filter GpuRunnerContractTests` there) and commit the files in both."""
import json
import os
import shutil
import zipfile

import pytest

from splatworker.gpurunner import compute_envelope
from splatworker.gpurunner.compute_envelope import pack_result, status_doc, unpack

HERE = os.path.dirname(os.path.abspath(__file__))
UPDATE = os.environ.get("BLOCWERK_UPDATE_CONTRACT") == "1"
CREATED = "2026-01-01T00:00:00+00:00"
JOB_ID = "11111111-2222-3333-4444-555555555555"
PREPARED = {"version": 2, "quality": "high", "points": 1200, "photoStems": ["IMG_0001", "IMG_0002", "IMG_0003"]}
# what the result zips of a prepare and a finish job hold: (kind, the job function's payload, the files' bytes)
RESULTS = {
    "prepare": ("splat-prepare",
                {"bundle": {"images": 3, "bytes": 11}, "quality": "high", "files": ["bundle.zip", "prepared.json", "sparse.zip"],
                 "stats": {"photos": 3, "registeredImages": 3, "sparsePoints": 1200}},
                {"bundle.zip": b"bundle-bytes", "prepared.json": json.dumps(PREPARED).encode(), "sparse.zip": b"sparse-bytes"}),
    "finish": ("splat-finish",
               {"frame": {"scale": 1.0}, "quality": "high", "files": ["wall.splat", "frame.json"], "stats": {"splats": 1000}},
               {"wall.splat": b"splat-bytes", "frame.json": b'{"scale":1.0}'}),
}


def contract_dir():
    for rel in (("..", "..", "compute-jobs-py", "tests", "contract"), ("..", "tests-shared", "contract")):  # tree, image
        path = os.path.normpath(os.path.join(HERE, *rel))
        if os.path.isdir(path):
            return path
    raise FileNotFoundError("compute-jobs-py/tests/contract not found")


def entries(path):
    """[(name, stored?, bytes)] of a zip, in order."""
    with zipfile.ZipFile(path) as zf:
        return [(i.filename, i.compress_type == zipfile.ZIP_STORED, zf.read(i)) for i in zf.infolist()]


@pytest.mark.parametrize("name, kind, photos", [("prepare", "splat-prepare", 5), ("finish", "splat-finish", 0)])
def test_the_servers_envelope_unpacks_into_a_job_directory(tmp_path, name, kind, photos):
    path = os.path.join(contract_dir(), f"envelope-{name}.zip")
    assert all(stored for _, stored, _ in entries(path)) and entries(path)[0][0] == "parts.json"
    assert unpack(path, str(tmp_path / "job"), kind) == photos
    made = sorted(os.listdir(tmp_path / "job"))
    if kind == "splat-prepare":
        assert made == ["arrived", "geometry.json", "inputs.json"]
        inputs = json.load(open(tmp_path / "job" / "inputs.json"))
        assert sorted(inputs["photos"]) == ["IMG_0001", "IMG_0002", "IMG_0003", "a00", "vf_0001"]
        assert inputs["anchors"] == ["a00"] and inputs["options"]["quality"] == "high"
    else:
        assert made == ["prepared.json", "splat.ply", "trainStats.json"]


def build_result(tmp_path, kind, payload, files, monkeypatch):
    monkeypatch.setattr(compute_envelope, "now_iso", lambda: CREATED)
    job = tmp_path / "job"
    job.mkdir()
    for name, data in files.items():
        (job / name).write_bytes(data)
    out = tmp_path / "result.zip"
    pack_result(str(job), status_doc(JOB_ID, kind, payload, CREATED), str(out))
    return out


@pytest.mark.parametrize("name", sorted(RESULTS))
def test_the_runners_result_is_the_checked_in_golden_the_server_reads(tmp_path, monkeypatch, name):
    kind, payload, files = RESULTS[name]
    made = build_result(tmp_path, kind, payload, files, monkeypatch)
    golden = os.path.join(contract_dir(), f"result-{name}.zip")
    if UPDATE:
        shutil.copyfile(made, golden)
    assert [n for n, _, _ in entries(golden)] == ["status.json", *files] and all(st for _, st, _ in entries(golden))
    assert entries(made) == entries(golden), "pack_result no longer writes the checked-in golden: regenerate it (BLOCWERK_UPDATE_CONTRACT=1)"
