"""Wall textures on the 3D runner against the fake Blocwerk server: the capability it advertises (hello), the bundle it
unpacks, and a textures job end to end on a tiny synthetic photo wall (wall-geometry's tests/photowall.py): the zip it
uploads is what the wall-geometry service's `textures` job makes, pixel for pixel, and a pause stops the render."""
import io
import json
import zipfile

import cv2
import numpy as np
import photowall
import pytest
from test_gpurunner import CAPS, KEY, fast, make_bundle, run_runner, server  # noqa: F401 - fixtures

from splatworker import procs
from splatworker.gpurunner import caps as caps_module
from splatworker.gpurunner import textures
from splatworker.gpurunner.textures_bundle import TexturesBundleError, extract_textures_bundle
from wallgeometry.textures import render_textures

OPTIONS = {"mmPerPx": 40, "maxSidePx": 256, "blendViews": 3}
MAC_48GB = {"totalMb": 49152}


def textures_bundle(tmp_path, options=OPTIONS, photos=6, extra=()):
    doc, images = photowall.scene(photos)
    out = io.BytesIO()
    with zipfile.ZipFile(out, "w") as zf:
        zf.writestr("textures-job.json", json.dumps({"version": 1, "options": options}))
        zf.writestr("geometry.json", json.dumps(doc))
        for name, img in images.items():
            zf.writestr(f"photos/{name}.jpg", cv2.imencode(".jpg", img, [cv2.IMWRITE_JPEG_QUALITY, 95])[1].tobytes())
        for name, data in extra:
            zf.writestr(name, data)
    return out.getvalue(), doc, images


@pytest.fixture
def budget(monkeypatch):
    monkeypatch.setattr(textures, "blend_budget_mb", lambda *a, **k: 24576)


def textures_runner(srv, tmp_path, **job):
    srv.job_extra = {"kind": "textures", "requiredMemoryMb": 1000, **job}
    return run_runner(srv, tmp_path)


# --- the capability ---------------------------------------------------------------------------------------------

def test_a_48_gb_mac_advertises_textures_with_half_its_memory():
    assert textures.advertise({}, MAC_48GB) == {"capabilities": ["splat", "textures"], "texturesMemoryMb": 24576}
    assert textures.blend_budget_mb({"RUNNER_TEXTURES_MEMORY_FRACTION": "0.75"}, MAC_48GB) == 36864


@pytest.mark.parametrize("env,info", [
    ({"RUNNER_TEXTURES": "0"}, MAC_48GB),
    ({}, {"totalMb": 4096}),  # 2 GB of budget: nothing over the server's own blend
    ({}, {"totalMb": None}),
])
def test_a_runner_that_cannot_render_textures_says_splat_only(env, info):
    assert textures.advertise(env, info) == {"capabilities": ["splat"], "texturesMemoryMb": None}


def test_without_wallgeometry_the_capability_is_not_advertised(monkeypatch):
    monkeypatch.setattr(textures, "available", lambda: False)
    assert textures.advertise({}, MAC_48GB)["capabilities"] == ["splat"]


def test_the_blend_budget_can_be_set_as_the_services_own_variable():
    assert textures.blend_budget_mb({"TEXTURES_BLEND_MAX_BYTES": str(12 << 30)}, {"totalMb": 1}) == 12288


def test_hello_carries_the_capabilities(tmp_path, fast, server, monkeypatch):  # noqa: F811
    monkeypatch.setattr(caps_module.textures, "advertise", lambda: {"capabilities": ["splat", "textures"],
                                                                    "texturesMemoryMb": 24576})
    monkeypatch.setattr(caps_module, "gpu_info", lambda: ("Apple M4", 49152))
    monkeypatch.setattr(caps_module, "budget_mb", lambda: 20000)
    caps = caps_module.Capabilities()
    monkeypatch.setattr(type(caps), "version", property(lambda self: "0.3.0"))
    srv = server(make_bundle(tmp_path), key=KEY)
    srv.jobs_left = 0
    from splatworker.gpurunner.client import Client
    from splatworker.gpurunner.loop import Runner
    runner = Runner(Client(srv.url, KEY), str(tmp_path / "w"), caps, max_jobs=1)
    assert runner._hello()
    assert srv.hellos[0]["capabilities"] == ["splat", "textures"] and srv.hellos[0]["texturesMemoryMb"] == 24576
    assert srv.hellos[0]["gpuName"] == "Apple M4"


