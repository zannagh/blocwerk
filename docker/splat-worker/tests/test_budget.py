"""Memory budget (resources.py), COLMAP self-tuning (tuning.py) and the step-down retry (sfm.py)."""
import sys

import pytest

from splatworker import procs, resources, sfm, tuning
from splatworker.colmap import Colmap
from splatworker.procs import MemoryLimitError, ToolRun

from test_limits import CAPS, V42, flags

VM_STAT = """Mach Virtual Memory Statistics: (page size of 16384 bytes)
Pages free:                                    44064.
Pages active:                                 250713.
Pages inactive:                               240754.
Pages speculative:                             10826.
Pages throttled:                                   0.
Pages wired down:                             230394.
Pages purgeable:                               16773.
"Translation faults":                     9397808910.
"""
MEMINFO = "MemTotal:       10180000 kB\nMemFree:  1000000 kB\nMemAvailable:    8000000 kB\nSwapTotal: 4194300 kB\nSwapFree: 4000000 kB\n"
B3 = [13402, 12382, 12248, 10361, 10354, 10119, 10079, 9846, 9822, 9384, 8985, 8808, 8495, 8239]  # the real capture


def test_parsers():
    assert resources.parse_vm_stat(VM_STAT) == 16384 * (44064 + 240754 + 10826 + 16773)
    assert resources.parse_swapusage("total = 7168.00M  used = 5986.19M  free = 1181.81M  (encrypted)") == (7168.0, 5986.19)
    mi = resources.parse_meminfo(MEMINFO)
    assert mi["MemAvailable"] == 8000000 * 1024 and mi["SwapTotal"] - mi["SwapFree"] == 194300 * 1024


def test_cgroup_v2_limit_and_unlimited(tmp_path):
    (tmp_path / "memory.max").write_text("max\n")
    assert resources.cgroup_memory(str(tmp_path)) is None
    (tmp_path / "memory.max").write_text(str(6 << 30))
    (tmp_path / "memory.current").write_text(str(2 << 30))
    (tmp_path / "memory.stat").write_text(f"anon 123\ninactive_file {1 << 30}\n")
    assert resources.cgroup_memory(str(tmp_path)) == (6 << 30, 2 << 30, 1 << 30)


def test_cgroup_v1(tmp_path):
    (tmp_path / "memory").mkdir()
    (tmp_path / "memory" / "memory.limit_in_bytes").write_text(str(9223372036854771712))  # "unlimited"
    assert resources.cgroup_memory(str(tmp_path)) is None
    (tmp_path / "memory" / "memory.limit_in_bytes").write_text(str(4 << 30))
    (tmp_path / "memory" / "memory.usage_in_bytes").write_text(str(1 << 30))
    assert resources.cgroup_memory(str(tmp_path)) == (4 << 30, 1 << 30, 0)


@pytest.mark.parametrize("total,avail,cap,expect", [
    (16384, 12000, 0, 9830),   # 60 % of the machine
    (16384, 4800, 0, 4800),    # what is available now
    (16384, 1000, 0, 3072),    # floor: extraction must be able to run at all
    (4096, 500, 0, 2457),      # ... but never above 60 % of a small machine
    (16384, 12000, 6144, 6144),  # SPLAT_MAX_MEMORY_MB is a hard upper bound
    (16384, 2000, 6144, 3072),   # ... never a floor
])
def test_memory_budget(total, avail, cap, expect):
    budget, why = resources.memory_budget({"totalMb": total, "availableMb": avail}, cap)
    assert budget == expect
    assert ("capped by SPLAT_MAX_MEMORY_MB" in why) == (cap > 0 and cap < min(int(total * 0.6), max(avail, 3072)))


def test_memory_budget_of_this_machine_is_sane():
    info = resources.system_memory()
    budget, why = resources.memory_budget(info)
    assert info["totalMb"] and 1024 <= budget <= info["totalMb"] * 0.6 + 1 and "GB" in why


def test_guided_model_covers_the_measurements():
    # measured peaks on the 14 photos: 1 thread 6178 MB, 2 threads 6699 MB, 4 threads 9734 MB
    for threads, measured in ((1, 6178), (2, 6699), (4, 9734)):
        est = tuning.guided_mb(B3, threads)
        assert measured <= est <= measured * 1.15


