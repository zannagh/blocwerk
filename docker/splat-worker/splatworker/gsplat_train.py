"""gsplat trainer script (CUDA): `python -m splatworker.gsplat_train --data <dataset> --out <ply> ...`.

Runs in the trainer's own Python (Dockerfile.cuda: CPython 3.10 + torch + the prebuilt gsplat wheel),
started by gsplat_trainer.train as a subprocess, so the worker never imports torch and a CUDA failure
only takes this process down. Output: one progress line per ~0.5 % ("step 1200/15000 splats 812345
loss 0.0612"), and on CUDA out-of-memory the line "GSPLAT_OOM <message>" with exit code OOM_EXIT.
--eval-every n (GSPLAT_EVAL_EVERY): views 0, n, 2n, ... (by name) never train and are scored at the end
("eval psnr 27.412 ssim 0.8631 views 6"): the measure of how well the splat renders unseen viewpoints.

The recipe is gsplat's examples/simple_trainer.py with the MCMC strategy (3DGS as Markov Chain Monte
Carlo, Kheradmand et al. 2024), trimmed to what the worker needs:
- NO world-space normalisation: poses and points are COLMAP's as read (colmap_model.py); the scene
  scale only scales the position learning rate. The written .ply is in the COLMAP frame.
- MCMC with cap_max = the profile's splat cap: the count grows 5 % per refine up to the cap and never
  beyond (the default densification strategy has no hard cap, so VRAM would not be plannable), and at
  a given count MCMC scores higher than densify-and-prune in the paper's and gsplat's benchmarks.
- SH degree 0: the exports keep only the DC colour (splatio.py); training higher bands would let the
  DC drift from what every view shows (the same reasoning as profiles.py for Brush).
- L1 + 0.2 D-SSIM (plain torch SSIM: no compiled fused-ssim), opacity and scale regularisers 0.01.
"""
import argparse
import sys
import time

import numpy as np
import torch
import torch.nn.functional as F

OOM_EXIT = 75
SH_C0 = 0.28209479177387814


