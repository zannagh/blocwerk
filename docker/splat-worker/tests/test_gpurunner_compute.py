"""splat-prepare / splat-finish on the 3D runner (gpurunner/compute.py) against the fake Blocwerk server: the compute
envelope unpacks into the job directory the HTTP upload builds, a finish makes the HTTP job's files byte for byte, the
capabilities are advertised only when the machine can do the work, and an old server never sees any of it."""
import io
import json
import multiprocessing as mp
import os
import shutil
import threading
import zipfile

import pytest
from test_api import GEOMETRY, client, form, jpeg  # noqa: F401 - the module-scoped HTTP client
from test_gpurunner import KEY, fast, make_bundle, run_runner, server  # noqa: F401 - fixtures
from test_roundtrip import CAMS, WallColmap, colmap, job_dir, scene  # noqa: F401
from test_split import STATE

from computejobs.child import JobError
from computejobs.settings import settings
from splatworker import brush
from splatworker.finish import run_finish
from splatworker.gpurunner import caps as caps_module
from splatworker.gpurunner import compute, compute_child
from splatworker.gpurunner.client import Stopped
from splatworker.gpurunner import compute_envelope
from splatworker.gpurunner.compute_envelope import EnvelopeError, status_doc, unpack
from splatworker.options import SplatOptions
from splatworker.prepare import run_prepare
from splatworker.procs import MemoryLimitError
from splatworker.slimply import write_slim_ply


def envelope(kind, parts, extra=(), manifest=None):
    """The server's bundle: parts = [(name, file name | None, bytes | json value)], stored in a zip with parts.json."""
    out, listed = io.BytesIO(), []
    with zipfile.ZipFile(out, "w", zipfile.ZIP_STORED) as zf:
        for name, file, value in parts:
            if file is None:
                listed.append({"name": name, "json": value})
            else:
                listed.append({"name": name, "file": file})
                zf.writestr(file, value)
        for name, data in extra:
            zf.writestr(name, data)
        zf.writestr("parts.json", json.dumps(manifest or {"version": 1, "service": "splat", "kind": kind,
                                                          "parts": listed}))
    return out.getvalue()


def prepare_envelope(photos, options=None, geometry=None):
    parts = [("photos", name, data) for name, data in photos]
    parts.append(("options", None, options or {}))
    if geometry is not None:
        parts.append(("geometry", "geometry.json", json.dumps(geometry)))
    return envelope("splat-prepare", parts)


def finish_envelope(prepared, ply, stats=None, scene_name="splat.ply"):
    return envelope("splat-finish", [("prepared", None, prepared), ("splat", scene_name, ply),
                                     ("trainStats", None, stats or {})])


def timeless(doc):
    """The document without the stages' wall-clock times (two runs never agree on those)."""
    if isinstance(doc, dict):
        return {k: timeless(v) for k, v in doc.items() if k != "stageSeconds"}
    return [timeless(v) for v in doc] if isinstance(doc, list) else doc


def tree(root):
    return {os.path.relpath(os.path.join(d, f), root): open(os.path.join(d, f), "rb").read()
            for d, _, files in os.walk(root) for f in files}


def result_zip(srv):
    zf = zipfile.ZipFile(io.BytesIO(srv.results[0]["body"]))
    return json.loads(zf.read("status.json")), zf


def claimed(srv, kind, **extra):
    srv.job_extra = {"kind": kind, **extra}


# --- the envelope ---------------------------------------------------------------------------------------------------

def test_a_prepare_envelope_unpacks_into_the_http_uploads_job_directory(tmp_path, client):  # noqa: F811
    options = {"maxSteps": 5000, "quality": "max"}
    photos = [("a.jpg", jpeg()), ("p1.jpg", jpeg()), ("p2.jpg", jpeg(color=(1, 2, 3)))]
    r = client.post("/v1/jobs/splat-prepare", files=form(photos, options, GEOMETRY))
    assert r.status_code == 202, r.text
    http_dir = os.path.join(settings.work_dir, r.json()["jobId"])

    assert unpack_to(tmp_path / "env", prepare_envelope(photos, options, GEOMETRY)) == 3
    assert tree(tmp_path / "env") == {k: v for k, v in tree(http_dir).items()}


def unpack_to(dest, data, kind="splat-prepare"):
    path = dest.parent / f"{dest.name}.zip"
    path.write_bytes(data)
    return unpack(str(path), str(dest), kind)