# --- the bundle --------------------------------------------------------------------------------------------------

def test_the_bundle_unpacks_into_the_renderers_layout(tmp_path):
    data, doc, images = textures_bundle(tmp_path)
    (tmp_path / "b.zip").write_bytes(data)
    count, options = extract_textures_bundle(str(tmp_path / "b.zip"), str(tmp_path / "w"))
    assert (count, options) == (6, OPTIONS)
    inputs = json.loads((tmp_path / "w" / "inputs.json").read_text())
    assert inputs["photos"]["PW_000"] == "photo_PW_000.jpg" and inputs["options"] == OPTIONS
    assert json.loads((tmp_path / "w" / "geometry.json").read_text())["cameras"] == doc["cameras"]


@pytest.mark.parametrize("extra,options,why", [
    ([("../evil.jpg", b"x")], OPTIONS, "unexpected file"),
    ([("notes.txt", b"x")], OPTIONS, "unexpected file"),
    ([], {"mmPerPx": 0.01}, "mmPerPx"),
    ([], {"bogus": 1}, "unknown option"),
])
def test_a_bundle_with_other_files_or_options_is_refused(tmp_path, extra, options, why):
    data, _, _ = textures_bundle(tmp_path, options=options, extra=extra)
    (tmp_path / "b.zip").write_bytes(data)
    with pytest.raises(TexturesBundleError, match=why):
        extract_textures_bundle(str(tmp_path / "b.zip"), str(tmp_path / "w"))


def test_a_bundle_without_photos_is_refused(tmp_path):
    out = io.BytesIO()
    with zipfile.ZipFile(out, "w") as zf:
        zf.writestr("textures-job.json", "{}")
        zf.writestr("geometry.json", "{}")
    (tmp_path / "b.zip").write_bytes(out.getvalue())
    with pytest.raises(TexturesBundleError, match="no photos"):
        extract_textures_bundle(str(tmp_path / "b.zip"), str(tmp_path / "w"))


# --- a job end to end ---------------------------------------------------------------------------------------------

def test_a_textures_job_renders_in_a_child_and_uploads_the_services_files(tmp_path, fast, server, budget):  # noqa: F811
    data, doc, images = textures_bundle(tmp_path)
    srv = server(data, key=KEY)
    runner, code = textures_runner(srv, tmp_path)

    assert code == 0 and runner.outcomes == ["succeeded"]
    res = srv.results[0]
    assert "Content-Encoding" not in res["headers"]  # a zip is not compressed again
    zf = zipfile.ZipFile(io.BytesIO(res["body"]))
    manifest = json.loads(zf.read("textures.json"))
    assert sorted(f["facet"] for f in manifest["facets"]) == ["0", "1"]
    assert sorted(zf.namelist()) == sorted(["textures.json"] + [f[k] for f in manifest["facets"]
                                                                for k in ("file", "maskFile", "sourceFile")])
    stats = json.loads(res["headers"]["X-Blocwerk-Stats"])
    assert stats["photos"] == 6 and stats["blendBudgetMb"] == 24576 and stats["runnerGpu"] == "Test GPU"
    assert any(p["stage"] == "textures" and p["fraction"] > 0 for p in srv.progress)
    assert [p.name for p in (tmp_path / "work").iterdir()] == ["state"]  # job dir gone

    # the same renderer as the service: the first facet's texture, mask and source map equal an in-process render
    names = set(images)
    direct = render_textures(doc, images.__getitem__, names, {**OPTIONS, "blendMaxBytes": 24576 << 20})
    first = {r["facet"]: r for r in direct}["0"]
    entry = next(f for f in manifest["facets"] if f["facet"] == "0")
    texture = cv2.imdecode(np.frombuffer(zf.read(entry["file"]), np.uint8), cv2.IMREAD_COLOR)
    assert texture.shape == first["image"].shape
    assert np.abs(texture.astype(int) - first["image"].astype(int)).mean() < 8  # JPEG at 90 on a 45 px facet
    mask = cv2.imdecode(np.frombuffer(zf.read(entry["maskFile"]), np.uint8), cv2.IMREAD_GRAYSCALE)
    assert np.array_equal(mask, first["mask"])
    assert (entry["widthPx"], entry["heightPx"]) == (texture.shape[1], texture.shape[0])