def ssim(a, b, size=11, sigma=1.5):
    """Mean SSIM of two (1, 3, h, w) images in [0, 1] (Gaussian window, 'same' padding)."""
    x = torch.arange(size, dtype=a.dtype, device=a.device) - size // 2
    g = torch.exp(-x ** 2 / (2 * sigma ** 2))
    g = (g / g.sum())
    win = (g[:, None] @ g[None, :]).expand(3, 1, size, size).contiguous()

    def blur(t):
        return F.conv2d(t, win, padding=size // 2, groups=3)

    mu_a, mu_b = blur(a), blur(b)
    va, vb, cov = blur(a * a) - mu_a ** 2, blur(b * b) - mu_b ** 2, blur(a * b) - mu_a * mu_b
    c1, c2 = 0.01 ** 2, 0.03 ** 2
    s = ((2 * mu_a * mu_b + c1) * (2 * cov + c2)) / ((mu_a ** 2 + mu_b ** 2 + c1) * (va + vb + c2))
    return s.mean()


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


def write_ply(params, path):
    """Standard 3DGS layout (x y z f_dc_* opacity scale_* rot_*), binary little-endian floats."""
    with torch.no_grad():
        cols = torch.cat([params["means"], params["sh0"][:, 0, :], params["opacities"][:, None],
                          params["scales"], F.normalize(params["quats"], dim=-1)], 1).float().cpu().numpy()
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


@torch.no_grad()
def evaluate(params, views, ids, device, packed):
    """Mean PSNR / SSIM of the held-out views `ids` (never trained on) rendered from their COLMAP poses."""
    psnrs, ssims = [], []
    for i in ids:
        img, K, vm = views.get(i)
        gt = torch.from_numpy(img).to(device).float().div_(255)
        out = render(params, torch.from_numpy(K).to(device), torch.from_numpy(vm).to(device),
                     img.shape[1], img.shape[0], packed).clamp(0, 1)
        mse = F.mse_loss(out, gt).clamp_min(1e-10)
        psnrs.append(float(-10 * torch.log10(mse)))
        ssims.append(float(ssim(out.permute(2, 0, 1)[None], gt.permute(2, 0, 1)[None])))
    return float(np.mean(psnrs)), float(np.mean(ssims))


def train(a, views, device, train_ids):
    from gsplat.strategy import MCMCStrategy
    torch.manual_seed(0)
    params = init_params(views.points, views.colors, a.cap, device)
    print(f"init {len(params['means'])} splats from {len(views.points)} sparse points; "
          f"scene scale {views.scene_scale:.3f} (world space NOT normalised)", flush=True)
    opts = make_optimizers(params, views.scene_scale * 1.1)
    sched = torch.optim.lr_scheduler.ExponentialLR(opts["means"], gamma=0.01 ** (1.0 / a.steps))
    strategy = MCMCStrategy(cap_max=a.cap, refine_stop_iter=max(1000, int(a.steps * a.refine_stop)),
                            verbose=False)
    strategy.check_sanity(params, opts)
    state = strategy.initialize_state()
    packed = a.cap > 2_000_000  # packed rasterisation: less memory for big scenes, a little slower
    order, every, t0 = [], max(1, a.steps // 200), time.time()
    rng = np.random.default_rng(0)
    for step in range(a.steps):
        if not order:
            order = [train_ids[i] for i in rng.permutation(len(train_ids))]
        img, K, vm = views.get(order.pop())
        gt = torch.from_numpy(img).to(device, non_blocking=True).float().div_(255)
        out = render(params, torch.from_numpy(K).to(device), torch.from_numpy(vm).to(device),
                     img.shape[1], img.shape[0], packed)
        l1 = (out - gt).abs().mean()
        d_ssim = 1 - ssim(out.permute(2, 0, 1)[None], gt.permute(2, 0, 1)[None])
        loss = 0.8 * l1 + 0.2 * d_ssim + 0.01 * torch.sigmoid(params["opacities"]).mean() \
            + 0.01 * torch.exp(params["scales"]).mean()
        loss.backward()
        for opt in opts.values():
            opt.step()
            opt.zero_grad(set_to_none=True)
        sched.step()
        strategy.step_post_backward(params, opts, state, step, {}, lr=sched.get_last_lr()[0])
        if (step + 1) % every == 0 or step + 1 == a.steps:  # .item() syncs with the GPU: only here
            print(f"step {step + 1}/{a.steps} splats {len(params['means'])} loss {loss.item():.4f}", flush=True)
    print(f"Training took {time.time() - t0:.1f}s", flush=True)
    return params


def main(argv=None):
    p = argparse.ArgumentParser()
    p.add_argument("--data", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--steps", type=int, required=True)
    p.add_argument("--max-edge", type=int, required=True)
    p.add_argument("--cap", type=int, required=True)
    p.add_argument("--refine-stop", type=float, default=0.5, help="fraction of the steps after which growth stops")
    p.add_argument("--cache-mb", type=int, default=4096)
    p.add_argument("--eval-every", type=int, default=0, help="hold out every n-th view and score it (0 = off)")
    a = p.parse_args(argv)
    from .gsplat_data import Views, eval_split
    if not torch.cuda.is_available():
        print("error: no CUDA device (run the container with --gpus all / an NVIDIA device reservation)")
        return 2
    device = torch.device("cuda")
    torch.backends.cudnn.enabled = False  # Dockerfile.cuda ships without cuDNN's engine libraries
    free, total = torch.cuda.mem_get_info()
    print(f"device {torch.cuda.get_device_name(0)}, {free >> 20} of {total >> 20} MB free", flush=True)
    views = Views(a.data, a.max_edge, a.cache_mb,
                  lambda i, n: print(f"loaded {i}/{n} images", flush=True))
    print(f"{len(views)} views at <= {a.max_edge} px ({len(views.cache)} cached)", flush=True)
    try:
        train_ids, held = eval_split(len(views), a.eval_every)
        if held:
            print(f"holding out {len(held)} of {len(views)} views for evaluation (every {a.eval_every})", flush=True)
        params = train(a, views, device, train_ids)
        if held:
            psnr, s = evaluate(params, views, held, device, a.cap > 2_000_000)
            print(f"eval psnr {psnr:.3f} ssim {s:.4f} views {len(held)}", flush=True)
        print("writing splats", flush=True)
        n = write_ply(params, a.out)
    except torch.cuda.OutOfMemoryError as e:
        print(f"GSPLAT_OOM {str(e).splitlines()[0][:200]}", flush=True)
        return OOM_EXIT
    print(f"wrote {n} splats; peak VRAM {torch.cuda.max_memory_reserved() >> 20} MB", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