def test_a_finish_envelope_unpacks_into_the_http_uploads_job_directory(tmp_path, client):  # noqa: F811
    r = client.post("/v1/jobs/splat-finish", files=[
        ("prepared", (None, json.dumps(STATE))), ("splat", ("splat.ply", b"ply\n", "application/octet-stream")),
        ("trainStats", (None, json.dumps({"steps": 1})))])
    assert r.status_code == 202, r.text
    http_dir = os.path.join(settings.work_dir, r.json()["jobId"])
    unpack_to(tmp_path / "env", finish_envelope(STATE, b"ply\n", {"steps": 1}), "splat-finish")
    assert tree(tmp_path / "env") == tree(http_dir)


BAD = [
    (envelope("splat-prepare", [], manifest={"version": 2, "service": "splat", "kind": "splat-prepare", "parts": []}),
     "splat-prepare", "version"),
    (envelope("splat-finish", []), "splat-prepare", "kind"),
    (envelope("splat-prepare", [("photos", "../p0.jpg", jpeg())]), "splat-prepare", "invalid part"),
    (envelope("splat-prepare", [("photos", "p0.jpg", jpeg())], extra=[("other.bin", b"x")]), "splat-prepare",
     "exactly the files"),
    (envelope("splat-prepare", [("evil", None, 1)]), "splat-prepare", "invalid part"),
    (envelope("splat-prepare", [("photos", "p0.exe", b"x")]), "splat-prepare", "file name"),
    (envelope("splat-prepare", [("photos", "p0.jpg", jpeg()), ("photos", "x/p0.jpg", jpeg())]), "splat-prepare",
     "unique"),
    (envelope("splat-prepare", [("photos", "p0.jpg", jpeg()), ("options", None, {}), ("options", None, {})]),
     "splat-prepare", "twice"),
    (envelope("splat-prepare", [("photos", "p0.jpg", jpeg()), ("options", None, {})]), "splat-prepare",
     "at least 3 photos"),
    (envelope("splat-prepare", [("photos", "p0.jpg", b"not a jpeg")] * 1, ), "splat-prepare", "photo p0"),
    (envelope("splat-finish", [("prepared", None, {"version": 1}), ("splat", "splat.ply", b"x")]), "splat-finish",
     "prepared"),
    (envelope("splat-finish", [("prepared", None, STATE), ("splat", "x.exe", b"x")]), "splat-finish", "splat.ply"),
    (b"not a zip", "splat-prepare", "bad envelope"),
    (envelope("splat-prepare", [("photos", "p:0.jpg", jpeg())]), "splat-prepare", "invalid part"),
]


@pytest.mark.parametrize("data, kind, why", BAD, ids=[b[2] for b in BAD])
def test_a_bad_envelope_is_refused(tmp_path, data, kind, why):
    with pytest.raises(EnvelopeError, match=why):
        unpack_to(tmp_path / "w", data, kind)


def test_two_entries_of_one_name_are_refused(tmp_path):
    with pytest.warns(UserWarning, match="Duplicate name"):
        data = envelope("splat-prepare", [("photos", "p0.jpg", jpeg())], extra=[("p0.jpg", jpeg())])
    with pytest.raises(EnvelopeError, match="two entries of one name"):
        unpack_to(tmp_path / "env", data)


def test_more_entries_than_the_photo_limit_allows_are_refused(tmp_path, monkeypatch):
    monkeypatch.setattr(compute_envelope.settings, "max_photos", 2)
    monkeypatch.setattr(compute_envelope, "EXTRA_ENTRIES", 1)  # parts.json + options + 3 photos > 2 + 1
    with pytest.raises(EnvelopeError, match="too many entries"):
        unpack_to(tmp_path / "env", prepare_envelope([(f"p{i}.jpg", jpeg()) for i in range(3)]))


def test_a_manifest_carrying_a_prepared_near_its_cap_is_read(tmp_path, monkeypatch):
    """The inline `prepared` may be as large as its own cap, so parts.json (it plus the framing) gets headroom."""
    cap = len(json.dumps(STATE)) + 400
    monkeypatch.setattr(compute_envelope, "MAX_JSON_BYTES", cap)
    monkeypatch.setattr(compute_envelope, "MANIFEST_BYTES", cap + 400)
    prepared = {**STATE, "pad": "x" * (cap - len(json.dumps(STATE)) - 20)}
    data = finish_envelope(prepared, b"ply\n")
    assert len(zipfile.ZipFile(io.BytesIO(data)).read("parts.json")) > cap
    unpack_to(tmp_path / "env", data, "splat-finish")


