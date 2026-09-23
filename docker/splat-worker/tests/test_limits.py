"""Memory caps: COLMAP command construction for 3.9 and 4.x option names, and the watchdog kill path."""
import os
import sys
import time

import pytest

from computejobs.child import JobError
from splatworker import memguard
from splatworker.colmap import Colmap
from splatworker.procs import ToolRun

# Option names as `colmap feature_extractor -h` / `exhaustive_matcher -h` list them (verified 2026-09-23)
V39 = {"SiftExtraction.use_gpu", "SiftExtraction.max_image_size", "SiftExtraction.num_threads",
       "SiftExtraction.max_num_features", "SiftMatching.use_gpu", "SiftMatching.num_threads",
       "SiftMatching.max_num_matches", "SiftMatching.guided_matching"}
V42 = {"FeatureExtraction.use_gpu", "FeatureExtraction.max_image_size", "FeatureExtraction.num_threads",
       "SiftExtraction.max_num_features", "FeatureMatching.use_gpu", "FeatureMatching.num_threads",
       "FeatureMatching.max_num_matches", "FeatureMatching.guided_matching"}
CAPS = {"max_image_size": 2400, "max_features": 8192, "max_matches": 8192, "threads": 4, "max_memory_mb": 6144}


def flags(args):
    return {a[2:]: b for a, b in zip(args, args[1:]) if a.startswith("--")}


@pytest.mark.parametrize("options,ext,match", [(V39, "SiftExtraction", "SiftMatching"),
                                                (V42, "FeatureExtraction", "FeatureMatching")])
def test_colmap_commands_carry_the_caps_under_the_right_names(tmp_path, options, ext, match):
    cm = Colmap("colmap", "log", str(tmp_path), CAPS, options)
    f = flags(cm.extract_args("db", "img", "list.txt", True, None, 16384))
    assert f[f"{ext}.max_image_size"] == "2400" and f[f"{ext}.num_threads"] == "4"
    assert f[f"{ext}.use_gpu"] == "0" and f["SiftExtraction.max_num_features"] == "8192"  # 16384 capped
    f = flags(cm.match_args("db", "exhaustive", 14))
    assert f[f"{match}.max_num_matches"] == "8192" and f[f"{match}.num_threads"] == "4"
    assert f[f"{match}.use_gpu"] == "0"
    assert f[f"{match}.guided_matching"] == "0"  # guided = 2x matchers x features^2: the memory hog
    seq = flags(cm.match_args("db", "sequential", 200))
    assert seq[f"{match}.guided_matching"] == "0" and seq[f"{match}.num_threads"] == "4"
    assert seq[f"{match}.max_num_matches"] == "8192"
    assert flags(cm.map_args("db", "img", "out"))["Mapper.num_threads"] == "4"
    other = "Feature" if ext.startswith("Sift") else "SiftExtraction.max_image"
    assert not any(a.startswith(f"--{other}") for a in cm.extract_args("db", "img", "l", True, None, 100))


def test_caps_off_and_smaller_requests_pass_through(tmp_path):
    off = Colmap("colmap", "log", str(tmp_path), {}, V42)
    args = off.extract_args("db", "img", "l", False, None, 16384) + off.match_args("db", "sequential", 200) \
        + off.map_args("db", "img", "out")
    f = flags(args)
    assert f["SiftExtraction.max_num_features"] == "16384"
    assert not any(k.endswith(("max_image_size", "num_threads", "max_num_matches")) for k in f)
    capped = Colmap("colmap", "log", str(tmp_path), CAPS, V42)
    assert flags(capped.extract_args("db", "img", "l", False, None, 4000))["SiftExtraction.max_num_features"] == "4000"


def test_unknown_option_spelling_is_left_out(tmp_path):
    cm = Colmap("colmap", "log", str(tmp_path), CAPS, V42 - {"FeatureMatching.max_num_matches"})
    assert not any("max_num_matches" in a for a in cm.match_args("db", "exhaustive", 14))


def test_describe_mb():
    assert memguard.describe_mb(6144) == "6 GB" and memguard.describe_mb(200) == "200 MB"
    assert memguard.describe_mb(1536) == "1.5 GB"


HOG = "import time; x = b'x' * (600 * 1024 * 1024); print('allocated', flush=True); time.sleep(30)"


def test_watchdog_kills_a_tool_above_the_ceiling(tmp_path):
    t0 = time.time()
    with pytest.raises(JobError) as e:
        ToolRun("sfm-matching", [sys.executable, "-c", HOG], str(tmp_path), str(tmp_path / "t.log"),
                mem_limit_mb=200, name="COLMAP").run()
    assert time.time() - t0 < 20
    assert e.value.stage == "sfm-matching"
    assert "COLMAP exceeded the 200 MB memory limit; try fewer or smaller photos" in str(e.value)
    assert "# peak memory" in (tmp_path / "t.log").read_text()


