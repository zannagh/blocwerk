"""Floater clean-up (splatworker/cleanup.py): one synthetic scene, one test per rule."""
import numpy as np
import pytest

from splatworker.cleanup import (ABOVE_TOP, BEHIND_WALL, KEEP, NEEDLE, SEEN_THROUGH, SPARSE, CleanupParams,
                                 classify, longest_axis)
from splatworker.cleanup_run import clean_job, clean_spz
from splatworker.options import OptionsError, parse_options
from splatworker.splatio import Splats, read_spz, spz_bytes, spz_subset

# world mm: x right, y into the wall, z up. A vertical wall (front = -y), an overhang folding out of
# it, and a side wall on the right (yawed 90 deg: the room goes on behind it).
FACETS = [
    {"id": "0", "origin": [0, 0, 0], "u": [1, 0, 0], "v": [0, 0, 1], "normal": [0, -1, 0],
     "extentMm": {"aMin": 0, "aMax": 3000, "bMin": 0, "bMax": 2000}},
    {"id": "1", "origin": [0, 0, 2000], "u": [1, 0, 0], "v": [0, -0.7071, 0.7071], "normal": [0, -0.7071, -0.7071],
     "extentMm": {"aMin": 0, "aMax": 3000, "bMin": 0, "bMax": 1000}},
    {"id": "2", "origin": [3000, 0, 0], "u": [0, -1, 0], "v": [0, 0, 1], "normal": [-1, 0, 0],
     "extentMm": {"aMin": 0, "aMax": 1500, "bMin": 0, "bMax": 2000}},
]
TOP_MARKER_Z = 2650.0
DOC = {"world": {"up": [0, 0, 1]}, "segments": [{"facets": FACETS}],
       "markers": [{"cornersWorldMm": [[1400, -600, TOP_MARKER_Z], [1500, -600, TOP_MARKER_Z - 100]]}]}
CAMERAS = np.array([[x, -3000.0, z] for x in (300, 900, 1500, 2100, 2700) for z in (600, 1200, 1800)])


class Scene:
    """Splat attributes built up group by group; `ids[name]` = the indices of a group."""

    def __init__(self):
        self.parts, self.ids, self.n = [], {}, 0

    def add(self, name, pts, size=6.0, alpha=0.8, long_axis=None):
        pts = np.atleast_2d(np.asarray(pts, float))
        k = len(pts)
        scale = np.full((k, 3), float(size))
        axis = np.tile(np.asarray(long_axis if long_axis is not None else [1, 0, 0], float), (k, 1))
        self.parts.append((pts, scale, np.full(k, alpha), axis))
        self.ids[name] = np.arange(self.n, self.n + k)
        self.n += k
        return self

    def needle(self, name, pts, length, axis, alpha=0.6):
        self.add(name, pts, alpha=alpha, long_axis=axis)
        self.parts[-1][1][:, 0] = length
        return self

    def arrays(self):
        return [np.concatenate([p[i] for p in self.parts]) for i in range(4)]

    def classify(self, cameras=CAMERAS, params=None):
        w, s, a, ax = self.arrays()
        return classify(w, s, a, DOC, cameras, params, ax)


def plane(rng, f, n, lift=0.0, noise=3.0):
    o, u, v, nn = (np.array(f[k], float) for k in ("origin", "u", "v", "normal"))
    e = f["extentMm"]
    a, b = rng.uniform(e["aMin"], e["aMax"], n), rng.uniform(e["bMin"], e["bMax"], n)
    return o + a[:, None] * u + b[:, None] * v + (lift + rng.normal(0, noise, n))[:, None] * nn


def blob(rng, centre, n=400, spread=40.0):
    return np.asarray(centre, float) + rng.normal(0, spread, (n, 3))


def base_scene(rng):
    s = Scene()
    for f in FACETS:
        s.add(f"wall{f['id']}", plane(rng, f, 12000))
    s.add("holds", plane(rng, FACETS[0], 300, lift=90.0, noise=20.0), size=10)
    floor = np.c_[rng.uniform(-500, 3500, 3000), rng.uniform(-2500, 0, 3000), rng.normal(0, 5, 3000)]
    s.add("floor", floor, size=15)                   # floor plane = kickboard bottom = z 0
    s.add("mats", floor[:600] + [0, 0, 250], size=15, alpha=0.3)
    return s


def verdicts(scene, **kw):
    v, report = scene.classify(**kw)
    return (lambda name: v[scene.ids[name]]), report


