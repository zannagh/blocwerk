"""Sparse per-facet photo views on the label-cell grid: memory follows what each photo really sees.

The texture renderer scores every photo on every facet's label cells (textures._score) and later keeps
a blend weight, a low-resolution colour and an exposure sample per photo and cell. Held densely that is
photos x cells per array: 400 photos on a 150 m2 wall would be several GB, although each photo sees
only a small part of the wall. Here a photo keeps only the bounding box of the cells it sees on a facet
(plus `PAD_CELLS`, which blend.FacetAccumulator.region reads around its weights); a photo that does not
see the facet keeps nothing.

The per-cell steps that compare photos (top-N weights, the consensus median, the label mode filter)
run in row tiles: `dense` rebuilds just those rows as a (photos reaching them, rows, cols) array, i.e.
exactly that slice of the old dense array without the photos that have nothing there. A step that
looks at neighbouring cells gets `halo` extra rows and only its inner rows are kept, so every step
computes the same numbers as on the whole dense array.
"""
import cv2
import numpy as np

PAD_CELLS = 1
TILE_CELLS = 1 << 18  # photos x cells of one dense row tile (x channels), ~1 MB per float32 array
# bytes per photo and cell of a view crop at its peak: score float64, blend weight float32, low-res
# colour and its log (exposure fit) 3 x float32 each; the penalised score (float64) replaces the logs
BYTES_PER_CELL = 8 + 4 + 12 + 12


def cell_labels(S, k):
    """Per label cell: the chosen photo index into S (C, ch, cw) (-1 = none), mode-filtered over k x k."""
    valid = S > 0
    lab = np.where(valid.any(0), S.argmax(0), -1)
    if k > 1 and len(S) > 1:
        votes = np.stack([cv2.boxFilter((lab == c).astype(np.float32), -1, (k, k), normalize=False,
                                        borderType=cv2.BORDER_REPLICATE) for c in range(len(S))])
        # tie-break by quality so the filter never picks a poor photo over an equally common one
        smax = S.max(0, keepdims=True)
        votes = votes + 0.01 * S / np.where(smax > 0, smax, 1)
        votes[~valid] = -1
        lab = np.where(valid.any(0), votes.argmax(0), -1)
    return lab


class View:
    """Photo index `c` on one facet: cell rows y0:y1, cols x0:x1 and named arrays over that crop."""

    __slots__ = ("c", "y0", "y1", "x0", "x1", "fields")

    def __init__(self, c, y0, y1, x0, x1, S):
        self.c, self.y0, self.y1, self.x0, self.x1 = c, y0, y1, x0, x1
        self.fields = {"S": S}


class FacetViews:
    """Every photo's view of one facet's (ch, cw) label-cell grid, ordered by photo index."""

    def __init__(self, shape):
        self.shape = tuple(shape)
        self.views = []
        self.by_photo = {}

    def add(self, c, s):
        """Keep photo c's score grid `s` (ch, cw) cropped to where it is > 0 (nothing if nowhere)."""
        ys, xs = np.nonzero(s > 0)
        if ys.size == 0:
            return
        ch, cw = self.shape
        y0, y1 = max(ys.min() - PAD_CELLS, 0), min(ys.max() + 1 + PAD_CELLS, ch)
        x0, x1 = max(xs.min() - PAD_CELLS, 0), min(xs.max() + 1 + PAD_CELLS, cw)
        v = View(c, int(y0), int(y1), int(x0), int(x1), s[y0:y1, x0:x1].copy())
        self.views.append(v)
        self.by_photo[c] = v

    def cells(self):
        """Photo-cells held (sum of the crop areas)."""
        return sum((v.y1 - v.y0) * (v.x1 - v.x0) for v in self.views)

    def drop(self, key):
        for v in self.views:
            v.fields.pop(key, None)

    def tiles(self, halo=0, channels=1):
        """(r0, r1, a0, a1): output rows r0:r1 and the rows a0:a1 (with `halo`) a step reads for them.
        About TILE_CELLS photo-cells per tile (from the average number of crops per row), and at least
        2 * halo output rows so the halo never more than doubles the work."""
        ch, cw = self.shape
        per_row = sum(v.y1 - v.y0 for v in self.views) / max(ch, 1)
        rows = max(1, 2 * halo, int(TILE_CELLS // max(1.0, per_row * cw * channels)))
        for r0 in range(0, ch, rows):
            r1 = min(r0 + rows, ch)
            yield r0, r1, max(r0 - halo, 0), min(r1 + halo, ch)

    def dense(self, key, a0, a1, fill):
        """(photo indices (n,), array (n, a1 - a0, cw, ...)) of field `key` over rows a0:a1 for the n
        photos whose crop reaches them; `fill` outside a crop. n may be 0."""
        vs = [v for v in self.views if v.y0 < a1 and v.y1 > a0]
        idx = np.array([v.c for v in vs], np.intp)
        if not vs:
            return idx, None
        ref = vs[0].fields[key]
        out = np.full((len(vs), a1 - a0, self.shape[1]) + ref.shape[2:], fill, ref.dtype)
        for k, v in enumerate(vs):
            y0, y1 = max(v.y0, a0), min(v.y1, a1)
            out[k, y0 - a0:y1 - a0, v.x0:v.x1] = v.fields[key][y0 - v.y0:y1 - v.y0]
        return idx, out

    def put(self, key, idx, rows, r0):
        """Write `rows` (n, r1 - r0, cw, ...) of photos `idx` into each one's crop field `key`."""
        r1 = r0 + rows.shape[1]
        for k, c in enumerate(idx):
            v = self.by_photo[int(c)]
            if key not in v.fields:
                v.fields[key] = np.zeros((v.y1 - v.y0, v.x1 - v.x0) + rows.shape[3:], rows.dtype)
            y0, y1 = max(v.y0, r0), min(v.y1, r1)
            if y1 > y0:
                v.fields[key][y0 - v.y0:y1 - v.y0] = rows[k, y0 - r0:y1 - r0, v.x0:v.x1]

    def labels(self, key, k, mask_key=None):
        """cell_labels of field `key` (mode filter k x k cells) -> photo index per cell (ch, cw), -1 = none.
        With `mask_key` a photo only counts where that field is > 0."""
        out = np.full(self.shape, -1, np.intp)
        for r0, r1, a0, a1 in self.tiles(k // 2):
            idx, S = self.dense(key, a0, a1, 0.0)
            if S is None:
                continue
            if mask_key is not None:
                S = np.where(self.dense(mask_key, a0, a1, 0.0)[1] > 0, S, 0)
            lab = cell_labels(S, k)[r0 - a0:r1 - a0]
            out[r0:r1] = np.where(lab >= 0, idx[np.maximum(lab, 0)], -1)
        return out

    def full(self, key, C, fill=0.0):
        """The old dense (C, ch, cw, ...) array of field `key` (tests and small grids only)."""
        idx, rows = self.dense(key, 0, self.shape[0], fill)
        if rows is None:
            return np.full((C,) + self.shape, fill)
        out = np.full((C,) + rows.shape[1:], fill, rows.dtype)
        out[idx] = rows
        return out
