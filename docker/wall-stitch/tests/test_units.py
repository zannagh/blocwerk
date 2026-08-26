"""Unit tests for the pieces that have no HTTP surface."""
from __future__ import annotations

import os
import sys
import time

import pytest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from app.config import load_settings  # noqa: E402
from app.errors import MESSAGES, classify  # noqa: E402
from app.stages import PIPELINE_MARKERS, ProgressTracker  # noqa: E402
from app.store import JobStore  # noqa: E402


def test_settings_refuse_to_load_without_a_token(monkeypatch):
    monkeypatch.delenv("WALLSTITCH_AUTH_TOKEN", raising=False)
    with pytest.raises(RuntimeError, match="WALLSTITCH_AUTH_TOKEN"):
        load_settings()


def test_settings_refuse_a_trivially_short_token(monkeypatch):
    monkeypatch.setenv("WALLSTITCH_AUTH_TOKEN", "short")
    with pytest.raises(RuntimeError):
        load_settings()


@pytest.mark.parametrize("line,code", [
    ("  pair 3-4: FAILED", "insufficient_overlap"),
    ("cv2.error: could not read input images", "unreadable_image"),
    ("MemoryError", "out_of_memory"),
    ("everything was fine actually", "pipeline_failed"),
    # Verbatim SystemExit messages the current pipeline raises.
    ("could not read frame: /work/input/3.jpeg", "unreadable_image"),
    ("no frame pair matched: the sweep is not overlapping enough", "insufficient_overlap"),
    ("WARNING 4 frames unreachable from the reference, dropped: 9.jpeg", "insufficient_overlap"),
    ("need at least two frames to stitch", "too_few_usable_images"),
    ("no frame lands on the canvas; check --roi", "no_dominant_plane"),
    ("no coarse old->new homography candidate survived matching", "hold_transfer_failed"),
])
def test_pipeline_output_is_classified(line, code):
    assert classify(line) == code


@pytest.mark.parametrize("line,code", [
    # The pipeline's own structured refusals, printed as `FAILED [code] message`.
    ("[13:44:02] FAILED [too_few_images] only one file could be read", "too_few_usable_images"),
    ("[13:44:02] FAILED [registration_failed] frames do not chain", "insufficient_overlap"),
    ("[13:44:02] FAILED [no_legacy_polygons] not the reference set", "no_dominant_plane"),
    ("FAILED [no_dominant_plane] nothing is flat here", "no_dominant_plane"),
])
def test_structured_pipeline_refusals_are_mapped(line, code):
    assert classify(line) == code


def test_an_unknown_pipeline_code_falls_through_rather_than_being_trusted():
    assert classify("FAILED [some_future_code] who knows") == "pipeline_failed"


def test_a_structured_refusal_beats_the_surrounding_log():
    # The log also contains a line the regexes would read as insufficient_overlap; the
    # pipeline's own verdict is the one that stopped the run, so it wins.
    log = "  pair 3-4: FAILED\n[13:44:02] FAILED [no_dominant_plane] nothing is flat\n"
    assert classify(log) == "no_dominant_plane"


def test_a_silent_failure_can_be_given_an_explicit_fallback():
    # What a cgroup OOM kill looks like: the kernel leaves no explanation behind.
    assert classify("", "out_of_memory") == "out_of_memory"
    # ...but real output still wins over the fallback.
    assert classify("cv2.error: could not read input images", "out_of_memory") == "unreadable_image"


def test_memory_settings_come_from_the_environment(monkeypatch):
    monkeypatch.setenv("WALLSTITCH_AUTH_TOKEN", "x" * 20)
    monkeypatch.setenv("WALLSTITCH_MAX_CANVAS_MPX", "42.5")
    monkeypatch.setenv("WALLSTITCH_COMPOSE_MP", "1.5")
    monkeypatch.setenv("WALLSTITCH_PIPELINE_THREADS", "2")
    s = load_settings()
    assert s.max_canvas_mpx == 42.5
    assert s.compose_mp == 1.5
    assert s.pipeline_threads == 2