def test_wall_top_cuts_over_the_wall_but_not_beyond_its_side_edges():
    rng = np.random.default_rng(1)
    s = base_scene(rng)
    s.add("over", blob(rng, [1500, -900, TOP_MARKER_Z + 400], 50, 30))
    s.add("room-left", blob(rng, [-600, -900, TOP_MARKER_Z + 400], 500, 30))   # beyond the left edge
    s.add("just-below-top", blob(rng, [1500, -900, TOP_MARKER_Z + 30], 500, 15))
    of, report = verdicts(s, cameras=None)
    assert (of("over") == ABOVE_TOP).all()
    assert (of("room-left") == KEEP).mean() > 0.95
    assert not (of("just-below-top") == ABOVE_TOP).any()
    assert report["wallTopMm"] == pytest.approx(TOP_MARKER_Z + CleanupParams().top_margin_mm)
    assert report["removed"]["aboveWallTop"] == 50


def test_behind_a_front_facet_is_removed_behind_a_side_facet_is_kept():
    rng = np.random.default_rng(2)
    s = base_scene(rng)
    s.add("behind-main", blob(rng, [1500, 400, 1000], 300, 60))           # inside facet 0's outline
    s.add("plywood", plane(rng, FACETS[0], 300, lift=-30.0, noise=5))     # within the tolerance
    s.add("behind-side", blob(rng, [3500, -700, 1000], 600, 40))         # the room past the side wall
    s.add("below-floor", blob(rng, [1500, -1000, -400], 200, 20))
    of, _ = verdicts(s, cameras=None)
    assert (of("behind-main") == BEHIND_WALL).mean() > 0.97
    assert (of("plywood") == KEEP).all()
    assert (of("behind-side") == KEEP).mean() > 0.95
    assert (of("below-floor") == BEHIND_WALL).all()


def test_surfaces_are_kept_and_sparse_air_is_removed():
    rng = np.random.default_rng(3)
    s = base_scene(rng)
    s.add("dust", np.c_[rng.uniform(300, 2700, 60), rng.uniform(-2000, -500, 60), rng.uniform(700, 1700, 60)],
          size=30, alpha=0.1)
    of, report = verdicts(s, cameras=None)
    for name in ("wall0", "wall1", "wall2", "holds", "floor", "mats"):
        assert (of(name) == KEEP).mean() > 0.995, name
    assert (of("dust") == SPARSE).mean() > 0.95
    assert report["removed"]["sparse"] >= 57


def test_a_dense_cluster_the_cameras_look_through_is_carved():
    rng = np.random.default_rng(4)
    s = base_scene(rng)
    s.add("ghost", blob(rng, [1500, -1500, 1200], 400, 25), size=8)   # between the cameras and the wall
    s.add("room-side", blob(rng, [3600, -900, 1000], 400, 25), size=8)  # dense, but nobody looks through it
    # a window in the room's side wall, past the side facet's outline: faint glass the cameras look
    # through at the view outside; the side wall's slab keeps it
    s.add("glass", np.c_[np.full(200, 3150.0), rng.uniform(-2400, -1700, 200), rng.uniform(800, 1400, 200)],
          size=20, alpha=0.05)
    s.add("outside", blob(rng, [3900, -2000, 1100], 300, 60), size=8)
    of, report = verdicts(s)
    assert (of("ghost") == SEEN_THROUGH).mean() > 0.9
    assert (of("room-side") == KEEP).mean() > 0.95
    assert (of("glass") == KEEP).all()
    assert (of("wall0") == KEEP).mean() > 0.995 and (of("holds") == KEEP).all()
    without, _ = verdicts(s, cameras=None)
    assert (without("ghost") == KEEP).mean() > 0.9   # dense enough to survive the sparse rule alone
    assert report["carvedCells"] > 0


def test_needles_off_the_surfaces_and_hairs_on_them():
    rng = np.random.default_rng(5)
    s = base_scene(rng)
    s.add("ghost", blob(rng, [1500, -1500, 1200], 400, 25), size=8)
    s.needle("line", [[1500, -1500, 1200]], 250.0, [0, 0, 1])
    s.needle("hair", [[1500, -20, 1000]], 300.0, [0, -1, 0])     # rooted on the wall, pointing out
    s.needle("groove", [[1500, -5, 800]], 120.0, [1, 0, 0])      # long but lying in the plane
    of, _ = verdicts(s, cameras=None)
    assert of("line")[0] == NEEDLE
    assert of("hair")[0] == NEEDLE
    assert of("groove")[0] == KEEP


