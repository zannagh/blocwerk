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
"""
import numpy as np

GAIN_DEFAULTS = {"gainMinOverlapCells": 40, "gainClamp": (0.67, 1.5), "gainDarkLevel": 12.0,
                 "gainBrightLevel": 243.0, "gainPriorLuma": 0.5, "gainPriorChroma": 4.0}
PAIR_CAP = 2000  # overlap cells beyond which a pair counts no more


def _log_samples(cells):
    """cells: list of (C, n, 3) float arrays (NaN = not seen) -> (C, N, 3) log values, NaN if unusable."""
    col = np.concatenate([c.reshape(c.shape[0], -1, 3) for c in cells], axis=1)
    ok = np.all((col > GAIN_DEFAULTS["gainDarkLevel"]) & (col < GAIN_DEFAULTS["gainBrightLevel"]), -1)
    with np.errstate(invalid="ignore", divide="ignore"):
        lg = np.log(np.where(ok[..., None], col, np.nan))
    return lg


def pair_offsets(lg, min_overlap):
    """(C, N, 3) log samples -> list of (i, j, median log ratio (3,), overlap count)."""
    V = ~np.isnan(lg[..., 0])
    counts = V.astype(np.float32) @ V.T.astype(np.float32)
    pairs = []
    C = lg.shape[0]
    for i in range(C):
        for j in range(i + 1, C):
            if counts[i, j] < min_overlap:
                continue
            m = V[i] & V[j]
            pairs.append((i, j, np.median(lg[i, m] - lg[j, m], axis=0), int(m.sum())))
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
    lg = _log_samples(cells)
    pairs = pair_offsets(lg, min_overlap)
    gains, ref = solve_gains(lg.shape[0], pairs, prior_luma, prior_chroma)
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