def test_a_scene_past_the_result_limit_is_refused(tmp_path, monkeypatch):
    monkeypatch.setattr(settings, "max_result_bytes", 10)
    with pytest.raises(EnvelopeError, match="exceeds"):
        unpack_to(tmp_path / "w", finish_envelope(STATE, b"x" * 100), "splat-finish")


# --- the capabilities -----------------------------------------------------------------------------------------------

def test_prepare_needs_colmap_and_the_cpu_switch_finish_needs_the_package(monkeypatch):
    monkeypatch.setattr(compute.shutil, "which", lambda name: "/bin/colmap")
    assert compute.advertise({}) == ["splat-prepare", "splat-finish"]
    assert compute.advertise({"RUNNER_SPLAT_CPU": "0"}) == ["splat-finish"]
    monkeypatch.setattr(compute.shutil, "which", lambda name: None)
    assert compute.advertise({}) == ["splat-finish"]
    monkeypatch.setattr(compute, "_importable", lambda module: False)
    assert compute.advertise({}) == []


def test_hello_carries_the_compute_capabilities(tmp_path, fast, server, monkeypatch):  # noqa: F811
    monkeypatch.setattr(caps_module.textures, "advertise", lambda: {"capabilities": ["splat"], "texturesMemoryMb": None})
    monkeypatch.setattr(caps_module.holds, "advertise", lambda: [])
    monkeypatch.setattr(caps_module.panels, "advertise", lambda: [])
    monkeypatch.setattr(caps_module.compute, "advertise", lambda: ["splat-prepare", "splat-finish"])
    monkeypatch.setattr(caps_module, "gpu_info", lambda: ("Test GPU", 8192, False))
    caps = caps_module.Capabilities()
    monkeypatch.setattr(type(caps), "version", property(lambda self: "0.3.0"))
    assert caps()["capabilities"] == ["splat", "splat-prepare", "splat-finish"]


def test_an_old_server_that_knows_none_of_it_still_gets_splat_jobs_trained(tmp_path, fast, server,  # noqa: F811
                                                                           monkeypatch):
    from test_gpurunner import brush_ok
    monkeypatch.setattr(brush, "train", brush_ok)
    srv = server(make_bundle(tmp_path))  # a claim without a kind: what a server before the compute kinds sends
    runner, code = run_runner(srv, tmp_path)
    assert code == 0 and runner.outcomes == ["succeeded"] and srv.results[0]["body"].startswith(b"ply\n")


# --- a job end to end -----------------------------------------------------------------------------------------------

def test_a_finish_job_makes_the_http_jobs_files_byte_for_byte(tmp_path, fast, server, colmap, monkeypatch):  # noqa: F811
    options = {**SplatOptions().to_dict(), "cleanup": True, "quality": "max", "wallZones": True}
    prep = job_dir(tmp_path, "prep", options)
    run_prepare(str(prep), lambda *a: None)
    prepared = json.load(open(prep / "prepared.json"))
    stats = {"trainer": "gsplat", "zoned": True, "quality": "max", "steps": 3000, "trainingSeconds": 12.5}
    http = tmp_path / "http"  # the directory the HTTP upload of splat-finish builds, run by the HTTP job's run_job body
    http.mkdir()
    write_slim_ply(scene(), str(http / "splat.ply"))
    (http / "prepared.json").write_text(json.dumps(prepared))
    (http / "trainStats.json").write_text(json.dumps(stats))
    ply = (http / "splat.ply").read_bytes()  # the job removes what it no longer needs
    want = run_finish(str(http), lambda *a: None)

    seen = []
    real = compute.run_child
    monkeypatch.setattr(compute, "run_child", lambda kind, d, report, *a, **k: real(
        kind, d, lambda *x: (seen.append(x[1]), report(*x)), *a, **k))
    srv = server(finish_envelope(prepared, ply, stats), key=KEY)
    claimed(srv, "splat-finish", requiredMemoryMb=3072)
    runner, code = run_runner(srv, tmp_path)

    assert code == 0 and runner.outcomes == ["succeeded"]
    status, zf = result_zip(srv)
    assert "Content-Encoding" not in srv.results[0]["headers"]  # a stored zip is not compressed again
    assert status["status"] == "succeeded" and status["kind"] == "splat-finish" and status["progress"] == 1.0
    got = status["result"]
    assert got["files"] == [{"name": f, "url": f"/v1/jobs/{srv.job_id}/files/{f}"} for f in want["files"]]
    assert timeless({k: v for k, v in got.items() if k != "files"}) == timeless(
        json.loads(json.dumps({k: v for k, v in want.items() if k != "files"})))
    assert sorted(zf.namelist()) == sorted(["status.json", *want["files"]])
    for name in want["files"]:
        if name == "frame.json":  # holds the stages' wall-clock times
            assert timeless(json.loads(zf.read(name))) == timeless(json.loads((http / name).read_bytes()))
        else:
            assert zf.read(name) == (http / name).read_bytes(), name
    assert {"load", "align", "export"} <= set(seen)  # the child's progress reached the heartbeat
    stats_header = json.loads(srv.results[0]["headers"]["X-Blocwerk-Stats"])
    assert stats_header["kind"] == "splat-finish" and stats_header["files"] == len(want["files"]) + 1
    assert [p.name for p in (tmp_path / "work").iterdir()] == ["state"]


