"""Seam smoothness of the select-mode texture: no staircase lines, no hard colour steps between photos."""
import cv2
import numpy as np
import photowall

from wallgeometry import edges, seamblend, sourcemap
from wallgeometry import textures as tx

P = {"seamBlendPx": 8, "seamBandPx": 12, "seamWidePx": 48}


def _slots(shift):
    """Two photos A | B over a 64 x 96 tile; B is `shift` grey levels brighter. The slot a photo occupies
    flips halfway, as in the real accumulator."""
    h, w = 64, 96
    cam = np.zeros((2, h, w), np.int16)
    cam[0, :, :w // 2], cam[1, :, :w // 2] = 0, 1
    cam[0, :, w // 2:], cam[1, :, w // 2:] = 1, 0
    rgb = np.where(cam[..., None] == 1, 100 + shift, 100).astype(np.uint8) * np.ones((1, 1, 1, 3), np.uint8)
    wt = np.ones((2, h, w), np.float32)
    label = np.zeros((h, w), np.int16)
    label[:, w // 2:] = 1
    return rgb, wt, cam, label


def test_slot_order_does_not_break_the_blend():
    rgb, wt, cam, label = _slots(0)
    img, w = seamblend.combine(rgb, wt, cam, label, P, slice(0, 64))
    assert np.allclose(img, 100, atol=0.01) and np.allclose(w.sum(0), 1)


def test_a_colour_step_between_photos_is_spread_over_a_wide_zone():
    rgb, wt, cam, label = _slots(10)
    img, _ = seamblend.combine(rgb, wt, cam, label, P, slice(0, 64))
    row = img[32, :, 0]
    assert np.abs(np.diff(row)).max() < 1.0, np.abs(np.diff(row)).max()  # a hard seam jumps by 10 at once
    assert row[2] < 103 and row[-3] > 107  # far from the seam each photo keeps (about) its own level


def test_an_occluder_only_one_photo_sees_does_not_tint_the_wall():
    rgb, wt, cam, label = _slots(0)
    rgb[cam == 1] = 20  # photo 1 is 80 levels darker: only MAX_LOW_STEP of it may leak into the wide blend
    img, _ = seamblend.combine(rgb, wt, cam, label, P, slice(0, 64))
    assert img[32, 10, 0] > 100 - seamblend.MAX_LOW_STEP - 1


def test_the_halo_makes_tiling_invisible():
    rgb, wt, cam, label = _slots(10)
    whole, _ = seamblend.combine(rgb, wt, cam, label, P, slice(0, 64))
    tile, _ = seamblend.combine(rgb[:, :48], wt[:, :48], cam[:, :48], label[:48], P, slice(0, 32))
    # rows 32:48 are the tile's halo (here shorter than the full 144 rows, so near-equal only)
    assert np.abs(tile - whole[:32]).max() < 0.5


def test_coverage_edge_staircase_is_filled_with_neighbour_colour():
    filled = np.zeros((96, 96), bool)
    for i in range(6):  # a staircase with 16 px steps
        filled[i * 16:, 8 + i * 16:] = True
    img = np.full((96, 96, 3), 150, np.uint8)
    img[~filled] = 0
    img2, f2 = edges.smooth_coverage(img.copy(), filled, 16.0)
    grown = f2 & ~filled
    assert grown.sum() > 0 and (f2 | filled).sum() == f2.sum()  # only grows
    assert (img2[grown] > 100).all()  # notches take the colour of their covered neighbours, not black


def test_a_big_job_blends_with_fewer_views_instead_of_falling_back_to_one_photo():
    doc, photos = photowall.scene(12)
    p = tx.scale.at_resolution({**tx.DEFAULTS, "mmPerPx": 8.0}, {"mmPerPx": 8.0})
    cams = {c["image"]: tx._cam(c) for c in doc["cameras"]}
    facets = list(tx._facets(doc))
    jobs = tx._score_facets(facets, cams, sorted(cams), p, tx.occluders(facets, doc))
    need = {n: tx.blend_bytes(jobs, {**p, "blendViews": n}) for n in (2, 3, 6)}
    assert need[2] < need[3] < need[6]
    fits = tx.fit_blend_views(jobs, {**p, "blendMaxBytes": need[3]})
    assert fits["blendViews"] == 3
    assert tx.fit_blend_views(jobs, {**p, "blendMaxBytes": need[6]})["blendViews"] == 6
    assert tx.fit_blend_views(jobs, {**p, "blendMaxBytes": 1})["blendViews"] == 1


def _shaded_wall():
    doc, photos = photowall.scene(30)
    rng = np.random.default_rng(5)
    for k in sorted(photos):  # a smooth illumination ramp per photo that the global gain cannot fix
        im = photos[k].astype(np.float32)
        h, w = im.shape[:2]
        yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
        a, b = rng.uniform(-0.12, 0.12, 2)
        ramp = (1 + a * (xx / w - 0.5) * 2 + b * (yy / h - 0.5) * 2)[..., None]
        photos[k] = np.clip(im * ramp, 0, 255).astype(np.uint8)
    return doc, photos


def test_rendered_seams_have_no_line_artifacts():
    """High-frequency energy along the photo boundaries stays at the interior's level (the per-slot
    feather of the old code left thin staircase lines there: ratio 1.25 / 1.6 on the two facets)."""
    doc, photos = _shaded_wall()
    for res in tx.render_textures(doc, photos.__getitem__, set(photos), {"mmPerPx": 2.0}):
        img, mask = res["image"], res["mask"]
        cells = sourcemap.decode(res["source"])
        lab = cv2.resize(cells.astype(np.int16), (cells.shape[1] * 8, cells.shape[0] * 8), interpolation=cv2.INTER_NEAREST)
        lab = lab[:img.shape[0], :img.shape[1]]
        bd = np.zeros(lab.shape, bool)
        bd[:, 1:] |= lab[:, 1:] != lab[:, :-1]
        bd[1:, :] |= lab[1:, :] != lab[:-1, :]
        bd &= mask > 250
        near = cv2.dilate(bd.astype(np.uint8), np.ones((5, 5), np.uint8)) > 0
        far = (mask > 250) & ~(cv2.dilate(bd.astype(np.uint8), np.ones((41, 41), np.uint8)) > 0)
        g = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY).astype(np.float32)
        hf = np.abs(g - cv2.GaussianBlur(g, (0, 0), 2))
        assert hf[near].mean() / hf[far].mean() < 1.2, res["facet"]