def test_tiers_follow_the_budget():
    big = tuning.matching_tiers(16000, B3, 4)
    assert [t.name for t in big] == ["guided, 4 threads", "guided, 1 thread", "unguided + triangulate"]
    mac = tuning.matching_tiers(9830, B3, 4)  # 16 GB machine, idle
    assert [(t.guided, t.threads) for t in mac] == [(True, 3), (True, 1), (False, 4)]
    one = tuning.matching_tiers(7000, B3, 4)
    assert [t.name for t in one] == ["guided, 1 thread", "unguided + triangulate"]
    small = tuning.matching_tiers(3072, B3, 4)
    assert len(small) == 1 and small[0].loose and not small[0].guided
    assert tuning.matching_tiers(16000, [3000] * 5, 4)[0].name == "guided, 4 threads"  # few features: cheap


def test_extraction_threads():
    assert tuning.extraction_threads(9830, 4) == 4
    assert tuning.extraction_threads(1500, 4) == 1
    assert tuning.extraction_threads(2000, 4) == 2
    assert tuning.extraction_threads(1000, 0) == 1


def test_tier_options_on_the_command_line(tmp_path):
    cm = Colmap("colmap", "log", str(tmp_path), CAPS, V42)
    f = flags(cm.match_args("db", "exhaustive", 14, tuning.Tier(True, 2, 7000)))
    assert f["FeatureMatching.guided_matching"] == "1" and f["FeatureMatching.num_threads"] == "2"
    assert "SiftMatching.max_ratio" not in f
    loose = tuning.matching_tiers(3072, B3, 4)[0]
    f = flags(cm.pairs_args("db", "pairs.txt", loose))
    assert f["FeatureMatching.guided_matching"] == "0" and f["FeatureMatching.num_threads"] == "4"
    assert f["SiftMatching.max_ratio"] == "0.9" and f["SiftMatching.cross_check"] == "0"
    assert f["FeatureMatching.max_num_matches"] == "8192"
    t = cm.triangulate_args("db", "img", "sparse/0", "tri")
    assert t[0] == "point_triangulator"
    f = flags(t)
    assert f["clear_points"] == "0" and f["Mapper.tri_ignore_two_view_tracks"] == "0" and f["input_path"] == "sparse/0"


def test_extraction_threads_cap_is_used(tmp_path):
    cm = Colmap("colmap", "log", str(tmp_path), {**CAPS, "extract_threads": 2}, V42)
    assert flags(cm.extract_args("db", "img", "l", True, None, 8192))["FeatureExtraction.num_threads"] == "2"


class LadderColmap:
    """Kills every guided run like the memory guard would; unguided works."""

    gpu_extraction = False

    def __init__(self, *a, caps=None, **kw):
        self.caps, self.calls = dict(caps or {}), []

    def feature_counts(self, db):
        return B3

    def extract(self, *a):
        self.calls.append(("extract", self.caps["extract_threads"]))
        if len(self.calls) == 1:
            raise MemoryLimitError("sfm-features", "COLMAP exceeded", "swap")

    def match(self, db, matcher, n, report, tier):
        self.calls.append(("match", tier.name))
        if tier.guided:
            raise MemoryLimitError("sfm-matching", "COLMAP exceeded the 9 GB memory limit", "memory")


class FakeRun:
    def __init__(self, tmp_path):
        self.dir, self.log, self.note, self.details = str(tmp_path), str(tmp_path / "tools.log"), None, []

    def begin(self, stage):
        self.stage, self.note = stage, None

    def report(self, fraction, detail=None):
        self.details.append((self.stage, self.note))