def test_the_status_has_the_keys_of_the_http_jobs_status(tmp_path, monkeypatch):
    from computejobs.jobs import Job
    monkeypatch.setattr(settings, "work_dir", str(tmp_path))
    http = Job("splat-finish")
    http.status, http.result, http.files = "succeeded", {"frame": {}}, ["wall.splat"]
    mine = status_doc(http.id, "splat-finish", {"frame": {}, "files": ["wall.splat"]}, http.created)
    assert set(mine) == set(http.to_status()) and mine["result"]["files"] == http.to_status()["result"]["files"]


def in_process_prepare():
    """run_child replaced by the prepare body in this process (a spawned child would not have the COLMAP stand-in)."""
    def run(kind, job_dir_, report, stop, limit_mb=0, timeout_s=0):
        assert kind == "splat-prepare" and limit_mb > 0 and timeout_s > 0
        report(0.5, "ingest", "photos")
        return run_prepare(job_dir_, lambda *a: None), 123
    return run


def test_a_prepare_job_unpacks_runs_and_uploads_status_and_files(tmp_path, fast, server, colmap, monkeypatch):  # noqa: F811
    photos = [(f"IMG_{i}.jpg", jpeg(size=WallColmap.size)) for i in range(len(CAMS))] + \
        [("vf_0001.jpg", jpeg(size=WallColmap.size))]
    from test_roundtrip import geometry
    monkeypatch.setattr(compute, "run_child", in_process_prepare())
    srv = server(prepare_envelope(photos, {"quality": "max"}, geometry()), key=KEY)
    claimed(srv, "splat-prepare", requiredMemoryMb=1000)
    runner, _ = run_runner(srv, tmp_path)

    assert runner.outcomes == ["succeeded"], srv.fails
    status, zf = result_zip(srv)
    assert [f["name"] for f in status["result"]["files"]] == ["bundle.zip", "prepared.json", "sparse.zip"]
    assert sorted(zf.namelist()) == ["bundle.zip", "prepared.json", "sparse.zip", "status.json"]
    assert json.loads(zf.read("prepared.json"))["version"] == 2
    stats = json.loads(srv.results[0]["headers"]["X-Blocwerk-Stats"])
    assert stats["photos"] == len(photos) and stats["peakMemoryMb"] == 123


def test_a_job_that_needs_more_memory_than_the_runner_has_goes_back_as_its_failure(tmp_path, fast, server):  # noqa: F811
    srv = server(prepare_envelope([("p.jpg", jpeg())]), key=KEY)
    claimed(srv, "splat-prepare", requiredMemoryMb=999999)
    runner, _ = run_runner(srv, tmp_path)
    assert runner.outcomes == ["failed"] and srv.fails[0]["retryable"] is True and srv.results == []


@pytest.mark.parametrize("data, why", [
    (prepare_envelope([("p.jpg", jpeg())]), "at least 3 photos"),  # run_job's own input check, as the HTTP API's 422
    (b"not a zip", "bad envelope"),
], ids=["too few photos", "not a zip"])
def test_a_bad_input_fails_for_good(tmp_path, fast, server, data, why):  # noqa: F811
    srv = server(data, key=KEY)
    claimed(srv, "splat-prepare")
    runner, _ = run_runner(srv, tmp_path)
    assert runner.outcomes == ["failed"] and srv.fails[0]["retryable"] is False and why in srv.fails[0]["reason"]


