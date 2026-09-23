"""Bundle adjustment: free marker poses, or markers constrained to facet planes."""
import numpy as np
from scipy.optimize import least_squares
from scipy.sparse import lil_matrix
from scipy.spatial.transform import Rotation

from .camera import N_INTR, project, rotmat  # noqa: F401  (project: reference implementation)


def rodrigues(r):
    """Batched rotation vectors (N,3) -> rotation matrices (N,3,3)."""
    r = np.atleast_2d(r)
    th = np.linalg.norm(r, axis=1)
    small = th < 1e-12
    k = r / np.where(small, 1.0, th)[:, None]
    K = np.zeros((len(r), 3, 3))
    K[:, 0, 1], K[:, 0, 2], K[:, 1, 0] = -k[:, 2], k[:, 1], k[:, 2]
    K[:, 1, 2], K[:, 2, 0], K[:, 2, 1] = -k[:, 0], -k[:, 1], k[:, 0]
    s, c = np.sin(th)[:, None, None], (1 - np.cos(th))[:, None, None]
    R = np.eye(3)[None] + s * K + c * (K @ K)
    R[small] = np.eye(3)
    return R


class Problem:
    """Parameter layout: [intr per group | cams (non-root) | structure].

    structure 'free':  per marker (rotvec, t) marker->world.
    structure 'facet': per facet (alpha, beta, dd) + per marker (a, b, psi).
    """

    def __init__(self, obs, cams, groups, root, marker_ids, obj, facets=None, prior=None):
        self.obs, self.cams, self.groups, self.root = obs, cams, groups, root
        self.obj = obj  # marker id -> (4,3) object points in the marker frame (size may differ)
        self.images = sorted(cams)
        self.free_imgs = [i for i in self.images if i != root]
        self.mids = list(marker_ids)
        self.facets = facets  # None or dict fid -> {"F0","p0","members"}
        self.prior = prior or {}
        self._layout()
        self._obs_arrays()

    # ---------- layout ----------
    def _layout(self):
        o = 0
        self.gi = {}
        for g in self.groups:
            self.gi[g] = o
            o += N_INTR
        self.ci = {}
        for img in self.free_imgs:
            self.ci[img] = o
            o += 6
        self.mi, self.fi = {}, {}
        if self.facets is None:
            for m in self.mids:
                self.mi[m] = o
                o += 6
        else:
            self.fid_of = {}
            for fid, f in self.facets.items():
                self.fi[fid] = o
                o += 3
                for m in f["members"]:
                    self.fid_of[m] = fid
            for m in self.mids:
                self.mi[m] = o
                o += 3
        self.n = o

    def _obs_arrays(self):
        self.o_img = [o["image"] for o in self.obs]
        self.o_mid = [o["id"] for o in self.obs]
        self.o_px = np.stack([o["corners"] for o in self.obs])
        self.o_w = 1.0 / np.stack([o["sigma"] for o in self.obs])
        # vectorised gathers for project_all
        img_idx = {img: k for k, img in enumerate(self.images)}
        mk_idx = {m: k for k, m in enumerate(self.mids)}
        self._oi = np.array([img_idx[i] for i in self.o_img])
        self._om = np.array([mk_idx[m] for m in self.o_mid])
        self._obj = np.stack([self.obj[m] for m in self.mids])  # (M,4,3)
        cams = [self.cams[i] for i in self.o_img]
        self._ow = np.array([c["w"] for c in cams], float)
        self._oh = np.array([c["h"] for c in cams], float)
        self._portrait = np.array([c["w"] < c["h"] for c in cams])
        self._psign = np.array([c["psign"] for c in cams], float)
        self._ogi = np.array([self.gi[c["group"]] for c in cams])

    # ---------- unpack ----------
    def intr(self, x, g):
        return x[self.gi[g]:self.gi[g] + N_INTR]

    def cam(self, x, img):
        if img == self.root:
            return np.eye(3), np.zeros(3)
        s = x[self.ci[img]:self.ci[img] + 6]
        return rotmat(s[:3])[0], s[3:]

    def facet_frame(self, x, fid):
        f = self.facets[fid]
        a, b, dd = x[self.fi[fid]:self.fi[fid] + 3]
        F0 = f["F0"]
        Rd = rotmat(a * F0[:, 0] + b * F0[:, 1])[0]
        F = Rd @ F0
        return F, f["p0"] + dd * F0[:, 2]

    def marker(self, x, m):
        """Marker->world (R, t)."""
        s = x[self.mi[m]:self.mi[m] + (6 if self.facets is None else 3)]
        if self.facets is None:
            return rotmat(s[:3])[0], s[3:]
        F, p = self.facet_frame(x, self.fid_of[m])
        a, b, psi = s
        c, sn = np.cos(psi), np.sin(psi)
        Rz = np.array([[c, -sn, 0], [sn, c, 0], [0, 0, 1.0]])
        return F @ Rz, p + F @ np.array([a, b, 0.0])

    def marker_world(self, x):
        R, t = self._marker_poses(x)
        P = np.einsum("mkj,mij->mki", self._obj, R) + t[:, None, :]
        return {m: P[k] for k, m in enumerate(self.mids)}

    # ---------- residuals ----------
    def _marker_poses(self, x):
        """All marker->world rotations (M,3,3) and translations (M,3), vectorised."""
        idx = np.array([self.mi[m] for m in self.mids])
        if self.facets is None:
            s = x[idx[:, None] + np.arange(6)]
            return rodrigues(s[:, :3]), s[:, 3:]
        frames = {fid: self.facet_frame(x, fid) for fid in self.facets}
        F = np.stack([frames[self.fid_of[m]][0] for m in self.mids])
        p = np.stack([frames[self.fid_of[m]][1] for m in self.mids])
        s = x[idx[:, None] + np.arange(3)]
        c, sn = np.cos(s[:, 2]), np.sin(s[:, 2])
        Rz = np.zeros((len(self.mids), 3, 3))
        Rz[:, 0, 0], Rz[:, 0, 1], Rz[:, 1, 0], Rz[:, 1, 1], Rz[:, 2, 2] = c, -sn, sn, c, 1.0
        ab = np.stack([s[:, 0], s[:, 1], np.zeros(len(self.mids))], 1)
        return F @ Rz, p + np.einsum("mij,mj->mi", F, ab)

    def project_all(self, x):
        R, t = self._marker_poses(x)
        P = np.einsum("mkj,mij->mki", self._obj, R) + t[:, None, :]  # (M,4,3) world corners
        cr = np.zeros((len(self.images), 3))
        ct = np.zeros((len(self.images), 3))
        for k, img in enumerate(self.images):
            if img != self.root:
                cr[k] = x[self.ci[img]:self.ci[img] + 3]
                ct[k] = x[self.ci[img] + 3:self.ci[img] + 6]
        Rc = rodrigues(cr)
        pc = np.einsum("nij,nkj->nki", Rc[self._oi], P[self._om]) + ct[self._oi][:, None, :]
        it = x[self._ogi[:, None] + np.arange(N_INTR)]  # (n,6)
        z = pc[..., 2]
        xn, yn = pc[..., 0] / z, pc[..., 1] / z
        r2 = xn * xn + yn * yn
        d = 1 + it[:, 3:4] * r2 + it[:, 4:5] * r2 * r2 + it[:, 5:6] * r2 * r2 * r2
        dx, dy = it[:, 1], it[:, 2]
        pdx = np.where(self._portrait, -self._psign * dy, dx)
        pdy = np.where(self._portrait, self._psign * dx, dy)
        cx = (self._ow - 1) / 2.0 + pdx
        cy = (self._oh - 1) / 2.0 + pdy
        f = it[:, 0:1]
        return np.stack([f * xn * d + cx[:, None], f * yn * d + cy[:, None]], -1)

    def residuals(self, x):
        px = self.project_all(x)
        r = ((px - self.o_px) * self.o_w[..., None]).ravel()
        pri = []
        for g in self.groups:
            it = self.intr(x, g)
            for j, (mean, sig) in self.prior.get(g, {}).items():
                pri.append((it[j] - mean) / sig)
        return np.concatenate([r, np.array(pri)]) if pri else r

    def sparsity(self, free):
        n_obs = len(self.obs)
        rows = n_obs * 8 + sum(len(self.prior.get(g, {})) for g in self.groups)
        S = lil_matrix((rows, self.n), dtype=int)
        for k, (img, m) in enumerate(zip(self.o_img, self.o_mid)):
            rs = slice(k * 8, k * 8 + 8)
            g = self.cams[img]["group"]
            S[rs, self.gi[g]:self.gi[g] + N_INTR] = 1
            if img != self.root:
                S[rs, self.ci[img]:self.ci[img] + 6] = 1
            if self.facets is None:
                S[rs, self.mi[m]:self.mi[m] + 6] = 1
            else:
                S[rs, self.mi[m]:self.mi[m] + 3] = 1
                fi = self.fi[self.fid_of[m]]
                S[rs, fi:fi + 3] = 1
        r = n_obs * 8
        for g in self.groups:
            for j in self.prior.get(g, {}):
                S[r, self.gi[g] + j] = 1
                r += 1
        return S.tocsr()[:, free]

    def solve(self, x0, free_mask, loss="soft_l1", f_scale=1.5, max_nfev=200):
        free = np.flatnonzero(free_mask)
        xf = x0.copy()

        def fun(z):
            xf[free] = z
            return self.residuals(xf)

        res = least_squares(fun, x0[free], jac_sparsity=self.sparsity(free), loss=loss,
                            f_scale=f_scale, x_scale="jac", max_nfev=max_nfev, method="trf")
        xf[free] = res.x
        return xf, res


def pack_free(prob, intr, cam_pose, mk_pose):
    x = np.zeros(prob.n)
    for g in prob.groups:
        x[prob.gi[g]:prob.gi[g] + N_INTR] = intr[g]
    for img in prob.free_imgs:
        R, t = cam_pose[img]
        x[prob.ci[img]:prob.ci[img] + 6] = np.r_[Rotation.from_matrix(R).as_rotvec(), t]
    for m in prob.mids:
        R, t = mk_pose[m]
        x[prob.mi[m]:prob.mi[m] + 6] = np.r_[Rotation.from_matrix(R).as_rotvec(), t]
    return x


def unpack_free(prob, x):
    """Inverse of pack_free: dicts of camera poses, marker poses, and intrinsics."""
    cam_pose = {img: prob.cam(x, img) for img in prob.images}
    mk_pose = {m: prob.marker(x, m) for m in prob.mids}
    intr = {g: prob.intr(x, g).copy() for g in prob.groups}
    return intr, cam_pose, mk_pose
