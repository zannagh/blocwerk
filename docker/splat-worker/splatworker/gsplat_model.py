"""The gsplat trainer's splat model (torch, CUDA; imported by gsplat_train.py in the trainer's own Python):
parameters and their initialisation, optimisers, the loss terms, rendering and the .ply writer."""
import numpy as np
import torch
import torch.nn.functional as F

SH_C0 = 0.28209479177387814


def ssim_map(a, b, size=11, sigma=1.5):
    """Per-pixel SSIM (1, 3, h, w) of two (1, 3, h, w) images in [0, 1] (Gaussian window, 'same' padding)."""
    x = torch.arange(size, dtype=a.dtype, device=a.device) - size // 2
    g = torch.exp(-x ** 2 / (2 * sigma ** 2))
    g = g / g.sum()
    gx = g.view(1, 1, 1, size).expand(3, 1, 1, size).contiguous()
    gy = g.view(1, 1, size, 1).expand(3, 1, size, 1).contiguous()

    def blur(t):  # separable Gaussian: the same window as the 2D one, 2 x 11 instead of 121 taps
        return F.conv2d(F.conv2d(t, gx, padding=(0, size // 2), groups=3), gy, padding=(size // 2, 0), groups=3)

    mu_a, mu_b = blur(a), blur(b)
    va, vb, cov = blur(a * a) - mu_a ** 2, blur(b * b) - mu_b ** 2, blur(a * b) - mu_a * mu_b
    c1, c2 = 0.01 ** 2, 0.03 ** 2
    return ((2 * mu_a * mu_b + c1) * (2 * cov + c2)) / ((mu_a ** 2 + mu_b ** 2 + c1) * (va + vb + c2))


def ssim(a, b, size=11, sigma=1.5):
    """Mean SSIM of two (1, 3, h, w) images in [0, 1]."""
    return ssim_map(a, b, size, sigma).mean()


def knn_mean_dist(points, k=3, chunk=2048):
    """Mean distance of every point to its k nearest neighbours (chunked on the GPU)."""
    out = torch.empty(len(points), device=points.device)
    for i in range(0, len(points), chunk):
        d = torch.cdist(points[i:i + chunk], points)
        out[i:i + chunk] = d.topk(min(k + 1, len(points)), largest=False).values[:, 1:].mean(1)
    return out


def init_params(xyz, rgb, cap, device):
    """Splats at the sparse points (a random subset when there are more than the cap)."""
    if len(xyz) > cap:
        keep = np.random.default_rng(0).choice(len(xyz), cap, replace=False)
        xyz, rgb = xyz[keep], rgb[keep]
    pts = torch.tensor(xyz, dtype=torch.float32, device=device)
    dist = knn_mean_dist(pts).clamp_min(1e-7)
    n = len(pts)
    return torch.nn.ParameterDict({
        "means": torch.nn.Parameter(pts),
        "scales": torch.nn.Parameter(torch.log(dist * 0.1)[:, None].repeat(1, 3)),  # MCMC init_scale 0.1
        "quats": torch.nn.Parameter(F.normalize(torch.rand(n, 4, device=device), dim=-1)),
        "opacities": torch.nn.Parameter(torch.logit(torch.full((n,), 0.5, device=device))),  # init_opa 0.5
        "sh0": torch.nn.Parameter(((torch.tensor(rgb, dtype=torch.float32, device=device) / 255 - 0.5)
                                   / SH_C0)[:, None, :]),
    })


def make_optimizers(params, scene_scale):
    lrs = {"means": 1.6e-4 * scene_scale, "scales": 5e-3, "quats": 1e-3, "opacities": 5e-2, "sh0": 2.5e-3}
    return {k: torch.optim.Adam([{"params": params[k], "lr": lr, "name": k}], eps=1e-15) for k, lr in lrs.items()}


def regularisers(params, a):
    """MCMC's opacity and scale regularisers (means over all splats) and the needle penalty: the log of
    each splat's longest / middle axis beyond log(a.aniso_max) (flat discs stay free: a wall is flat)."""
    log_s = params["scales"]
    loss = a.opacity_reg * torch.sigmoid(params["opacities"]).mean() + a.scale_reg * torch.exp(log_s).mean()
    if a.aniso_reg > 0:
        top = torch.topk(log_s, 2, dim=1).values
        loss = loss + a.aniso_reg * F.relu(top[:, 0] - top[:, 1] - float(np.log(a.aniso_max))).mean()
    return loss


def write_ply(params, path, keep=None):
    """Standard 3DGS layout (x y z f_dc_* opacity scale_* rot_*), binary little-endian floats."""
    with torch.no_grad():
        cols = torch.cat([params["means"], params["sh0"][:, 0, :], params["opacities"][:, None],
                          params["scales"], F.normalize(params["quats"], dim=-1)], 1)
        if keep is not None:
            cols = cols[keep]
        cols = cols.float().cpu().numpy()
    names = ["x", "y", "z", "f_dc_0", "f_dc_1", "f_dc_2", "opacity", "scale_0", "scale_1", "scale_2",
             "rot_0", "rot_1", "rot_2", "rot_3"]
    header = "ply\nformat binary_little_endian 1.0\n" + f"element vertex {len(cols)}\n" + \
             "".join(f"property float {n}\n" for n in names) + "end_header\n"
    with open(path, "wb") as fh:
        fh.write(header.encode("ascii"))
        fh.write(np.ascontiguousarray(cols, dtype="<f4").tobytes())
    return len(cols)


def render(params, K, viewmat, w, h, packed):
    from gsplat import rasterization
    colors, _, _ = rasterization(
        params["means"], params["quats"], torch.exp(params["scales"]), torch.sigmoid(params["opacities"]),
        params["sh0"], viewmat[None], K[None], w, h, sh_degree=0, packed=packed, near_plane=0.01,
        far_plane=1e10, rasterize_mode="classic")
    return colors[0]


def d_ssim(out, gt, crop=0):
    """1 - SSIM of (h, w, 3) images; crop > 0: over one random crop x crop window (SSIM's element-wise
    maps cost ~10x the L1 at 12 MP; the window changes every step, so every region is still covered)."""
    a, b = out.permute(2, 0, 1)[None], gt.permute(2, 0, 1)[None]
    h, w = a.shape[-2:]
    if crop and (h > crop or w > crop):
        y = int(torch.randint(0, max(1, h - crop + 1), (1,)))
        x = int(torch.randint(0, max(1, w - crop + 1), (1,)))
        a, b = a[..., y:y + crop, x:x + crop], b[..., y:y + crop, x:x + crop]
    return 1 - ssim(a, b)