def test_watchdog_under_a_pty_too(tmp_path):
    with pytest.raises(JobError, match="Brush exceeded the 200 MB memory limit"):
        ToolRun("train", [sys.executable, "-c", HOG], str(tmp_path), str(tmp_path / "t.log"),
                use_pty=True, mem_limit_mb=200, name="Brush").run()


def test_tool_under_the_ceiling_runs_normally(tmp_path):
    seen = []
    ToolRun("sfm-mapping", [sys.executable, "-c", "x = bytearray(20 * 1024 * 1024); print('ok')"], str(tmp_path),
            str(tmp_path / "t.log"), seen.append, mem_limit_mb=200, limit_address_space=True).run()
    assert seen == ["ok"]


def test_tree_footprint_sees_a_live_process_and_zero_once_gone():
    import subprocess
    p = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(5)"])
    try:
        time.sleep(0.3)
        assert memguard.tree_footprint(p.pid) > 1024 * 1024
    finally:
        p.kill()
        p.wait()
    assert memguard.tree_footprint(p.pid) == 0


def _own_rss():
    import subprocess
    if sys.platform.startswith("linux"):
        with open("/proc/self/statm") as fh:
            return int(fh.read().split()[1]) * os.sysconf("SC_PAGE_SIZE")
    out = subprocess.run(["ps", "-o", "rss=", "-p", str(os.getpid())], capture_output=True, text=True).stdout
    return int(out.strip()) * 1024


def test_footprint_of_this_process_is_sane_and_at_least_its_rss():
    ballast = bytearray(64 * 1024 * 1024)  # touched pages, so the reading is well above noise
    ballast[::4096] = b"x" * len(ballast[::4096])
    rss = _own_rss()
    used = memguard.tree_footprint(os.getpid())
    assert rss >= 64 * 1024 * 1024
    assert used >= rss * 0.95  # rss sampled first; allow for the interpreter shrinking in between
    assert used < 8 * 1024 ** 3
    del ballast


@pytest.mark.skipif(sys.platform != "darwin", reason="libproc is macOS only")
def test_macos_uses_libproc_not_ps():
    assert memguard._LIBPROC is not None
    assert memguard._darwin_footprint(os.getpid()) > 1024 * 1024


LINUX_STATUS = """Name:\tcolmap
State:\tR (running)
Tgid:\t4242
Pid:\t4242
PPid:\t17
VmPeak:\t 9000000 kB
VmSize:\t 8800000 kB
VmRSS:\t 4900000 kB
RssAnon:\t 4800000 kB
VmSwap:\t 2900000 kB
Threads:\t8
"""


def test_linux_status_counts_resident_plus_swap():
    assert memguard.parse_linux_status(LINUX_STATUS) == (17, (4900000 + 2900000) * 1024)
    # a kernel thread / zombie has no Vm* lines at all
    assert memguard.parse_linux_status("Name:\tkthreadd\nPPid:\t0\n") == (0, 0)


@pytest.mark.skipif(not sys.platform.startswith("linux"), reason="RLIMIT_AS is only enforced on Linux")
def test_address_space_limit_turns_an_allocation_failure_into_the_memory_message(tmp_path):
    # MemoryError at once under 200 MB * 1.5 of address space (or, where emulation ignores RLIMIT_AS, the watchdog)
    script = "x = b'x' * (2000 * 1024 * 1024)"
    with pytest.raises(JobError, match="exceeded the 200 MB memory limit"):
        ToolRun("sfm-features", [sys.executable, "-c", script], str(tmp_path), str(tmp_path / "t.log"),
                mem_limit_mb=200, limit_address_space=True, name="COLMAP").run()


@pytest.mark.skipif(not sys.platform.startswith("linux"), reason="RLIMIT_AS is only set on Linux")
def test_address_space_limit_is_set_in_the_child_with_headroom(tmp_path):
    seen = []
    ToolRun("sfm-features", [sys.executable, "-c", "import resource; print(resource.getrlimit(resource.RLIMIT_AS)[0])"],
            str(tmp_path), str(tmp_path / "t.log"), seen.append, mem_limit_mb=200, limit_address_space=True).run()
    if seen == ["-1"]:  # e.g. Rosetta/qemu amd64 emulation on a Mac ignores RLIMIT_AS (even `ulimit -v`)
        pytest.skip("RLIMIT_AS is not supported on this (emulated) kernel")
    assert seen == [str(300 * 1024 * 1024)]
