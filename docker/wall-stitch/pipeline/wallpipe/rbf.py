"""Thin-plate-spline residual field and deterministic seeding.

Vendored from work/holds-match/hm_common.py, minus that module's fixed wall paths.
"""
import cv2
import numpy as np

SEED = 20260822


def seed_everything(seed=SEED):
    cv2.setRNGSeed(seed)
    np.random.seed(seed)
    return np.random.default_rng(seed)


class RbfField:
    """Thin-plate-spline residual field (2-D in, 2-D out) with ridge regularisation."""

    def __init__(self, ctrl, weights, affine, scale):
        self.ctrl = ctrl
        self.weights = weights
        self.affine = affine
        self.scale = scale

    @staticmethod
    def _phi(r2):
        r2 = np.maximum(r2, 1e-12)
        return 0.5 * r2 * np.log(r2)

    @classmethod
    def fit(cls, ctrl, values, lam=1e-3):
        ctrl = np.asarray(ctrl, np.float64)
        values = np.asarray(values, np.float64)
        scale = float(np.std(ctrl)) or 1.0
        c = ctrl / scale
        d2 = ((c[:, None, :] - c[None, :, :]) ** 2).sum(-1)
        k = cls._phi(d2) + lam * np.eye(len(c))
        p = np.hstack([np.ones((len(c), 1)), c])
        a = np.vstack([np.hstack([k, p]), np.hstack([p.T, np.zeros((3, 3))])])
        b = np.vstack([values, np.zeros((3, values.shape[1]))])
        sol = np.linalg.lstsq(a, b, rcond=None)[0]
        return cls(c, sol[: len(c)], sol[len(c):], scale)

    def __call__(self, pts):
        q = np.atleast_2d(np.asarray(pts, np.float64)) / self.scale
        d2 = ((q[:, None, :] - self.ctrl[None, :, :]) ** 2).sum(-1)
        out = self._phi(d2) @ self.weights
        out += np.hstack([np.ones((len(q), 1)), q]) @ self.affine
        return out
