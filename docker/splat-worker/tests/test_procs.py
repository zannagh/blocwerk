"""ToolRun: TTY output capture (Brush needs a TTY), failure reasons, missing tools."""
import sys

import pytest

from computejobs.child import JobError
from splatworker.procs import ToolRun

TTY_SCRIPT = r"""
import sys
assert sys.stdout.isatty(), "not a tty"
for i in (10, 50, 100):
    sys.stdout.write(f"\r\x1b[2K[1s] ◍ {i}/100     Steps (9/s)")
    sys.stdout.flush()
print("\nTraining took 2s")
"""


def test_pty_lines_are_split_and_cleaned(tmp_path):
    seen = []
    ToolRun("train", [sys.executable, "-c", TTY_SCRIPT], str(tmp_path), str(tmp_path / "t.log"),
            seen.append, use_pty=True).run()
    assert [s for s in seen if "Steps" in s] == ["[1s] ◍ 10/100     Steps (9/s)",
                                                 "[1s] ◍ 50/100     Steps (9/s)",
                                                 "[1s] ◍ 100/100     Steps (9/s)"]
    assert "Training took 2s" in seen and "\x1b" not in (tmp_path / "t.log").read_text()


def test_failure_names_stage_and_telling_line(tmp_path):
    script = "import sys; print('loading'); print('E0923 Check failed: Vulkan adapter not found'); print('bye'); sys.exit(3)"
    with pytest.raises(JobError) as e:
        ToolRun("train", [sys.executable, "-c", script], str(tmp_path), str(tmp_path / "t.log")).run()
    assert e.value.stage == "train" and "exit code 3" in str(e.value) and "Vulkan adapter not found" in str(e.value)


def test_missing_tool(tmp_path):
    with pytest.raises(JobError, match="tool not found"):
        ToolRun("sfm-features", ["/nonexistent/colmap"], str(tmp_path), str(tmp_path / "t.log")).run()


def test_brush_log_filter_drops_spinner_frames_and_most_step_lines():
    from splatworker.brush import _worth_logging
    assert not _worth_logging("\U0001f58c\ufe0f \u2588\u2593 Completed loading")
    assert _worth_logging("\u2588\U0001f58c\ufe0f Loading dataset with 14 training, 0 eval views")
    assert _worth_logging("[9s] 500/5000 Steps (9/s)") and not _worth_logging("[9s] 510/5000 Steps (9/s)")
    assert _worth_logging("Training took 32s")


def test_tools_get_a_minimal_environment(tmp_path, monkeypatch):
    """COLMAP / Brush must not inherit the service's secrets or unrelated variables."""
    monkeypatch.setenv("COMPUTE_API_KEY", "leak-key")
    monkeypatch.setenv("COMPUTE_CALLBACK_SECRET", "leak-secret")
    monkeypatch.setenv("SOME_CLOUD_TOKEN", "leak-token")
    monkeypatch.setenv("VK_ICD_FILENAMES", "/icd.json")
    seen = []
    script = "import os; print(sorted(os.environ.items()))"
    ToolRun("train", [sys.executable, "-c", script], str(tmp_path), str(tmp_path / "t.log"), seen.append,
            env={"EXTRA_FOR_TOOL": "1"}).run()
    out = " ".join(seen)
    assert "leak" not in out and "/icd.json" in out and "EXTRA_FOR_TOOL" in out and "PATH" in out


def test_missing_tool_does_not_reveal_its_path(tmp_path):
    with pytest.raises(JobError) as e:
        ToolRun("sfm-features", ["/opt/secret/place/colmap"], str(tmp_path), str(tmp_path / "t.log")).run()
    assert "/opt/secret" not in str(e.value) and "colmap" in str(e.value)