def test_longest_axis_follows_the_quaternion():
    q = np.array([[1, 0, 0, 0], [np.cos(np.pi / 4), 0, 0, np.sin(np.pi / 4)]])   # identity, 90 deg about z
    ax = longest_axis(q, np.array([[5.0, 1, 1], [5.0, 1, 1]]), np.eye(3))
    assert np.allclose(ax[0], [1, 0, 0]) and np.allclose(np.abs(ax[1]), [0, 1, 0], atol=1e-9)


def _splats(world, size=6.0):
    n = len(world)
    return Splats(world.copy(), np.full((n, 3), np.log(size)), np.zeros((n, 3)), np.full(n, 3.0),
                  np.tile([1.0, 0, 0, 0], (n, 1)))


def test_clean_job_reports_and_never_raises():
    rng = np.random.default_rng(6)
    w, _, _, _ = base_scene(rng).add("dust", blob(rng, [1500, -1500, 1200], 30, 400)).arrays()
    frame = {"aligned": True, "toWorldMm": np.eye(4).tolist()}
    keep, block = clean_job(_splats(w), frame, DOC, CAMERAS)
    assert block["applied"] and block["rawFile"] == "wall.raw.spz" and block["kept"] == keep.sum() < len(w)
    keep, block = clean_job(_splats(w), {"aligned": False}, DOC, CAMERAS)
    assert keep.all() and not block["applied"]
    keep, block = clean_job(_splats(w), frame, {"segments": []}, CAMERAS)
    assert keep.all() and not block["applied"] and "facets" in block["reason"]


def test_spz_subset_is_lossless_and_clean_spz_filters():
    rng = np.random.default_rng(7)
    w, _, _, _ = base_scene(rng).add("dust", blob(rng, [1500, -1500, 1200], 40, 400)).arrays()
    data = spz_bytes(_splats(w / 1000.0, 0.006), fractional_bits=12)     # metres in the file, like a real scene
    keep = rng.random(len(w)) < 0.5
    full, sub = read_spz(data), read_spz(spz_subset(data, keep))
    for k in ("xyz", "alpha", "colour", "log_scale", "quat_xyz"):
        assert np.array_equal(full[k][keep], sub[k])
    frame = {"aligned": True, "toWorldMm": (np.diag([1000.0, 1000, 1000, 1])).tolist()}
    out, report = clean_spz(data, frame, DOC)
    assert len(read_spz(out)["alpha"]) == report["kept"] < len(w)
    assert report["cameras"] == 0 and report["removed"]["sparse"] > 0


def test_option_is_on_by_default_and_boolean():
    assert parse_options(None).cleanup is True
    assert parse_options({"cleanup": False}).cleanup is False
    with pytest.raises(OptionsError):
        parse_options({"cleanup": "yes"})


@pytest.mark.parametrize("enabled", [True, False])
def test_pipeline_cleans_after_the_crop_and_keeps_the_raw_scene(tmp_path, monkeypatch, enabled):
    import json

    from splatworker import pipeline
    from splatworker.options import SplatOptions

    rng = np.random.default_rng(8)
    w, _, _, _ = base_scene(rng).add("dust", blob(rng, [1500, -1500, 1200], 40, 400)).arrays()
    stems = [f"p{i:02d}" for i in range(len(CAMERAS))]
    (tmp_path / "inputs.json").write_text(json.dumps({
        "photos": {s: {} for s in stems}, "options": {**SplatOptions().to_dict(), "cleanup": enabled}}))
    (tmp_path / "geometry.json").write_text(json.dumps(DOC))
    r = pipeline.Run(str(tmp_path), lambda *a: None)
    r.model = {"images": {f"g/{s}.jpg": c for s, c in zip(stems, CAMERAS)}}
    frame = {"aligned": True, "toWorldMm": np.eye(4).tolist(), "toViewer": np.eye(4).tolist(), "crop": None}
    monkeypatch.setattr(pipeline, "read_ply", lambda p: None)
    monkeypatch.setattr(pipeline.Splats, "from_ply", classmethod(lambda cls, c: _splats(w)))
    monkeypatch.setattr(pipeline, "align", lambda *a: dict(frame))
    monkeypatch.setattr(pipeline, "refine_frame", lambda f, s, d: f)

    trained, kept, raw, out = r.frame_and_crop("x.ply")
    if enabled:
        assert out["cleanup"]["applied"] and out["cleanup"]["cameras"] == len(CAMERAS)
        assert len(raw) == len(trained) and len(kept) == out["cleanup"]["kept"] < len(raw)
    else:
        assert raw is None and "cleanup" not in out and len(kept) == len(trained)
