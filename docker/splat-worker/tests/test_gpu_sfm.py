"""GPU SfM (COLMAP_USE_GPU), the SIFT quality options, the VRAM matching planner, vocabulary-tree loop
closure, the global mapper, and the SfM / held-out evaluation stats."""
import pytest

from computejobs.child import JobError
from splatworker import sfm, tuning
from splatworker.colmap import Colmap
from splatworker.colmap_model import read_text_model
from splatworker.frames import build_pairs
from splatworker.gsplat_data import eval_split
from splatworker.gsplat_trainer import GsplatPlan, args_for
from splatworker.parsers import GsplatParser
from splatworker.procs import MemoryLimitError
from splatworker.profiles import PROFILES

from test_budget import B3, FakeRun
from test_limits import CAPS, V39, V42, flags

V42_GPU = V42 | {"FeatureExtraction.gpu_index", "FeatureMatching.gpu_index"}
V39_GPU = V39 | {"SiftExtraction.gpu_index", "SiftMatching.gpu_index"}
GPU_16G = {"name": "NVIDIA GeForce RTX 4070 Ti SUPER", "totalMb": 16376, "freeMb": 14900}
REAL_RUN = [16384] * 353  # The Attic: 53 photos + 300 frames at COLMAP_MAX_FEATURES=16384


def test_defaults_are_unchanged_cpu_with_dsp_and_affine(tmp_path):
    f = flags(Colmap("colmap", "log", str(tmp_path), CAPS, V42_GPU).extract_args("db", "img", "l", True, None, 8192))
    assert f["FeatureExtraction.use_gpu"] == "0" and "FeatureExtraction.gpu_index" not in f
    assert f["SiftExtraction.domain_size_pooling"] == "1" and f["SiftExtraction.estimate_affine_shape"] == "1"


@pytest.mark.parametrize("options,ns", [(V42_GPU, "FeatureExtraction"), (V39_GPU, "SiftExtraction")])
def test_gpu_extraction_needs_dsp_and_affine_off(tmp_path, options, ns):
    gpu = {**CAPS, "use_gpu": True, "gpu_index": 1, "dsp": False, "affine": False}
    f = flags(Colmap("colmap", "log", str(tmp_path), gpu, options).extract_args("db", "img", "l", True, None, 16384))
    assert f[f"{ns}.use_gpu"] == "1" and f[f"{ns}.gpu_index"] == "1"
    assert f["SiftExtraction.domain_size_pooling"] == "0" and f["SiftExtraction.estimate_affine_shape"] == "0"
    for covariant in ({"dsp": True}, {"affine": True}):  # COLMAP runs covariant SIFT on the CPU only
        cm = Colmap("colmap", "log", str(tmp_path), {**gpu, **covariant}, options)
        assert not cm.gpu_extraction and flags(cm.extract_args("db", "img", "l", True, None, 1))[f"{ns}.use_gpu"] == "0"


def test_gpu_tier_on_the_command_line(tmp_path):
    cm = Colmap("colmap", "log", str(tmp_path), {**CAPS, "use_gpu": True}, V42_GPU)
    guided, unguided, cpu = tuning.gpu_matching_tiers(14900, [16384] * 48, 32768, 8)
    f = flags(cm.pairs_args("db", "pairs.txt", guided))
    assert f["FeatureMatching.use_gpu"] == "1" and f["FeatureMatching.gpu_index"] == "0"
    assert f["FeatureMatching.guided_matching"] == "1" and f["FeatureMatching.max_num_matches"] == "16384"
    assert "SiftMatching.cross_check" not in f
    f = flags(cm.match_args("db", "exhaustive", 48, unguided))
    assert f["FeatureMatching.use_gpu"] == "1" and f["SiftMatching.cross_check"] == "0"
    f = flags(cm.match_args("db", "exhaustive", 48, cpu))
    assert f["FeatureMatching.use_gpu"] == "0" and f["FeatureMatching.max_num_matches"] == "8192"  # the caps'


def test_gpu_vram_model_matches_siftgpu_buffers():
    # 2 matchers x (M^2 floats + M x M/8 x 4 floats) at 16384 = 3 GiB, + the base
    assert tuning.gpu_match_vram_mb(16384, True) == tuning.GPU_MATCH_BASE_MB + 3072
    assert tuning.gpu_match_vram_mb(16384, False, cross_check=False) == tuning.GPU_MATCH_BASE_MB + 1024


