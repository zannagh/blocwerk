"""Voxel helpers of the splat clean-up (cleanup.py): an occupancy grid of surface evidence and a
free-space carving along the camera rays. numpy only (the worker image has no scipy)."""
import numpy as np


class Grid:
    """A dense boolean/count grid over the box [lo, hi] (mm) with cubic cells of `cell` mm."""

    def __init__(self, lo, hi, cell):
        self.lo, self.cell = np.asarray(lo, float), float(cell)
        self.shape = tuple(int(v) for v in np.maximum(np.ceil((np.asarray(hi, float) - self.lo) / cell), 1))

    def index(self, pts):
        """(ijk int array, inside mask) of the cells holding `pts`."""
        ijk = np.floor((pts - self.lo) / self.cell).astype(np.int64)
        ok = np.all((ijk >= 0) & (ijk < np.array(self.shape)), axis=1)
        return ijk, ok

    def counts(self, pts, weights=None):
        """Per-cell sum of `weights` (1 each by default) of the points inside the grid."""
        ijk, ok = self.index(pts)
        flat = np.ravel_multi_index(ijk[ok].T, self.shape)
        w = None if weights is None else np.asarray(weights, float)[ok]
        return np.bincount(flat, weights=w, minlength=int(np.prod(self.shape))).reshape(self.shape)

    def lookup(self, grid, pts, outside=False):
        """grid value at each point's cell (`outside` for points beyond the grid)."""
        ijk, ok = self.index(pts)
        out = np.full(len(pts), outside, dtype=grid.dtype)
        out[ok] = grid[tuple(ijk[ok].T)]
        return out

    def centres(self, mask):
        return (np.argwhere(mask) + 0.5) * self.cell + self.lo


def dilate(mask, steps):
    """Binary dilation by `steps` cells with the 6-neighbourhood, applied `steps` times (a diamond)."""
    out = mask.copy()
    for _ in range(int(steps)):
        grown = out.copy()
        for axis in range(3):
            grown[(slice(None),) * axis + (slice(1, None),)] |= out[(slice(None),) * axis + (slice(None, -1),)]
            grown[(slice(None),) * axis + (slice(None, -1),)] |= out[(slice(None),) * axis + (slice(1, None),)]
        out = grown
    return out


def carve(grid, cameras, targets, stop_mm, step_mm=None, chunk=4096):
    """How many cameras see THROUGH each cell: the segment from every camera centre to every target
    point (surface evidence it looked at), ending `stop_mm` short of the target, is free space.

    Returns an int grid of distinct-camera counts. Visibility is not tested (a camera behind a wall
    also "sees" through it), so the caller only carves cells that are not surface and not protected.
    """
    step = step_mm or grid.cell * 0.5
    seen = np.zeros(grid.shape, np.int32)
    n_cells = int(np.prod(grid.shape))
    for cam in np.asarray(cameras, float):
        hit = np.zeros(n_cells, bool)
        for s in range(0, len(targets), chunk):
            t = targets[s:s + chunk]
            ray = t - cam
            length = np.linalg.norm(ray, axis=1)
            usable = length > stop_mm + step
            ray, length = ray[usable] / length[usable, None], length[usable] - stop_mm
            if not len(ray):
                continue
            n_steps = int(np.ceil(length.max() / step))
            for k in range(n_steps):
                d = k * step
                live = d < length
                if not live.any():
                    break
                ijk, ok = grid.index(cam + ray[live] * d)
                hit[np.ravel_multi_index(ijk[ok].T, grid.shape)] = True
        seen += hit.reshape(grid.shape)
    return seen
