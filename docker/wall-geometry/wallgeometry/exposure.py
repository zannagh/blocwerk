"""Per-photo exposure / white-balance normalisation for the facet textures.

Every photo gets one multiplicative gain per colour channel (sRGB values; a gain there is a gain in
linear light too, just raised to the gamma). The gains come from where photos overlap on the facets,
at low resolution (one sample per label cell): for each overlapping pair the median log ratio per
channel (robust to occluders and parallax), then a weighted least-squares solve of
  x_i - x_j = median(log I_i - log I_j)
anchored at the photo with the most overlap (x_ref = 0), with a weak pull of every x towards 0 so a
photo that overlaps nothing keeps gain 1. Near-black and near-saturated samples are ignored.
"""
import numpy as np

GAIN_DEFAULTS = {"gainMinOverlapCells": 40, "gainClamp": (0.5, 2.0), "gainDarkLevel": 12.0,
                 "gainBrightLevel": 243.0, "gainRegularisation": 0.02}


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


def solve_gains(C, pairs, ref=None, reg=GAIN_DEFAULTS["gainRegularisation"], clamp=GAIN_DEFAULTS["gainClamp"]):
    """Least squares for per-photo log offsets -> (gains (C, 3), reference index)."""
    if not pairs:
        return np.ones((C, 3)), ref
    if ref is None:
        ov = np.zeros(C)
        for i, j, _, n in pairs:
            ov[i] += n
            ov[j] += n
        ref = int(ov.argmax())
    rows = len(pairs) + C + 1
    A = np.zeros((rows, C))
    b = np.zeros((rows, 3))
    for r, (i, j, d, n) in enumerate(pairs):
        w = np.sqrt(min(n, 2000))
        A[r, i], A[r, j], b[r] = w, -w, w * d
    for i in range(C):
        A[len(pairs) + i, i] = reg
    A[-1, ref] = 1e3
    x = np.linalg.lstsq(A, b, rcond=None)[0]
    x -= x[ref]
    return np.clip(np.exp(-x), *clamp), ref


def fit_gains(cells, min_overlap=GAIN_DEFAULTS["gainMinOverlapCells"]):
    """cells: per facet an array (C, ch, cw, 3) of low-res colours (NaN where unseen / not usable).
    Returns (gains (C, 3), reference photo index, number of overlapping pairs)."""
    lg = _log_samples(cells)
    pairs = pair_offsets(lg, min_overlap)
    gains, ref = solve_gains(lg.shape[0], pairs)
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
