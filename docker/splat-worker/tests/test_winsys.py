"""Windows portability, testable on every OS: memory figures (winsys.py), the watchdog's process tree (memguard.py),
how a tool's terminal is chosen and the ConPTY adapter (procs.py, winpty_proc.py), bundle names (portable.py), and the
shared hardening (computejobs.child: Windows paths scrubbed, Windows variables kept for tools)."""
import sys
import types
import zipfile

import pytest
from computejobs.child import safe_message, tool_env

from splatworker import memguard, portable, procs, resources, winpty_proc, winsys
from splatworker.bundle import extract_bundle

GB = 1024 ** 3


def test_swap_from_status_is_the_page_file_beyond_ram():
    total, used = winsys.swap_from_status(total_phys=32 * GB, total_page_file=40 * GB, avail_page_file=20 * GB,
                                          avail_phys=16 * GB)
    assert total == 8 * 1024 and used == 4 * 1024  # 20 GB committed, 16 GB of it in RAM
    assert winsys.swap_from_status(16 * GB, 16 * GB, 10 * GB, 4 * GB) == (0, 0)  # no page file


def test_tree_pids_and_table_tree_bytes_skip_the_idle_process_and_cycles():
    parents = {0: 0, 4: 0, 100: 4, 200: 100, 201: 100, 300: 4, 400: 400}
    assert winsys.tree_pids(parents, 100) == {100, 200, 201}
    assert winsys.tree_pids(parents, 0) == {0, 4, 100, 200, 201, 300}
    table = {pid: (ppid, 10) for pid, ppid in parents.items()}
    assert memguard.table_tree_bytes(table, 100) == 30
    assert memguard.table_tree_bytes(table, 400) == 10 and memguard.table_tree_bytes(table, 999) == 0
    assert memguard.table_tree_bytes({1: (2, 5), 2: (1, 7)}, 1) == 12  # a parent loop is counted once


def test_console_close_hands_back_and_ctrl_c_passes_on():
    calls = []
    handler = winsys.close_event_handler(calls.append, grace_s=3.0)
    assert handler(0) is False and handler(1) is False and calls == []  # Ctrl+C / Ctrl+Break: Python's SIGINT path
    assert handler(2) is True and handler(6) is True and calls == [3.0, 3.0]  # close, shutdown
    boom = winsys.close_event_handler(lambda g: 1 / 0)
    assert boom(5) is True  # an error never escapes into the console thread
    assert winsys.on_console_close(calls.append) is (sys.platform == "win32")


def test_terminal_mode():
    assert procs.terminal_mode(False) == "pipe"
    assert procs.terminal_mode(True, "darwin") == "pty" and procs.terminal_mode(True, "linux") == "pty"
    assert procs.terminal_mode(True, "win32", winpty_ok=True) == "winpty"
    assert procs.terminal_mode(True, "win32", winpty_ok=False) == "pipe"
    assert winpty_proc.available({"RUNNER_WINPTY": "0"}) is False


class FakePty:
    """pywinpty's PtyProcess as far as WinPtyTool uses it."""

    def __init__(self, chunks, code=0):
        self.chunks, self.exitstatus, self.pid, self.killed, self.closed = list(chunks), None, 4242, False, False
        self.code = code

    @classmethod
    def spawn(cls, argv, cwd=None, env=None, dimensions=None):
        inst = cls(["step 1/10\r", "", "step 2/10\r\n", "done\n"])
        inst.argv, inst.dimensions = argv, dimensions
        return inst

    def read(self, size):
        if not self.chunks:
            self.exitstatus = self.code
            raise EOFError
        return self.chunks.pop(0)

    def isalive(self):
        return self.exitstatus is None

    def terminate(self, force=False):
        self.killed, self.exitstatus = True, 1

    def close(self, force=False):
        self.closed = True


def test_winpty_tool_reads_bytes_until_the_end(monkeypatch):
    monkeypatch.setitem(sys.modules, "winpty", types.SimpleNamespace(PtyProcess=FakePty))
    tool = winpty_proc.WinPtyTool(["brush_app", "x"], ".", {})
    assert tool.pid == 4242 and tool.proc.dimensions == (winpty_proc.ROWS, winpty_proc.COLUMNS)
    out = []
    while chunk := tool.read():
        out.append(chunk)
    assert b"".join(out) == b"step 1/10\rstep 2/10\r\ndone\n"  # the empty read was waited out, not an EOF
    assert tool.poll() == 0 and tool.wait() == 0
    tool.kill()
    tool.close()
    assert tool.proc.closed


def test_tool_run_through_the_winpty_adapter(tmp_path, monkeypatch):
    monkeypatch.setitem(sys.modules, "winpty", types.SimpleNamespace(PtyProcess=FakePty))
    monkeypatch.setattr(procs, "terminal_mode", lambda use_pty: "winpty")
    lines = []
    run = procs.ToolRun("train", ["brush_app", "dataset"], str(tmp_path), str(tmp_path / "t.log"), lines.append,
                        use_pty=True).run()
    assert lines == ["step 1/10", "step 2/10", "done"] and run.tail[-1] == "done"


