"""Per-photo exposure / white-balance normalisation for the facet textures.

Every photo gets one multiplicative gain per colour channel (sRGB values; a gain there is a gain in
linear light too, just raised to the gamma). The gains come from where photos overlap on the facets,
at low resolution (one sample per label cell). The overlaps of ALL facets go into ONE solve, so a
photo that covers two facets ties them together and no facet drifts on its own. For each overlapping
pair the median log ratio per channel (robust to occluders and parallax), then a weighted
least-squares solve of
  x_i - x_j = median(log I_i - log I_j)
with every x pulled towards 0 (gain 1) in proportion to how much evidence the photo has. The photos
are shot at fixed exposure / ISO / white balance, so what differs between two views of the same spot
is mostly view-dependent (sheen, the angle to a lamp), not the camera: the pull keeps real lighting
differences from being over-corrected. Brightness (the mean over the channels) is pulled with
`gainPriorLuma`, colour (the per-channel rest) much harder with `gainPriorChroma`, so no photo is
tinted into an odd colour. No photo is the reference: the pull fixes the overall level at the photos'
own. Near-black and near-saturated samples are ignored.

The samples are sparse (per photo only the cells it sees, views.py) and only photos whose samples
overlap are paired. When all pairs together would compare more than `gainMaxPairCells` cells (hundreds
of photos on a big wall), every pair uses the same regular subset of cells instead: a median over a few
thousand cells per pair is as good as over all of them.
"""
import numpy as np

GAIN_DEFAULTS = {"gainMinOverlapCells": 40, "gainClamp": (0.67, 1.5), "gainDarkLevel": 12.0,
                 "gainBrightLevel": 243.0, "gainPriorLuma": 0.5, "gainPriorChroma": 4.0,
                 "gainMaxPairCells": 50_000_000}
PAIR_CAP = 2000  # overlap cells beyond which a pair counts no more


def log_samples(col):
    """(..., 3) low-res colours (NaN = not seen) -> log values, NaN where unusable (near black / saturated)."""
    ok = np.all((col > GAIN_DEFAULTS["gainDarkLevel"]) & (col < GAIN_DEFAULTS["gainBrightLevel"]), -1)
    with np.errstate(invalid="ignore", divide="ignore"):
        return np.log(np.where(ok[..., None], col, np.nan))


def _overlapping(blocks):
    """Per facet blocks [(c, y0, x0, lg (h, w, 3)), ...] -> {(i, j): [(block i, block j, y0, y1, x0, x1)]}
    for every pair of photos (i < j) whose crops overlap somewhere, and the summed overlap area."""
    found, area = {}, 0
    for fb in blocks:
        fb = sorted(fb, key=lambda b: b[0])
        y0 = np.array([b[1] for b in fb])
        x0 = np.array([b[2] for b in fb])
        y1 = y0 + np.array([b[3].shape[0] for b in fb])
        x1 = x0 + np.array([b[3].shape[1] for b in fb])
        for a in range(len(fb)):
            oy0, oy1 = np.maximum(y0[a], y0[a + 1:]), np.minimum(y1[a], y1[a + 1:])
            ox0, ox1 = np.maximum(x0[a], x0[a + 1:]), np.minimum(x1[a], x1[a + 1:])
            for k in np.nonzero((oy1 > oy0) & (ox1 > ox0))[0]:
                b = a + 1 + k
                found.setdefault((fb[a][0], fb[b][0]), []).append((fb[a], fb[b], oy0[k], oy1[k], ox0[k], ox1[k]))
                area += int((oy1[k] - oy0[k]) * (ox1[k] - ox0[k]))
    return found, area


def _crop(block, y0, y1, x0, x1, step):
    """The block's log samples over cells y0:y1, x0:x1, only every `step`-th cell row / column of the grid."""
    _, by, bx, lg = block
    return lg[y0 - by + (-y0) % step:y1 - by:step, x0 - bx + (-x0) % step:x1 - bx:step]


