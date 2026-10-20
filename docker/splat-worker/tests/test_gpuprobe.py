"""GPU detection on every OS (gpuprobe.py) and the quality a runner claims from it (caps.max_quality)."""
import json

from splatworker import gpuprobe
from splatworker.gpurunner import caps
from splatworker.settings import settings

GB = 1024 ** 3


def test_windows_registry_adapters_use_the_64_bit_size_and_skip_virtual_ones():
    entries = [{"DriverDesc": "AMD Radeon RX 7900 XTX", "HardwareInformation.qwMemorySize": 24 * GB,
                "HardwareInformation.MemorySize": 4 * GB - 1},  # the 32-bit value tops out at 4 GB
               {"DriverDesc": "Intel(R) UHD Graphics 770", "HardwareInformation.MemorySize": (128 << 20).to_bytes(4, "little")},
               {"DriverDesc": "Microsoft Basic Render Driver"},
               {"DriverDesc": "Parsec Virtual Display Adapter", "HardwareInformation.qwMemorySize": 1 * GB},
               {}]
    docs = gpuprobe.parse_windows_adapters(entries)
    assert [d["name"] for d in docs] == ["AMD Radeon RX 7900 XTX", "Intel(R) UHD Graphics 770"]
    amd, intel = docs
    assert amd == {"name": "AMD Radeon RX 7900 XTX", "vendor": "amd", "totalMb": 24576, "freeMb": 24576,
                   "unified": False}
    assert intel["vendor"] == "intel" and intel["totalMb"] == 128 and intel["unified"] is True
    assert gpuprobe.best(docs) is amd


def test_best_prefers_a_discrete_card_over_a_bigger_shared_pool():
    igpu = gpuprobe.gpu_doc("AMD Radeon(TM) Graphics", 512)
    card = gpuprobe.gpu_doc("NVIDIA GeForce RTX 3060", 12288)
    assert gpuprobe.best([igpu, card]) is card
    assert gpuprobe.best([igpu]) is igpu and gpuprobe.best([]) is None and gpuprobe.best([None]) is None


def test_amd_sysfs_reads_amdgpu_cards_only(tmp_path):
    def card(name, vendor, total=None, used=None):
        dev = tmp_path / name / "device"
        dev.mkdir(parents=True)
        (dev / "vendor").write_text(vendor + "\n")
        if total is not None:
            (dev / "mem_info_vram_total").write_text(f"{total}\n")
        if used is not None:
            (dev / "mem_info_vram_used").write_text(f"{used}\n")
    card("card0", "0x8086")  # Intel: no VRAM file
    card("card1", "0x1002", 16 * GB, 2 * GB)
    card("card1-DP-1", "0x1002", 1)  # a connector, not a card
    (tmp_path / "renderD128").mkdir()
    docs = gpuprobe.parse_amd_sysfs(str(tmp_path))
    assert docs == [{"name": "AMD GPU (card1)", "vendor": "amd", "totalMb": 16384, "freeMb": 14336, "unified": False}]
    assert gpuprobe.parse_amd_sysfs(str(tmp_path / "missing")) == []


def test_apple_name_and_vendor_guess():
    doc = json.dumps({"SPDisplaysDataType": [{"sppci_model": "Apple M4 Pro"}]})
    assert gpuprobe.apple_name(doc) == "Apple M4 Pro" and gpuprobe.apple_name("not json") is None
    assert gpuprobe.vendor_of("NVIDIA GeForce RTX 4090") == "nvidia" and gpuprobe.vendor_of("?") == "unknown"


def test_detect_per_platform(monkeypatch):
    monkeypatch.setattr(settings, "vram_mb", 0)
    monkeypatch.setattr(gpuprobe, "_run", lambda cmd: json.dumps({"SPDisplaysDataType": [{"sppci_model": "Apple M4"}]}))
    mac = gpuprobe.detect("darwin", total_ram_mb=49152)
    assert mac == {"name": "Apple M4", "vendor": "apple", "totalMb": 49152, "freeMb": 49152, "unified": True}
    monkeypatch.setattr(gpuprobe.gpu, "vram", lambda: {"name": "NVIDIA GeForce RTX 4070", "totalMb": 12282,
                                                       "freeMb": 11000})
    nv = gpuprobe.detect("win32")
    assert nv["vendor"] == "nvidia" and nv["freeMb"] == 11000 and nv["unified"] is False
    monkeypatch.setattr(gpuprobe.gpu, "vram", lambda: None)
    monkeypatch.setattr(gpuprobe, "_windows_registry_entries",
                        lambda: [{"DriverDesc": "AMD Radeon RX 6800", "HardwareInformation.qwMemorySize": 16 * GB}])
    assert gpuprobe.detect("win32")["totalMb"] == 16384
    monkeypatch.setattr(gpuprobe, "parse_amd_sysfs", lambda root="": [])
    assert gpuprobe.detect("linux") is None
    monkeypatch.setattr(settings, "vram_mb", 8000)
    assert gpuprobe.detect("darwin")["totalMb"] == 8000 and gpuprobe.detect("darwin")["unified"] is False