def test_tool_errors_never_name_a_windows_path():
    run = procs.ToolRun("train", ["brush_app", r"C:\Users\Jane Doe\AppData\Local\blocwerk-runner\job-1\b"], ".", "x")
    assert run._verb() == ""
    assert procs.ToolRun("s", ["colmap", "mapper"], ".", "x")._verb() == "mapper"
    msg = safe_message(r"cannot open C:\Users\Jane Doe\AppData\Local\photo.jpg or \\nas\share\x\y.bin")
    assert "Jane" not in msg and "nas" not in msg and "photo.jpg" in msg and "y.bin" in msg
    assert safe_message("tool /opt/brush/brush_app failed") == "tool brush_app failed"  # POSIX unchanged


@pytest.mark.parametrize("name", ["Jane Doe", "O'Brien", "jane&co", "Jane, Inc", "J. (Admin)"])
def test_no_user_name_survives_in_error_text(name):
    texts = [rf"cannot open C:\Users\{name}", rf"cannot open C:\Users\{name}\AppData\Local\job\photo.jpg now",
             rf"C:\Users\{name}\x y\z.log failed", f"cannot open /Users/{name}/photos/a.jpg",
             f"read /home/{name}", rf"\\nas\homes\Users\{name}\b.bin"]
    for text in texts:
        msg = safe_message(text)
        for word in name.replace(",", " ").replace("(", " ").replace(")", " ").split():
            assert word not in msg, (text, msg)
    assert "photo.jpg" in safe_message(texts[1]) and "z.log" in safe_message(texts[2])


def test_tool_env_keeps_what_windows_children_need(monkeypatch):
    monkeypatch.setenv("SYSTEMROOT", r"C:\Windows")
    monkeypatch.setenv("LOCALAPPDATA", r"C:\Users\x\AppData\Local")
    monkeypatch.setenv("COMPUTE_API_KEY", "leak")
    env = tool_env()
    assert env["SYSTEMROOT"] == r"C:\Windows" and "LOCALAPPDATA" in env and "COMPUTE_API_KEY" not in env


def test_portable_names():
    ok = ["dataset/images/g/IMG_1.jpg", "dataset/images/g/IMG_2.jpg"]
    assert portable.name_problem(ok, windows=True, case_insensitive=True) is None
    clash = ["dataset/images/g/IMG_1.jpg", "dataset/images/g/img_1.jpg"]
    assert "case" in portable.name_problem(clash, windows=False, case_insensitive=True)
    assert portable.name_problem(clash, windows=False, case_insensitive=False) is None  # Linux: two files
    for bad in ("dataset/images/CON/a.jpg", "dataset/images/g/nul.jpg", "dataset/images/g/a.", "dataset/x /a.jpg",
                "dataset/images/g/a?.jpg", "dataset/images/com1.tar.jpg"):
        assert portable.name_problem([bad], windows=True, case_insensitive=False), bad
        assert portable.name_problem([bad], windows=False, case_insensitive=False) is None
    assert portable.name_problem(["dataset/images/console.jpg"], windows=True, case_insensitive=False) is None


def test_portable_names_unicode():
    nfc, nfd = "dataset/images/g/café.jpg", "dataset/images/g/café.jpg"
    assert portable.name_problem([nfc, nfd], windows=False, case_insensitive=False, norm_insensitive=True)  # APFS
    assert portable.name_problem([nfc, nfd], windows=True, case_insensitive=True) is None  # NTFS keeps both
    assert portable.name_problem([nfc.upper(), nfd], False, True, norm_insensitive=True)  # case + normalization
    sharp = ["dataset/images/g/straße.jpg", "dataset/images/g/strasse.jpg"]
    assert portable.name_problem(sharp, windows=True, case_insensitive=True, norm_insensitive=True) is None


def test_case_probe_matches_this_file_system(tmp_path):
    probe = tmp_path / "Probe"
    probe.write_text("x")
    assert portable.is_case_insensitive(str(tmp_path)) == (tmp_path / "probe").exists()
    assert sorted(p.name for p in tmp_path.iterdir()) == ["Probe"]  # the probe file is gone again


def test_extract_bundle_refuses_names_this_runner_cannot_hold(tmp_path, monkeypatch):
    zpath = tmp_path / "b.zip"
    with zipfile.ZipFile(zpath, "w") as z:
        z.writestr("train.json", "{}")
        z.writestr("dataset/images/g/A.jpg", b"1")
        z.writestr("dataset/images/g/a.jpg", b"2")
    monkeypatch.setattr(portable, "is_case_insensitive", lambda d: True)
    with pytest.raises(portable.BundleNotPortable):
        extract_bundle(str(zpath), str(tmp_path / "out"))
    assert not (tmp_path / "out" / "dataset").exists()  # refused before anything was written


@pytest.mark.skipif(sys.platform != "win32", reason="the ctypes calls exist on Windows only")
def test_windows_readings_are_real():
    import os
    st = winsys.memory_status()
    assert st["totalMb"] > 1024 and 0 < st["availableMb"] <= st["totalMb"]
    assert resources.system_memory()["totalMb"] == st["totalMb"]
    table = winsys.process_table(only=os.getpid())
    assert table[os.getpid()][1] > 1 << 20 and memguard.tree_footprint(os.getpid()) > 1 << 20
    assert resources.SwapMeter().used_mb() is None  # no swap guard on Windows