def test_step_down_on_a_memory_kill(tmp_path, monkeypatch):
    monkeypatch.setattr(sfm, "Colmap", LadderColmap)
    monkeypatch.setattr(sfm.settings, "max_memory_mb", 0)
    run = FakeRun(tmp_path)
    s = sfm.Sfm(run, {"totalMb": 32768, "availableMb": 30000})
    assert s.budget_mb == 19660
    s.extract("db", "img", [], 16384)
    tier = s.match("db", "exhaustive", 14)
    assert s.cm.calls == [("extract", 4), ("extract", 2), ("match", "guided, 4 threads"),
                          ("match", "guided, 1 thread"), ("match", "unguided + triangulate")]
    assert tier.loose and s.stats()["matchingTier"] == "unguided + triangulate"
    assert [r["reason"] for r in s.stats()["memoryRetries"]] == ["swap", "memory", "memory"]
    assert run.details[0] == ("sfm-features", "memory budget 19.2 GB (29.3 GB available of 32.0 GB); 4 threads")
    assert ("sfm-matching", "guided, 4 threads (est. 9.8 GB of 19.2 GB)") in run.details
    log = (tmp_path / "tools.log").read_text()
    assert "retrying as unguided + triangulate" in log and "matching tier: guided, 4 threads" in log


def test_the_last_tier_failing_fails_the_job(tmp_path, monkeypatch):
    class Always(LadderColmap):
        def match(self, *a, **kw):
            raise MemoryLimitError("sfm-matching", "COLMAP exceeded", "memory")
    monkeypatch.setattr(sfm, "Colmap", Always)
    s = sfm.Sfm(FakeRun(tmp_path), {"totalMb": 4096, "availableMb": 4000})
    with pytest.raises(MemoryLimitError):
        s.match("db", "exhaustive", 14)


class GrowingSwap:
    def __init__(self):
        self.n = 0

    def used_mb(self):
        self.n += 1
        return 1000.0 + 400 * self.n


def test_watchdog_stops_a_big_tool_when_the_system_swaps(tmp_path, monkeypatch):
    monkeypatch.setattr(procs, "_SWAP", GrowingSwap())
    big = "x = b'x' * (150 * 1024 * 1024); import time; time.sleep(30)"
    with pytest.raises(MemoryLimitError) as e:
        ToolRun("sfm-matching", [sys.executable, "-c", big], str(tmp_path),
                str(tmp_path / "t.log"), mem_limit_mb=250, swap_limit_mb=1000, name="COLMAP").run()
    assert e.value.kind == "swap"
    assert "COLMAP was stopped: the machine's swap grew by" in str(e.value) and "within 120 s" in str(e.value)
    assert "swap +" in (tmp_path / "t.log").read_text()


def test_a_small_tool_is_not_blamed_for_system_swap(tmp_path, monkeypatch):
    monkeypatch.setattr(procs, "_SWAP", GrowingSwap())
    seen = []
    ToolRun("train", [sys.executable, "-c", "import time; time.sleep(2); print('done')"], str(tmp_path),
            str(tmp_path / "t.log"), seen.append, mem_limit_mb=4000, swap_limit_mb=1000, name="Brush").run()
    assert seen == ["done"] and "swap +" in (tmp_path / "t.log").read_text()


class Readings:
    def __init__(self, values):
        self.values = list(values)

    def used_mb(self):
        return self.values.pop(0)


def test_swap_guard_reacts_to_growth_rate_not_level():
    from splatworker.memguard import Watchdog
    t = [0.0]
    # slow creep: +300 MB a minute for 20 minutes from an already high level -> never 1 GB per 2 min
    slow = Watchdog(None, 4000, swap_limit_mb=1000, swap_meter=Readings([9000 + 300 * i for i in range(30)]),
                    clock=lambda: t[0])
    for _ in range(20):
        t[0] += 60
        assert not slow._swap_grew_too_much()
    assert slow.swap_growth_mb == 600
    # fast: +1.2 GB within 40 s
    t[0] = 0.0
    fast = Watchdog(None, 4000, swap_limit_mb=1000, swap_meter=Readings([5000, 5400, 5800, 6200]), clock=lambda: t[0])
    t[0] = 20
    assert not fast._swap_grew_too_much()
    t[0] = 30
    assert not fast._swap_grew_too_much()
    t[0] = 40
    assert fast._swap_grew_too_much() and fast.swap_growth_mb == 1200


def test_swap_meter_reads_something():
    used = resources.SwapMeter().used_mb()
    assert used is None or used >= 0