def test_brush_quality_takes_the_vram_of_a_discrete_card_and_the_ram_of_unified_memory(monkeypatch):
    monkeypatch.delenv("RUNNER_MAX_QUALITY", raising=False)
    # unified (Apple): the host budget alone, as before
    assert caps.max_quality("brush", 49152, 20000) == "max"
    assert caps.max_quality("brush", 16384, 6000, unified=True) == "high"
    # a discrete card: the lower of the RAM tier and the VRAM tier
    assert caps.max_quality("brush", 24564, 20000, unified=False) == "max"  # 24 GB card, 20 GB budget
    assert caps.max_quality("brush", 8188, 20000, unified=False) == "high"  # 8 GB card
    assert caps.max_quality("brush", 4096, 20000, unified=False) == "draft"  # 4 GB card
    assert caps.max_quality("brush", 24564, 6000, unified=False) == "high"  # RAM limits
    assert caps.max_quality("brush", None, 20000, unified=False) == "draft"  # discrete, VRAM unknown: assume small
    assert caps.max_quality("brush", None, 20000) == "max"  # nothing known (no adapter seen): the budget, as before
    assert caps.max_quality("gsplat", 16376, 8000, unified=False) == "ultra"  # gsplat unchanged
    assert caps.max_quality("gsplat", 65536, 8000, unified=True) == "draft"  # host RAM is never gsplat's VRAM


def test_only_apple_and_known_igpus_are_unified():
    for name in ("Intel(R) UHD Graphics 770", "Intel(R) Iris(R) Xe Graphics", "Intel(R) HD Graphics 620",
                 "Intel(R) Arc(TM) Graphics", "AMD Radeon(TM) Graphics", "AMD Radeon(TM) Vega 8 Graphics",
                 "AMD Radeon 780M Graphics"):
        assert gpuprobe.gpu_doc(name, 512)["unified"] is True, name
    for name in ("Intel(R) Arc(TM) A770 Graphics", "AMD Radeon RX 7800 XT", "AMD Radeon RX Vega 64",
                 "NVIDIA GeForce GTX 1050", "Some Future GPU"):
        assert gpuprobe.gpu_doc(name, None)["unified"] is False, name  # unknown VRAM is not "unified"


def test_a_registry_card_without_a_size_is_discrete_and_trains_draft(monkeypatch):
    docs = gpuprobe.parse_windows_adapters([{"DriverDesc": "AMD Radeon RX 6600"}])
    assert docs == [{"name": "AMD Radeon RX 6600", "vendor": "amd", "totalMb": None, "freeMb": None,
                     "unified": False}]
    monkeypatch.setattr(caps, "system_memory", lambda: {"totalMb": 65536})
    monkeypatch.setattr(caps.gpuprobe, "detect", lambda total_ram_mb=None: docs[0])
    name, vram, unified = caps.gpu_info()
    assert (vram, unified) == (None, False)
    assert caps.max_quality("brush", vram, 30000, unified=unified) == "draft"


def test_gpu_info_reports_ram_on_unified_and_vram_on_discrete(monkeypatch):
    monkeypatch.setattr(caps, "system_memory", lambda: {"totalMb": 65536})
    monkeypatch.setattr(caps.gpuprobe, "detect", lambda total_ram_mb=None: gpuprobe.gpu_doc("RX 7900", 24576))
    assert caps.gpu_info() == ("RX 7900", 24576, False)
    monkeypatch.setattr(caps.gpuprobe, "detect", lambda total_ram_mb=None: gpuprobe.gpu_doc("Intel UHD", 128))
    assert caps.gpu_info() == ("Intel UHD", 65536, True)
    monkeypatch.setattr(caps.gpuprobe, "detect", lambda total_ram_mb=None: None)
    assert caps.gpu_info() == ("unknown", None, True)


def test_the_real_probe_runs_here():
    """Whatever this machine is: a document or None, never an exception."""
    doc = gpuprobe.detect(total_ram_mb=1024)
    assert doc is None or {"name", "vendor", "totalMb", "freeMb", "unified"} <= set(doc)
