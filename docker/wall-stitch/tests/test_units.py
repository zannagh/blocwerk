"""Unit tests for the pieces that have no HTTP surface."""
from __future__ import annotations

import os
import sys
import time

import pytest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from app.config import load_settings  # noqa: E402
from app.errors import MESSAGES, classify  # noqa: E402
from app.stages import HoldsProgressTracker, ProgressTracker, STITCH_MARKERS  # noqa: E402
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
    monkeypatch.setenv("WALLSTITCH_PNG_COMPRESSION", "1")
    monkeypatch.setenv("WALLSTITCH_PIPELINE_THREADS", "2")
    s = load_settings()
    assert s.max_canvas_mpx == 42.5
    assert s.png_compression == 1
    assert s.pipeline_threads == 2


def test_a_non_numeric_canvas_cap_is_refused_rather_than_ignored(monkeypatch):
    monkeypatch.setenv("WALLSTITCH_AUTH_TOKEN", "x" * 20)
    monkeypatch.setenv("WALLSTITCH_MAX_CANVAS_MPX", "big")
    with pytest.raises(RuntimeError, match="WALLSTITCH_MAX_CANVAS_MPX"):
        load_settings()


def test_memory_flags_are_only_passed_where_the_pipeline_advertises_them(monkeypatch):
    from app import invocation
    invocation.supported_flags.cache_clear()
    monkeypatch.setattr(invocation, "supported_flags",
                        lambda *a, **k: frozenset({"--src", "--work", "--wall-angle"}))
    argv = invocation.stitch_command("python", "/p", "stitch_wall.py", "/in", "/work",
                                     "/cache", 45.0, ["1.jpeg"],
                                     max_canvas_mpx=80.0, png_compression=3)
    assert "--max-canvas-mpx" not in argv and "--png-compression" not in argv

    monkeypatch.setattr(invocation, "supported_flags",
                        lambda *a, **k: frozenset({"--src", "--work", "--wall-angle",
                                                   "--max-canvas-mpx", "--png-compression"}))
    argv = invocation.stitch_command("python", "/p", "stitch_wall.py", "/in", "/work",
                                     "/cache", 45.0, ["1.jpeg"],
                                     max_canvas_mpx=80.0, png_compression=3)
    assert argv[argv.index("--max-canvas-mpx") + 1] == "80"
    assert argv[argv.index("--png-compression") + 1] == "3"


def test_every_error_code_has_an_actionable_message():
    for code, message in MESSAGES.items():
        assert len(message) > 15, code
        assert "Traceback" not in message and "/Users" not in message


def test_stitch_progress_is_monotonic_and_named():
    tracker = ProgressTracker(STITCH_MARKERS, 0.02, 0.72, "registering")
    seen = []
    for line in ["[00:00:01] plumb-line distortion calibration",
                 "[00:00:09] undistorted + masked",
                 "[00:00:20]   pair 1-2: coarse 900 inl -> guided 700/900 inl",
                 "[00:01:00] plane normal [0 0 1]",
                 "[00:02:00] canvas 9363x5188",
                 "[00:03:00] composited 9363x5188",
                 "[00:04:00] angled main-span 7648x4864 -> 7648x3439"]:
        if tracker.feed(line):
            seen.append((tracker.progress, tracker.stage))
    assert [s for _, s in seen] == ["calibrating", "undistorting", "registering",
                                    "rectifying", "blending", "blending", "projecting"]
    assert seen == sorted(seen)
    assert 0.02 < seen[0][0] and seen[-1][0] <= 0.72


def test_unrecognised_lines_do_not_invent_progress():
    tracker = ProgressTracker(STITCH_MARKERS, 0.0, 1.0, "registering")
    assert tracker.feed("some incidental chatter") is False
    assert tracker.progress == 0.0


def test_holds_progress_reads_the_matcher_step_numbers():
    tracker = HoldsProgressTracker(0.72, 0.97)
    assert tracker.feed("[1/7] loading") is True
    first = tracker.progress
    assert tracker.feed("[6b/7] the other two planes") is True
    assert tracker.progress > first
    assert tracker.feed("[1/7] loading") is False  # never goes backwards


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