def test_a_job_that_needs_more_memory_than_the_runner_has_goes_back_as_its_failure(tmp_path, fast, server,  # noqa: F811
                                                                                   monkeypatch):
    monkeypatch.setattr(textures, "blend_budget_mb", lambda *a, **k: 4096)
    data, _, _ = textures_bundle(tmp_path)
    srv = server(data)
    runner, _ = textures_runner(srv, tmp_path, requiredMemoryMb=30000)
    assert runner.outcomes == ["failed"] and srv.results == []
    assert srv.fails[0]["retryable"] is True and "needs 30000 MB" in srv.fails[0]["reason"]
    assert not [p for _, p, _, _ in srv.requests if p.endswith("/bundle")]  # nothing downloaded for it


def test_inconsistent_inputs_fail_for_good(tmp_path, fast, server, budget):  # noqa: F811
    data, doc, images = textures_bundle(tmp_path)
    doc["cameras"][0]["width"] += 10  # the photo is no longer the size the camera was solved at
    out = io.BytesIO()
    with zipfile.ZipFile(io.BytesIO(data)) as src, zipfile.ZipFile(out, "w") as dst:
        for item in src.infolist():
            dst.writestr(item.filename, json.dumps(doc) if item.filename == "geometry.json" else src.read(item))
    srv = server(out.getvalue())
    runner, _ = textures_runner(srv, tmp_path)
    assert runner.outcomes == ["failed"] and srv.fails[0]["retryable"] is False
    assert "invalid input" in srv.fails[0]["reason"] and srv.results == []


def test_a_pause_kills_the_render_and_hands_the_job_back_free(tmp_path, fast, server, budget, monkeypatch):  # noqa: F811
    seen = {}

    def render(self, work, budget_mb):
        seen["started"] = True
        self.pause.set()  # the owner pressed "pause now"
        assert procs.current_stop.wait(5)
        raise procs.ToolStopped("textures", "the texture renderer was stopped")

    monkeypatch.setattr(textures.TexturesRun, "_render", render)
    data, _, _ = textures_bundle(tmp_path)
    srv = server(data)
    runner, _ = textures_runner(srv, tmp_path)
    assert seen["started"] and runner.outcomes == ["paused"]
    assert srv.fails == [{"reason": "the runner was paused", "retryable": True, "shutdown": True, "pause": True}]
    assert srv.results == []


def test_a_splat_job_is_still_trained_not_rendered(tmp_path, fast, server, monkeypatch):  # noqa: F811
    from splatworker import brush
    from test_gpurunner import brush_ok
    monkeypatch.setattr(brush, "train", brush_ok)
    srv = server(make_bundle(tmp_path))
    srv.job_extra = {"kind": "splat"}
    runner, _ = run_runner(srv, tmp_path)
    assert runner.outcomes == ["succeeded"] and srv.results[0]["body"].startswith(b"ply\n")


def test_pack_result_zips_the_manifest_and_what_it_names(tmp_path):
    (tmp_path / "textures.json").write_text(json.dumps({"facets": [{"file": "facet_0.jpg", "maskFile": "facet_0_mask.png"}]}))
    (tmp_path / "facet_0.jpg").write_bytes(b"j")
    (tmp_path / "facet_0_mask.png").write_bytes(b"p")
    (tmp_path / "stray.txt").write_bytes(b"s")
    assert textures.pack_result(str(tmp_path), str(tmp_path / "r.zip")) == 3
    assert sorted(zipfile.ZipFile(tmp_path / "r.zip").namelist()) == ["facet_0.jpg", "facet_0_mask.png", "textures.json"]