def test_the_photo_caps_admit_a_real_sweep(monkeypatch):
    # A real sweep of a bouldering wall is 40-50 frames; the pipeline was validated on
    # 46. A cap below that silently makes the good result unreachable from the app.
    monkeypatch.setenv("WALLSTITCH_AUTH_TOKEN", "x" * 20)
    s = load_settings()
    assert s.max_photos >= 46
    assert s.max_request_bytes >= 46 * 30 * 1024 * 1024


def test_a_non_numeric_canvas_cap_is_refused_rather_than_ignored(monkeypatch):
    monkeypatch.setenv("WALLSTITCH_AUTH_TOKEN", "x" * 20)
    monkeypatch.setenv("WALLSTITCH_MAX_CANVAS_MPX", "big")
    with pytest.raises(RuntimeError, match="WALLSTITCH_MAX_CANVAS_MPX"):
        load_settings()


ALL_FLAGS = frozenset({"--input-dir", "--output-dir", "--cache-dir", "--curve",
                       "--natural",
                       "--wall-width-m", "--wall-height-m", "--work-mp", "--compose-mp",
                       "--max-canvas-mpx", "--nfeat", "--onnx", "--prior-holds",
                       "--prior-image"})


def _argv(monkeypatch, flags, **kwargs):
    from app import invocation
    monkeypatch.setattr(invocation, "supported_flags", lambda *a, **k: flags)
    defaults = dict(input_dir="/in", output_dir="/work", cache_dir="/cache",
                    natural="flat", curve="gentle", onnx_model="/m.onnx",
                    wall_width_m=5.5,
                    wall_height_m=2.5, work_mp=0.7, compose_mp=2.5,
                    max_canvas_mpx=40.0)
    defaults.update(kwargs)
    return invocation.pipeline_command("python", "/p", "wall_pipeline.py", **defaults)


def test_the_pipeline_argv_carries_the_input_output_and_resolution_knobs(monkeypatch):
    argv = _argv(monkeypatch, ALL_FLAGS)
    assert argv[:2] == ["python", "wall_pipeline.py"]
    assert argv[argv.index("--input-dir") + 1] == "/in"
    assert argv[argv.index("--output-dir") + 1] == "/work"
    assert argv[argv.index("--curve") + 1] == "gentle"
    assert argv[argv.index("--natural") + 1] == "flat"
    assert argv[argv.index("--max-canvas-mpx") + 1] == "40"
    assert argv[argv.index("--compose-mp") + 1] == "2.5"
    assert argv[argv.index("--onnx") + 1] == "/m.onnx"


def test_flags_are_only_passed_where_the_pipeline_advertises_them(monkeypatch):
    argv = _argv(monkeypatch, frozenset({"--input-dir", "--output-dir"}))
    assert "--max-canvas-mpx" not in argv and "--curve" not in argv
    assert "--natural" not in argv
    assert argv[argv.index("--output-dir") + 1] == "/work"


def test_carryover_is_requested_only_when_both_prior_inputs_are_present(monkeypatch):
    assert "--prior-holds" not in _argv(monkeypatch, ALL_FLAGS, prior_holds="/h.json")
    assert "--prior-image" not in _argv(monkeypatch, ALL_FLAGS, prior_image="/old.jpg")
    argv = _argv(monkeypatch, ALL_FLAGS, prior_holds="/h.json", prior_image="/old.jpg")
    assert argv[argv.index("--prior-holds") + 1] == "/h.json"
    assert argv[argv.index("--prior-image") + 1] == "/old.jpg"


def test_every_error_code_has_an_actionable_message():
    for code, message in MESSAGES.items():
        assert len(message) > 15, code
        assert "Traceback" not in message and "/Users" not in message