def test_gpu_planner_never_picks_a_cpu_guided_tier():
    for vram in (16000, 8000, 4000, 2500, 1200, 0):
        for counts in (B3, REAL_RUN, [40000] * 10):
            tiers = tuning.gpu_matching_tiers(vram, counts, 32768, 8)
            cpu = [t for t in tiers if not t.gpu]
            assert len(cpu) == 1 and not cpu[0].guided and cpu[0].threads == 8 and tiers[-1] is cpu[0]
    # the real run that stalled on the CPU ("guided, 1 thread", est. 21.7 GB): GPU guided at full features
    best = tuning.gpu_matching_tiers(14900, REAL_RUN, 32768, 8)[0]
    assert best.gpu and best.guided and best.max_matches == 16384 and best.vram_mb < 14900 * 0.9
    assert best.estimate_mb == tuning.GPU_HOST_MB  # host memory is not planned as if it matched on the CPU


def test_gpu_planner_fits_the_buffer_to_the_vram():
    tiers = tuning.gpu_matching_tiers(3000, REAL_RUN, 32768, 8)  # 3 GB free: guided only at fewer features
    assert tiers[0].guided and tiers[0].max_matches < 16384 and tiers[0].vram_mb <= 2700
    assert tiers[1].gpu and not tiers[1].guided and tiers[1].max_matches > tiers[0].max_matches
    assert tuning.gpu_matching_tiers(14900, [9000] * 5, 32768, 4)[0].max_matches == 9000  # never above the need
    assert tuning.gpu_matching_tiers(14900, REAL_RUN, 8192, 4)[0].max_matches == 8192  # COLMAP_MAX_MATCHES caps it
    assert tuning.gpu_matching_tiers(64000, [60000] * 3, 0, 4)[0].max_matches == tuning.GPU_MAX_MATCHES
    assert [t.gpu for t in tuning.gpu_matching_tiers(500, REAL_RUN, 32768, 4)] == [False]


class GpuColmap:
    """Stands in for Colmap: the first GPU matching run fails like a CUDA error would."""
    gpu_extraction = False

    def __init__(self, *a, caps=None, **kw):
        self.caps, self.calls = dict(caps or {}), []

    def feature_counts(self, db):
        return REAL_RUN

    def match_pairs(self, db, pairs, report, tier):
        self.calls.append(tier.name)
        if len(self.calls) == 1:
            raise JobError("sfm-matching", "COLMAP failed (exit 1): Failed to create feature matcher")

    def match_vocab(self, db, queries, k, tree, tier):
        self.calls.append(("vocab", len(queries), k, tree, tier.name))


def _gpu_sfm(tmp_path, monkeypatch, info=GPU_16G, **env):
    monkeypatch.setattr(sfm, "Colmap", GpuColmap)
    for k, v in {"colmap_use_gpu": True, "max_memory_mb": 0, "colmap_max_matches": 32768, **env}.items():
        monkeypatch.setattr(sfm.settings, k, v)
    return sfm.Sfm(FakeRun(tmp_path), {"totalMb": 32768, "availableMb": 30000}, gpu_info=info)


def test_sfm_matches_on_the_gpu_and_steps_down_when_the_gpu_run_fails(tmp_path, monkeypatch):
    s = _gpu_sfm(tmp_path, monkeypatch)
    assert s.cm.caps["use_gpu"] and s.stats()["colmapGpu"] == GPU_16G["name"]
    tier = s.match("db", "pairs", 353, pairs=[("a", "b")])
    assert s.cm.calls == ["GPU guided, <= 16384 features", "GPU unguided + triangulate, <= 16384 features"]
    assert tier.gpu and tier.loose and s.retries[0]["reason"] == "gpu"
    assert "free VRAM" in (tmp_path / "tools.log").read_text()


def test_use_gpu_without_a_visible_gpu_falls_back_to_the_cpu_tiers(tmp_path, monkeypatch):
    monkeypatch.setattr(sfm.gpu, "vram", lambda: None)
    s = _gpu_sfm(tmp_path, monkeypatch, info=None)
    assert not s.cm.caps["use_gpu"] and not any(t.gpu for t in s.tiers("db"))
    assert "no NVIDIA GPU is visible" in (tmp_path / "tools.log").read_text()


def test_a_cpu_failure_that_is_not_memory_still_fails_the_job(tmp_path, monkeypatch):
    class Broken(GpuColmap):
        def match(self, *a, **kw):
            raise JobError("sfm-matching", "COLMAP failed")
    monkeypatch.setattr(sfm, "Colmap", Broken)
    monkeypatch.setattr(sfm.settings, "colmap_use_gpu", False)
    s = sfm.Sfm(FakeRun(tmp_path), {"totalMb": 32768, "availableMb": 30000})
    with pytest.raises(JobError) as e:
        s.match("db", "exhaustive", 14)
    assert not isinstance(e.value, MemoryLimitError)


