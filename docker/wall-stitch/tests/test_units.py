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