def test_the_child_failing_is_the_jobs_failure_and_an_overrun_is_this_runners(tmp_path, fast, server,  # noqa: F811
                                                                              monkeypatch):
    data = prepare_envelope([(f"p{i}.jpg", jpeg()) for i in range(3)])
    for raised, retryable in ((compute_child.ChildFailed("sfm: too few images registered"), False),
                              (MemoryLimitError("sfm", "too big", "memory"), True),
                              (JobError("child", "internal error (ref ab12; details in the worker log)"), True)):
        def boom(*a, _e=raised, **k):
            raise _e
        monkeypatch.setattr(compute, "run_child", boom)
        srv = server(data, key=KEY)
        claimed(srv, "splat-prepare")
        shutil.rmtree(tmp_path / "work", ignore_errors=True)
        run_runner(srv, tmp_path)
        assert srv.fails[0]["retryable"] is retryable and srv.results == []


# --- the child --------------------------------------------------------------------------------------------------------

def test_the_child_reports_a_failed_job_and_a_stop(tmp_path):
    empty = tmp_path / "empty"
    empty.mkdir()  # a finish without its inputs: run_job's internal error, which is this machine's fault, not the input's
    with pytest.raises(JobError, match="internal error") as raised:
        compute_child.run_child("splat-finish", str(empty), lambda *a: None, threading.Event(), 0, 60)
    assert not isinstance(raised.value, compute_child.ChildFailed)
    stop = threading.Event()
    stop.set()
    with pytest.raises(Stopped):
        compute_child.run_child("splat-finish", str(empty), lambda *a: None, stop, 0, 60)


def test_an_overrun_or_a_timeout_ends_the_child_as_this_runners_failure():
    class Alive:
        def is_alive(self):
            return True

    class Tripped:
        tripped, peak_mb = "memory", 4000

    parent, child = mp.Pipe(duplex=False)  # nothing is ever sent: the child is "busy"
    with pytest.raises(MemoryLimitError, match="more memory"):
        compute_child._outcome(parent, Alive(), Tripped(), threading.Event(), 60, lambda *a: None)
    with pytest.raises(JobError, match="timed out"):
        compute_child._outcome(parent, Alive(), None, threading.Event(), -1, lambda *a: None)


# --- which child errors are the input's and which this machine's ------------------------------------------------------

class Quiet:
    """Stands in for the child process: alive, never exited."""
    exitcode = None

    def is_alive(self):
        return True


def outcome_of(monkeypatch, body):
    """What the parent makes of `body` failing inside run_job's child entry (run in this process over a real Pipe)."""
    from splatworker import prepare, runner
    monkeypatch.setattr(runner, "run_in_child", runner.run_in_child)  # restored; _run_job_tagged swaps it
    monkeypatch.setattr(prepare, "run_prepare", body)
    parent, child = mp.Pipe(duplex=False)
    compute_child._run_job_tagged("splat-prepare", "/job", child)
    try:
        return compute_child._outcome(parent, Quiet(), None, threading.Event(), 60, lambda *a: None)
    except Exception as e:  # noqa: BLE001 - the test inspects which one
        return e


def raising(exc):
    def body(job_dir, progress):
        raise exc
    return body


@pytest.mark.parametrize("exc, final", [
    (ValueError("at least 3 photos"), True),  # invalid input, as the HTTP API's 422
    (JobError("sfm", "too few images registered"), True),  # the job's own, reproducible failure
    (MemoryLimitError("sfm", "colmap exceeded 8 GB", "memory"), False),  # past this machine's memory after the retries
    (MemoryLimitError("sfm", "swap grew", "swap"), False),
    (OSError(28, "No space left on device"), False),  # an internal error: disk full, a broken environment
    (RuntimeError("boom"), False),
], ids=["input", "job error", "memory", "swap", "disk full", "bug"])
def test_a_child_error_is_final_only_when_the_input_caused_it(monkeypatch, exc, final):
    got = outcome_of(monkeypatch, raising(exc))
    assert isinstance(got, compute_child.ChildFailed) is final
    assert isinstance(got, JobError) is (not final)


def test_child_ignores_the_profile_override(monkeypatch):
    """The spawned child re-reads SPLAT_PROFILE_OVERRIDE from the environment; a runner's prepare must still write the
    quality the server asked for (seen on exp 2026-10-28: a draft capture prepared as ultra)."""
    from splatworker import runner
    from splatworker.settings import settings as worker_settings
    monkeypatch.setattr(worker_settings, "profile_override", "ultra")
    seen = {}
    monkeypatch.setattr(runner, "run_job", lambda kind, job_dir, conn: seen.setdefault("override", worker_settings.profile_override))
    compute_child._run_job_tagged("splat-prepare", "/nonexistent", None)
    assert seen["override"] == ""