def test_vocab_tree_loop_closure_for_the_frames(tmp_path, monkeypatch):
    tree = tmp_path / "tree.bin"
    tree.write_bytes(b"x")
    s = _gpu_sfm(tmp_path, monkeypatch, colmap_vocab_tree_images=20, colmap_vocab_tree_path=str(tree))
    s.match("db", "pairs", 353, pairs=[("a", "b")], vocab_queries=["v/vf_0001.jpg", "v/vf_0002.jpg"])
    assert s.cm.calls[-1][:4] == ("vocab", 2, 20, str(tree)) and s.stats()["vocabTreeMatching"] == "2 frames x 20 images"
    missing = _gpu_sfm(tmp_path, monkeypatch, colmap_vocab_tree_images=20, colmap_vocab_tree_path="/nope.bin")
    missing.match("db", "pairs", 353, pairs=[("a", "b")], vocab_queries=["v/vf_0001.jpg"])
    assert not any(isinstance(c, tuple) for c in missing.cm.calls)
    assert missing.stats()["vocabTreeMatching"].startswith("off")


def test_vocab_tree_and_global_mapper_commands(tmp_path):
    cm = Colmap("colmap", "log", str(tmp_path), {**CAPS, "use_gpu": True}, V42_GPU)
    tier = tuning.gpu_matching_tiers(14900, B3, 32768, 4)[0]
    args = cm.vocab_tree_args("db", "/opt/tree.bin", "q.txt", 30, tier)
    f = flags(args)
    assert args[0] == "vocab_tree_matcher" and f["VocabTreeMatching.num_images"] == "30"
    assert f["VocabTreeMatching.match_list_path"] == "q.txt" and f["FeatureMatching.use_gpu"] == "1"
    g = cm.map_args("db", "img", "out", "global")
    assert g[0] == "global_mapper" and flags(g)["GlobalMapper.num_threads"] == "4"
    assert cm.map_args("db", "img", "out")[0] == "mapper"


def test_global_mapper_falls_back_when_colmap_has_none(tmp_path, monkeypatch):
    monkeypatch.setattr(sfm, "help_options", lambda *a: set())
    s = _gpu_sfm(tmp_path, monkeypatch, colmap_mapper="global")
    assert s.mapper == "incremental" and "needs COLMAP 4.x" in (tmp_path / "tools.log").read_text()
    monkeypatch.setattr(sfm, "help_options", lambda *a: {"GlobalMapper.num_threads"})
    assert _gpu_sfm(tmp_path, monkeypatch, colmap_mapper="global").mapper == "global"


def test_pair_list_has_every_photo_pair_plus_the_frame_pairs():
    photos = [f"g/p{i:02d}.jpg" for i in range(12)]
    frames = [f"v/vf_{i:04d}.jpg" for i in range(30)]
    pairs = set(build_pairs(photos, frames, neighbours=3, stride=5))
    every_photo_pair = {(a, b) for i, a in enumerate(photos) for b in photos[i + 1:]}
    assert every_photo_pair <= pairs  # exhaustive, not only neighbours
    assert ("v/vf_0000.jpg", "v/vf_0003.jpg") in pairs and ("v/vf_0000.jpg", "v/vf_0004.jpg") not in pairs


@pytest.mark.parametrize("n,every,held", [(48, 8, [0, 8, 16, 24, 32, 40]), (10, 0, []), (1, 8, []),
                                          (5, 1, [0, 1, 2, 3]), (7, 8, [0])])
def test_eval_split(n, every, held):
    train, test = eval_split(n, every)
    assert test == held and sorted(train + test) == list(range(n)) and train


def test_eval_line_and_trainer_option():
    p = GsplatParser()
    assert p("eval psnr 27.412 ssim 0.8631 views 6") is None
    assert p.eval == {"psnr": 27.412, "ssim": 0.8631, "views": 6}
    plan = GsplatPlan(PROFILES["draft"], 1600, 500_000, 3000, 1024)
    assert "--eval-every" not in args_for(plan)
    assert args_for(plan, 8)[-2:] == ["--eval-every", "8"]


def test_text_model_reports_the_mean_track_length(tmp_path):
    (tmp_path / "images.txt").write_text("# header\n1 1 0 0 0 0 0 0 1 a.jpg\n1 2 3\n2 1 0 0 0 1 0 0 1 b.jpg\n\n")
    (tmp_path / "points3D.txt").write_text("# header\n1 0 0 0 1 2 3 0.5 1 0 2 0\n2 0 0 0 1 2 3 1.5 1 1 2 1 3 4\n")
    m = read_text_model(str(tmp_path))
    assert m["points"] == 2 and m["meanReprojErrorPx"] == 1.0 and m["meanTrackLength"] == 2.5
    assert len(m["images"]) == 2