# Verbatim lines from a real 46-frame run, elapsed-time prefixes and all.
REAL_LOG = [
    "[    0.0s] 46 frames, reference 2535.jpg",
    "[    0.0s] features for 46 frames at 0.70 Mpx",
    "[   12.0s]   pair 2535.jpg-2536.jpg: 209/269 inliers",
    "[   82.1s] 153 connected pairs",
    "[   82.1s] refining 360 params over 93500 residuals",
    "[  149.9s] transfer rms 6.41 -> 4.94 px, median |e| 0.84 -> 0.84, nfev 400",
    "[  151.0s] canvas 9884x3625 (35.8 Mpx) at out_scale 0.319, frames 1370x1825",
    "[  158.4s] seam-scale warps: 46 frames",
    "[  161.1s] exposure gains fed",
    "[  163.8s] seams found, 1.6 Mpx claimed of 2.5",
    "[  171.7s] composited 46 frames",
    "[  178.0s] silhouette 28 verts, 277108 px inpainted, crop (0, 94, 9884, 3625)",
    "[  178.1s] flat-base 9884x3531",
    "[  181.8s] wall polygon: 20 vertices (from the coverage mask)",
    "[  184.8s]   tile 1280 -> 8499 raw",
    "[  187.4s] flat done: 81 holds",
    "[  187.5s] manifest /work/manifest.json",
]


def test_pipeline_progress_is_monotonic_and_named():
    tracker = ProgressTracker(PIPELINE_MARKERS, 0.02, 0.97, "reading")
    seen = []
    for line in REAL_LOG:
        if tracker.feed(line):
            seen.append((tracker.progress, tracker.stage))
    assert seen == sorted(seen)
    assert [s for _, s in seen][-1] == "packaging"
    assert "blending" in [s for _, s in seen]
    assert "detecting" in [s for _, s in seen]
    assert 0.02 <= seen[0][0] and seen[-1][0] <= 0.97


def test_every_marker_stage_is_reachable_from_a_real_log():
    # A marker whose line the pipeline never prints is a silent progress stall, so the
    # verbatim log above must exercise every stage name the tracker can report.
    tracker = ProgressTracker(PIPELINE_MARKERS, 0.0, 1.0, "reading")
    fed = {marker.stage for marker in PIPELINE_MARKERS
           for line in REAL_LOG if marker.pattern.search(_strip(line))}
    unreachable = {m.stage for m in PIPELINE_MARKERS} - fed - {"matching holds"}
    assert not unreachable, unreachable
    assert tracker.feed(REAL_LOG[0]) is True


def _strip(line):
    from app.stages import TIMESTAMP
    return TIMESTAMP.sub("", line)


def test_unrecognised_lines_do_not_invent_progress():
    tracker = ProgressTracker(PIPELINE_MARKERS, 0.0, 1.0, "reading")
    assert tracker.feed("[   1.0s] some incidental chatter") is False
    assert tracker.progress == 0.0


def test_the_carryover_steps_advance_progress_at_the_tail():
    tracker = ProgressTracker(PIPELINE_MARKERS, 0.0, 1.0, "reading")
    tracker.feed("[  187.4s] flat done: 81 holds")
    before = tracker.progress
    assert tracker.feed("[  190.0s] [3] TPS-ICP") is True
    assert tracker.stage == "matching holds"
    assert tracker.progress > before


def test_orphaned_jobs_are_failed_after_a_restart(tmp_path):
    store = JobStore(str(tmp_path))
    store.create("a", {})
    store.create("b", {})
    store.update("b", status="succeeded")
    assert store.fail_orphans("interrupted", MESSAGES["interrupted"]) == 1
    assert store.read("a")["status"] == "failed"
    assert store.read("a")["error"]["code"] == "interrupted"
    assert store.read("b")["status"] == "succeeded"


def test_the_reaper_deletes_only_expired_job_directories(tmp_path):
    store = JobStore(str(tmp_path))
    store.create("old", {})
    store.create("fresh", {})
    store.update("old", createdAt=time.time() - 90000)
    removed = store.reap(ttl_seconds=86400)
    assert removed == ["old"]
    assert store.read("fresh") is not None
    assert not os.path.exists(store.job_dir("old"))


def test_job_ids_from_the_wire_cannot_escape_the_data_dir(tmp_path):
    store = JobStore(str(tmp_path))
    store.create("real", {})
    assert store.read("../real") is None
    assert store.delete("../real") is False
    assert store.read("real") is not None
