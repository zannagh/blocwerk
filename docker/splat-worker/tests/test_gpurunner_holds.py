"""Hold detection on the 3D runner: the capability, the server's tiling and merge rules replayed box for box, the
bundle it accepts, and a detect-holds job end to end against the fake Blocwerk server (a fake ONNX session)."""
import io
import json
import zipfile

import numpy as np
import pytest
from PIL import Image
from test_gpurunner import KEY, fast, run_runner, server  # noqa: F401 - fixtures

from splatworker.gpurunner import holds
from splatworker.gpurunner import holds_detect as hd
from splatworker.gpurunner.holds import HoldsBundleError, read_bundle

JOB = {"version": 1, "tiling": {"enabled": True, "tileSize": 1280, "overlap": 256, "confidence": 0.35},
       "wholeImageConfidence": 0.25, "windowIoU": 0.45}


class FakeInput:
    name, shape = "images", [1, 3, 640, 640]


class FakeSession:
    """Answers every inference with one box in the middle of the letterboxed input (confidence 0.9, class 0)."""

    def __init__(self, boxes=((320.0, 320.0, 64.0, 64.0, 0.9, 0),)):
        self.boxes, self.calls = boxes, 0

    def get_inputs(self):
        return [FakeInput()]

    def run(self, _, feeds):
        assert feeds["images"].shape == (1, 3, 640, 640) and feeds["images"].dtype == np.float32
        self.calls += 1
        out = np.zeros((1, 6, 8400), dtype=np.float32)
        for i, (cx, cy, w, h, conf, cls) in enumerate(self.boxes):
            out[0, :4, i] = (cx, cy, w, h)
            out[0, 4 + cls, i] = conf
        return [out]


def photo_bytes(w=1600, h=1200):
    buf = io.BytesIO()
    Image.new("RGB", (w, h), (120, 90, 60)).save(buf, "JPEG", quality=90)
    return buf.getvalue()


def holds_bundle(photos=2, extra=(), job=JOB):
    out = io.BytesIO()
    with zipfile.ZipFile(out, "w") as zf:
        zf.writestr("holds-job.json", json.dumps(job))
        zf.writestr("model.onnx", b"onnx")
        for i in range(photos):
            zf.writestr(f"photos/p{i:02d}.jpg", photo_bytes())
        for name, data in extra:
            zf.writestr(name, data)
    return out.getvalue()


# --- the capability ---------------------------------------------------------------------------------------------

def test_it_is_advertised_only_when_onnx_runtime_imports(monkeypatch):
    monkeypatch.setattr(holds, "available", lambda: True)
    assert holds.advertise({}) == ["detect-holds"]
    assert holds.advertise({"RUNNER_HOLDS": "0"}) == []
    monkeypatch.setattr(holds, "available", lambda: False)
    assert holds.advertise({}) == []


# --- the server's rules, replayed -----------------------------------------------------------------------------------

def test_the_windows_are_the_servers_tile_grid():
    assert hd.tile_starts(4032, 1280, 256) == [0, 1024, 2048, 2752]
    assert hd.tile_starts(1000, 1280, 256) == [0]
    grid = hd.tile_grid(4032, 3024, 1280, 256)
    assert len(grid) == 12 and grid[-1] == (2752, 1744, 1280, 1280)


def test_the_letterbox_is_proportional_and_centred():
    rgb = np.full((960, 1280, 3), 255, dtype=np.uint8)
    t = hd.letterbox(rgb, 640)
    assert t.shape == (1, 3, 640, 640)
    assert t[0, :, 79, 320].max() == 0 and t[0, :, 80, 320].min() == 1 and t[0, :, 559, 320].min() == 1
    assert t[0, :, 560, 320].max() == 0


def test_decode_maps_boxes_back_with_the_proportional_gain():
    out = np.zeros((6, 4), dtype=np.float32)
    out[:4, 0] = (320, 320, 64, 32)
    out[4, 0] = 0.5
    out[5, 1] = 0.2  # below the confidence
    boxes = hd.decode(out, 1280, 960, 640, ["hold", "volume"], 0.35)
    assert boxes == [(576, 448, 704, 512, pytest.approx(0.5), "hold")]


def test_nms_is_class_agnostic_and_merge_is_per_class():
    a, b = (0, 0, 100, 100, 0.9, "hold"), (5, 5, 105, 105, 0.8, "volume")
    assert hd.nms([b, a], 0.45) == [a]
    assert hd.merge([b, a]) == [a, b]