def pair_offsets(blocks, min_overlap, max_cells=GAIN_DEFAULTS["gainMaxPairCells"]):
    """Per facet the photos' log-sample blocks [(c, y0, x0, lg (h, w, 3)), ...] -> list of (i, j, median log
    ratio (3,), overlap count), i < j. Only photos whose crops overlap are compared. When the pairs'
    overlaps add up to more than `max_cells` cells, every pair uses the same regular subset of cells (each
    `step`-th row and column) and its count is scaled up accordingly."""
    found, area = _overlapping(blocks)
    step = max(1, int(np.ceil(np.sqrt(area / max_cells)))) if max_cells else 1
    pairs = []
    for (i, j), parts in sorted(found.items()):
        diffs = []
        for bi, bj, y0, y1, x0, x1 in parts:
            li, lj = _crop(bi, y0, y1, x0, x1, step), _crop(bj, y0, y1, x0, x1, step)
            m = ~np.isnan(li[..., 0]) & ~np.isnan(lj[..., 0])
            diffs.append(li[m] - lj[m])
        d = np.concatenate(diffs)
        n = len(d) * step * step
        if n < min_overlap or len(d) == 0:
            continue
        pairs.append((i, j, np.median(d, axis=0), n))
    return pairs


def _overlap(C, pairs):
    ov = np.zeros(C)
    for i, j, _, n in pairs:
        ov[i] += min(n, PAIR_CAP)
        ov[j] += min(n, PAIR_CAP)
    return ov


def _solve(C, pairs, b, prior, ov):
    """min sum_pairs w (x_i - x_j - b)^2 + sum_i prior * ov_i * x_i^2  (b: (P, k)) -> x (C, k)."""
    A = np.zeros((len(pairs) + C, C))
    rhs = np.zeros((len(pairs) + C, b.shape[1]))
    for r, (i, j, _, n) in enumerate(pairs):
        w = np.sqrt(min(n, PAIR_CAP))
        A[r, i], A[r, j], rhs[r] = w, -w, w * b[r]
    # a photo without overlaps keeps gain 1; the tiny floor only fixes the gauge when prior = 0
    A[len(pairs):, :] = np.diag(np.sqrt(np.maximum(prior * ov, 1e-6 * max(ov.max(), 1.0))))
    return np.linalg.lstsq(A, rhs, rcond=None)[0]


def solve_gains(C, pairs, prior_luma=GAIN_DEFAULTS["gainPriorLuma"],
                prior_chroma=GAIN_DEFAULTS["gainPriorChroma"], clamp=GAIN_DEFAULTS["gainClamp"]):
    """Regularised least squares for per-photo log offsets -> (gains (C, 3), best-connected photo).
    Brightness and colour are solved separately, each with its own pull towards gain 1."""
    if not pairs:
        return np.ones((C, 3)), None
    ov = _overlap(C, pairs)
    d = np.array([p[2] for p in pairs])  # (P, 3)
    luma = d.mean(1, keepdims=True)
    x = _solve(C, pairs, luma, prior_luma, ov) + _solve(C, pairs, d - luma, prior_chroma, ov)
    return np.clip(np.exp(-x), *clamp), int(ov.argmax())


def fit_gains(cells, min_overlap=GAIN_DEFAULTS["gainMinOverlapCells"],
              prior_luma=GAIN_DEFAULTS["gainPriorLuma"], prior_chroma=GAIN_DEFAULTS["gainPriorChroma"]):
    """cells: per facet an array (C, ch, cw, 3) of low-res colours (NaN where unseen / not usable);
    all facets are solved together. Returns (gains (C, 3), best-connected photo index, pair count)."""
    blocks = [[(c, 0, 0, log_samples(fc[c])) for c in range(len(fc))] for fc in cells]
    return fit_gains_blocks(blocks, cells[0].shape[0], min_overlap, prior_luma, prior_chroma)


def fit_gains_blocks(blocks, C, min_overlap=GAIN_DEFAULTS["gainMinOverlapCells"],
                     prior_luma=GAIN_DEFAULTS["gainPriorLuma"], prior_chroma=GAIN_DEFAULTS["gainPriorChroma"],
                     max_cells=GAIN_DEFAULTS["gainMaxPairCells"]):
    """fit_gains on sparse samples: per facet [(photo index, cell row, cell col, log samples (h, w, 3)
    from log_samples), ...], C photos in all."""
    pairs = pair_offsets(blocks, min_overlap, max_cells)
    gains, ref = solve_gains(C, pairs, prior_luma, prior_chroma)
    return gains, ref, len(pairs)


def gain_luts(gains):
    """(C, 3) gains -> (C, 3, 256) uint8 lookup tables."""
    g = np.asarray(gains, np.float64)
    v = np.arange(256, dtype=np.float64)
    return np.clip(v[None, None, :] * g[..., None] + 0.5, 0, 255).astype(np.uint8)


def apply_luts(rgb, cam, lut):
    """rgb (..., 3) uint8 drawn from photo `cam` (...) int (-1 = empty) -> gain-corrected uint8."""
    c = np.maximum(cam, 0).astype(np.intp)[..., None]
    return lut[c, np.arange(3), rgb]
