"""Shared paths, seeding, IO and smooth-map helpers for the hold re-recognition tool."""
import json
import os

import cv2
import numpy as np

SEED = 20260822

# VENDORED-COPY PATCH (docker/wall-stitch): the upstream copy hardcodes a developer's
# Desktop.  The sidecar runs one job per directory, so the root and the model path come
# from the environment; the upstream defaults are kept so the file still behaves
# identically when the variables are unset.  Remove this once the upstream CLI grows
# --old/--new/--holds/--wall flags.
WALL_ROOT = os.environ.get(
    "WALLSTITCH_WORK_ROOT", "/Users/patrickweindl/Desktop/wall-photos/work")
OLD_IMG = os.environ.get("WALLSTITCH_OLD_IMG") or os.path.join(WALL_ROOT, "holds", "wall-photo.jpg")
NEW_IMG = os.environ.get("WALLSTITCH_NEW_IMG") or os.path.join(WALL_ROOT, "06-final", "wall-orthophoto.png")
HOLDS_JSON = os.environ.get("WALLSTITCH_HOLDS_JSON") or os.path.join(WALL_ROOT, "holds", "holds.json")
WALL_JSON = os.environ.get("WALLSTITCH_WALL_JSON") or os.path.join(WALL_ROOT, "holds", "wall.json")
ONNX = os.environ.get(
    "WALLSTITCH_ONNX_MODEL",
    "/Users/patrickweindl/Projects/blocwerk/src/Blocwerk.HoldDetection/models/climbingcrux.onnx")


def seed_everything():
    cv2.setRNGSeed(SEED)
    np.random.seed(SEED)
    return np.random.default_rng(SEED)


def load_images():
    old = cv2.imread(OLD_IMG, cv2.IMREAD_COLOR)
    new = cv2.imread(NEW_IMG, cv2.IMREAD_COLOR)
    if old is None or new is None:
        raise SystemExit("could not read input images")
    return old, new


def load_holds(generation=1):
    with open(HOLDS_JSON) as fh:
        doc = json.load(fh)
    live = [h for h in doc["holds"] if h["Generation"] == generation]
    live.sort(key=lambda h: h["Id"])
    return doc, live


def hold_px(hold, w, h):
    """Stored normalisation -> pixels. X/Y are per-axis; Radius is vs the longer side."""
    return (
        hold["X"] * w,
        hold["Y"] * h,
        float(hold.get("Radius") or 0.0) * max(w, h),
    )


class PolyMap:
    """Least-squares bivariate polynomial map R2 -> R2, robustly refitted (IRLS/Huber)."""

    def __init__(self, degree, centre, scale, coeff):
        self.degree = degree
        self.centre = np.asarray(centre, np.float64)
        self.scale = float(scale)
        self.coeff = np.asarray(coeff, np.float64)

    @staticmethod
    def _feat(pts, degree, centre, scale):
        p = np.atleast_2d(np.asarray(pts, np.float64))
        x = (p[:, 0] - centre[0]) / scale
        y = (p[:, 1] - centre[1]) / scale
        cols = [np.ones_like(x)]
        for k in range(1, degree + 1):
            for i in range(k + 1):
                cols.append(x ** (k - i) * y ** i)
        return np.stack(cols, 1)

    @classmethod
    def fit(cls, src, dst, degree=3, huber=None, iters=8):
        src = np.asarray(src, np.float64)
        dst = np.asarray(dst, np.float64)
        centre = src.mean(0)
        scale = float(src.std()) or 1.0
        a = cls._feat(src, degree, centre, scale)
        w = np.ones(len(src))
        coeff = np.zeros((a.shape[1], 2))
        for _ in range(iters if huber else 1):
            sw = np.sqrt(w)[:, None]
            coeff, *_ = np.linalg.lstsq(a * sw, dst * sw, rcond=None)
            if huber is None:
                break
            r = np.linalg.norm(a @ coeff - dst, axis=1)
            w = np.where(r <= huber, 1.0, huber / np.maximum(r, 1e-9))
        return cls(degree, centre, scale, coeff)

    def __call__(self, pts):
        return self._feat(pts, self.degree, self.centre, self.scale) @ self.coeff

    def jacobian(self, pt, eps=4.0):
        p = np.asarray(pt, np.float64).reshape(1, 2)
        base = self(p)[0]
        jx = (self(p + [eps, 0])[0] - base) / eps
        jy = (self(p + [0, eps])[0] - base) / eps
        return np.stack([jx, jy], 1)  # columns = d/dx, d/dy


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
        top = np.hstack([k, p])
        bot = np.hstack([p.T, np.zeros((3, 3))])
        a = np.vstack([top, bot])
        b = np.vstack([values, np.zeros((3, values.shape[1]))])
        sol = np.linalg.lstsq(a, b, rcond=None)[0]
        return cls(c, sol[: len(c)], sol[len(c):], scale)

    def __call__(self, pts):
        q = np.atleast_2d(np.asarray(pts, np.float64)) / self.scale
        d2 = ((q[:, None, :] - self.ctrl[None, :, :]) ** 2).sum(-1)
        out = self._phi(d2) @ self.weights
        out += np.hstack([np.ones((len(q), 1)), q]) @ self.affine
        return out


def cache_path(work, name):
    d = os.path.join(work, ".cache")
    os.makedirs(d, exist_ok=True)
    return os.path.join(d, name)