def test_boxes_cut_by_an_inner_window_edge_are_dropped():
    window = (1024, 0, 1280, 1280)
    kept = hd.to_image([(2, 100, 50, 150, 0.9, "hold"), (300, 100, 350, 150, 0.9, "hold")], window, 4032, 3024)
    assert kept == [(1324, 100, 1374, 150, 0.9, "hold")]


def test_tangles_and_nested_parts_are_dropped():
    lattice = [(x, 0, x + 100, 100, 0.9, "hold") for x in (0, 40, 80)]
    assert hd.drop_tangles(lattice) == [lattice[0], lattice[2]]  # the middle one overlaps both others
    hold, bolt = (0, 0, 400, 400, 0.9, "hold"), (190, 190, 210, 210, 0.8, "hold")
    assert hd.drop_nested([hold, bolt]) == [hold]


def test_square_rounds_half_to_even_like_the_server():
    # centre (2, 0.5), side 4: top = round(-1.5) = -2 (C#'s Math.Round and Python's round both round half to even)
    assert hd.square((0, 0, 4, 1, 0.5, "hold"))[:4] == (0, -2, 4, 2)


def test_a_tiled_photo_runs_every_window_and_the_whole_image():
    session = FakeSession()
    det = hd.Detector(session, ["hold", "volume"], 640, JOB)
    boxes = det.detect(np.zeros((3024, 4032, 3), dtype=np.uint8))
    assert session.calls == 13
    assert boxes and all(b[5] == "hold" for b in boxes)


def test_tiling_off_runs_the_whole_image_once_at_its_own_confidence():
    session = FakeSession(((320.0, 320.0, 64.0, 64.0, 0.3, 0),))
    det = hd.Detector(session, ["hold", "volume"], 640, {**JOB, "tiling": {"enabled": False}})
    assert len(det.detect(np.zeros((1200, 1600, 3), dtype=np.uint8))) == 1 and session.calls == 1


# --- the bundle -----------------------------------------------------------------------------------------------------

@pytest.mark.parametrize("extra,why", [((("../evil.jpg", b"x"),), "unexpected"), ((("photos/a b.jpg", b"x"),), "unexpected")])
def test_a_bundle_with_other_files_is_refused(extra, why):
    with zipfile.ZipFile(io.BytesIO(holds_bundle(extra=extra))) as zf, pytest.raises(HoldsBundleError, match=why):
        read_bundle(zf)


def test_a_bundle_without_photos_or_model_is_refused():
    with zipfile.ZipFile(io.BytesIO(holds_bundle(photos=0))) as zf, pytest.raises(HoldsBundleError, match="no photos"):
        read_bundle(zf)
    out = io.BytesIO()
    with zipfile.ZipFile(out, "w") as zf:
        zf.writestr("holds-job.json", json.dumps(JOB))
    with zipfile.ZipFile(out) as zf, pytest.raises(HoldsBundleError, match="lacks"):
        read_bundle(zf)


# --- a job end to end ---------------------------------------------------------------------------------------------

@pytest.fixture
def fake_onnx(monkeypatch):
    session = FakeSession()
    monkeypatch.setattr(holds, "open_session", lambda model, env=None: session)
    monkeypatch.setattr(holds, "model_labels", lambda s: ["hold", "volume"])
    return session


def test_a_detect_holds_job_uploads_the_boxes_per_photo(tmp_path, fast, server, fake_onnx):  # noqa: F811
    srv = server(holds_bundle(photos=2), key=KEY)
    srv.job_extra = {"kind": "detect-holds"}
    runner, code = run_runner(srv, tmp_path)

    assert code == 0 and runner.outcomes == ["succeeded"]
    doc = json.loads(srv.results[0]["body"])
    assert doc["version"] == 1 and sorted(doc["photos"]) == ["p00", "p01"]
    first = doc["photos"]["p00"]
    assert (first["width"], first["height"]) == (1600, 1200)
    assert all(len(b) == 6 and b[5] == "hold" for b in first["boxes"]) and first["boxes"]
    stats = json.loads(srv.results[0]["headers"]["X-Blocwerk-Stats"])
    assert stats["photos"] == 2 and stats["holds"] == sum(len(p["boxes"]) for p in doc["photos"].values())


def test_a_bad_bundle_fails_for_good(tmp_path, fast, server, fake_onnx):  # noqa: F811
    srv = server(holds_bundle(extra=(("other.txt", b"x"),)), key=KEY)
    srv.job_extra = {"kind": "detect-holds"}
    runner, code = run_runner(srv, tmp_path)
    assert runner.outcomes == ["failed"] and srv.fails and srv.fails[0]["retryable"] is False
