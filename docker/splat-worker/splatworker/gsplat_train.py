"""gsplat trainer script (CUDA): `python -m splatworker.gsplat_train --data <dataset> --out <ply> ...`.

Runs in the trainer's own Python (Dockerfile.cuda: CPython 3.10 + torch + the prebuilt gsplat wheel),
started by gsplat_trainer.train as a subprocess, so the worker never imports torch and a CUDA failure
only takes this process down. Output: one progress line per ~0.5 % ("step 1200/15000 splats 812345
loss 0.0612"), and on CUDA out-of-memory the line "GSPLAT_OOM <message>" with exit code OOM_EXIT.
--eval-every n (GSPLAT_EVAL_EVERY): photos 0, n, 2n, ... (by name; video frames always train) never
train and are scored at the end ("eval psnr 27.412 ssim 0.8631 views 6"; with --zones also over the
wall's pixels only: "eval wall psnr ..."): how well the splat renders unseen viewpoints.

The recipe is gsplat's examples/simple_trainer.py with the MCMC strategy (3DGS as Markov Chain Monte
Carlo, Kheradmand et al. 2024), trimmed to what the worker needs:
- NO world-space normalisation: poses and points are COLMAP's as read (colmap_model.py); the scene
  scale only scales the position learning rate. The written .ply is in the COLMAP frame.
- MCMC with cap_max = the profile's splat cap: the count grows 5 % per refine up to the cap and never
  beyond (the default densification strategy has no hard cap, so VRAM would not be plannable).
- --zones (zones.json, from the wall geometry): ZonedMCMC (gsplat_zones.py) spends the cap on the wall
  and a small share on its surroundings, never on the room.
- SH degree 0: the exports keep only the DC colour (splatio.py).
- L1 + 0.2 D-SSIM (plain torch SSIM), opacity and scale regularisers (0.01 each, as MCMC), optionally a
  needle penalty (--aniso-reg), per-view pose correction and per-frame appearance (gsplat_extras.py).
- --behind-reg (with --zones): the facets are opaque; what a facet pixel shows from behind that facet's
  plane (room splats or the background) is penalised (gsplat_zones.see_through).
"""
import argparse
import json
import sys
import time

import numpy as np
import torch

from .gsplat_model import d_ssim, init_params, make_optimizers, regularisers, render, ssim, write_ply

OOM_EXIT = 75
# --behind-reg: every BEHIND_EVERY-th step (its 4 + facets channels cost ~2/3 of a render), over every
# BEHIND_STRIDE-th pixel's ray (a penalty, not a picture: 1/16 of the pixels is plenty)
BEHIND_EVERY, BEHIND_STRIDE = 4, 4
AIR_EVERY = 100  # --aniso-air-reg: the air mask (gsplat_zones.ZoneMap.air_mask) is refreshed this often


def air_due(step, air, n, every=AIR_EVERY):
    """Whether the air mask must be recomputed before `step`: first, after growth (n changed), and on the step
    right after MCMC's refine step (step % every == 0 relocates dead splats onto live ones at the same count:
    refreshed before it, a relocated wall splat kept its old slot's air flag, and its penalty, for 99 steps)."""
    return air is None or len(air) != n or step % every == 1


@torch.no_grad()
def evaluate(params, views, ids, device, packed, zones=None):
    """{"psnr", "ssim", "views"} of the held-out views `ids` (never trained on) from their COLMAP poses,
    plus "wall": the same over the pixels showing a facet (zones given)."""
    from .gsplat_zones import masked_scores
    full, wall = [], []
    for i in ids:
        img, K, vm = views.get(i)
        gt = torch.from_numpy(img).to(device).float().div_(255)
        K, vm = torch.from_numpy(K).to(device), torch.from_numpy(vm).to(device)
        out = render(params, K, vm, img.shape[1], img.shape[0], packed).clamp(0, 1)
        mse = torch.nn.functional.mse_loss(out, gt).clamp_min(1e-10)
        full.append((float(-10 * torch.log10(mse)), float(ssim(out.permute(2, 0, 1)[None], gt.permute(2, 0, 1)[None]))))
        if zones is not None:
            s = masked_scores(out, gt, zones.wall_pixels(K, vm, img.shape[1], img.shape[0]))
            if s:
                wall.append(s)
    res = {"psnr": float(np.mean([p for p, _ in full])), "ssim": float(np.mean([s for _, s in full])), "views": len(ids)}
    if wall:
        res["wall"] = {"psnr": float(np.mean([p for p, _ in wall])), "ssim": float(np.mean([s for _, s in wall])),
                       "views": len(wall)}
    return res


def make_strategy(a, zones):
    from gsplat.strategy import MCMCStrategy
    stop = max(1000, int(a.steps * a.refine_stop))
    if zones is None or a.plain_mcmc:
        return MCMCStrategy(cap_max=a.cap, refine_stop_iter=stop, verbose=False)
    from .gsplat_zones import ZonedMCMC
    s = ZonedMCMC(cap_max=a.cap, refine_stop_iter=stop, verbose=False)
    s.zones, s.share = zones, a.surround_share
    return s


def extras(a, views, train_ids, device):
    """(pose correction or None, frame appearance or None, their optimisers)."""
    from .gsplat_extras import FrameAppearance, PoseCorrection
    pose = PoseCorrection(len(views), views.scene_scale, device) if a.pose_lr > 0 else None
    app = FrameAppearance([v["name"].split("/")[-1].startswith("vf_") for v in views.views], device) \
        if a.appearance_lr > 0 else None
    opts = []
    if pose is not None:
        opts.append(torch.optim.Adam(pose.parameters(), lr=a.pose_lr, weight_decay=a.pose_reg))
    if app is not None:
        opts.append(torch.optim.Adam(app.parameters(), lr=a.appearance_lr))
    return pose, app, opts


def step_loss(a, params, K, vm, gt, packed, appearance=None, zones=None, opaque=False, air=None):
    """One view's loss: L1 + 0.2 D-SSIM + the regularisers; `opaque` (zones given) adds --behind-reg x what
    shows through the facets (they are opaque: nothing behind the one a pixel's ray hits may show)."""
    h, w = gt.shape[:2]
    opaque = opaque and zones is not None and a.behind_reg > 0
    out = render(params, K, vm, w, h, packed, zones.behind(params["means"]) if opaque else None)
    if opaque:
        out, behind = out[..., :3], out[..., 3:]
    if appearance is not None:
        out = appearance(out)
    loss = 0.8 * (out - gt).abs().mean() + 0.2 * d_ssim(out, gt, a.ssim_crop) + regularisers(params, a, air)
    if opaque:
        from .gsplat_zones import see_through
        hit = zones.facet_hit(K, vm.detach(), w, h, BEHIND_STRIDE)
        loss = loss + a.behind_reg * see_through(behind, hit, BEHIND_STRIDE)
    return loss


def train(a, views, device, train_ids, zones=None):
    torch.manual_seed(0)
    params = init_params(views.points, views.colors, a.cap, device)
    print(f"init {len(params['means'])} splats from {len(views.points)} sparse points; "
          f"scene scale {views.scene_scale:.3f} (world space NOT normalised)", flush=True)
    opts = make_optimizers(params, views.scene_scale * 1.1)
    sched = torch.optim.lr_scheduler.ExponentialLR(opts["means"], gamma=0.01 ** (1.0 / a.steps))
    strategy = make_strategy(a, zones)
    strategy.check_sanity(params, opts)
    state = strategy.initialize_state()
    pose, app, extra_opts = extras(a, views, train_ids, device)
    packed = a.cap > 2_000_000  # packed rasterisation: less memory for big scenes, a little slower
    order, every, t0 = [], max(1, a.steps // 200), time.time()
    rng = np.random.default_rng(0)
    air = None
    for step in range(a.steps):
        if zones is not None and a.aniso_air_reg > 0 and air_due(step, air, len(params["means"]), strategy.refine_every):
            air = zones.air_mask(params["means"])  # the splats move slowly: refreshed now and then
        if not order:
            order = [train_ids[i] for i in rng.permutation(len(train_ids))]
        i = order.pop()
        img, K, vm = views.get(i)
        gt = torch.from_numpy(img).to(device, non_blocking=True).float().div_(255)
        vm = torch.from_numpy(vm).to(device)
        if pose is not None:  # its gradient (atomics over every splat) is costly: learnt early, then frozen
            vm = pose(i, vm) if step < a.pose_steps else pose(i, vm).detach()
        loss = step_loss(a, params, torch.from_numpy(K).to(device), vm, gt, packed,
                         None if app is None else (lambda o: app(i, o)), zones, step % BEHIND_EVERY == 0, air)
        loss.backward()
        for opt in list(opts.values()) + extra_opts:
            opt.step()
            opt.zero_grad(set_to_none=True)
        sched.step()
        strategy.step_post_backward(params, opts, state, step, {}, lr=sched.get_last_lr()[0])
        if (step + 1) % every == 0 or step + 1 == a.steps:  # .item() syncs with the GPU: only here
            print(f"step {step + 1}/{a.steps} splats {len(params['means'])} loss {loss.item():.4f}", flush=True)
    print(f"Training took {time.time() - t0:.1f}s", flush=True)
    for name, mod in (("pose", pose), ("appearance", app)):
        if mod is not None:
            print(f"{name} correction {json.dumps(mod.report())}", flush=True)
    return params


def parse_args(argv):
    p = argparse.ArgumentParser()
    p.add_argument("--data", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--steps", type=int, required=True)
    p.add_argument("--max-edge", type=int, required=True)
    p.add_argument("--cap", type=int, required=True)
    p.add_argument("--refine-stop", type=float, default=0.5, help="fraction of the steps after which growth stops")
    p.add_argument("--cache-mb", type=int, default=4096)
    p.add_argument("--eval-every", type=int, default=0, help="hold out every n-th photo and score it (0 = off)")
    p.add_argument("--zones", help="zones.json (zones.py): spend the cap on the wall, not the room")
    p.add_argument("--surround-share", type=float, default=0.1, help="share of the cap for the surroundings")
    p.add_argument("--plain-mcmc", action="store_true", help="zones for the wall-only scores only (a baseline)")
    p.add_argument("--behind-reg", type=float, default=0.0,
                   help="with --zones: weight of the opacity seen through the facets (0 = off)")
    p.add_argument("--ssim-crop", type=int, default=0, help="D-SSIM over a random crop of this size (0 = whole image)")
    p.add_argument("--opacity-reg", type=float, default=0.01)
    p.add_argument("--scale-reg", type=float, default=0.01)
    p.add_argument("--aniso-reg", type=float, default=0.0, help="needle penalty weight (0 = off)")
    p.add_argument("--aniso-max", type=float, default=6.0, help="longest / middle axis ratio allowed freely")
    p.add_argument("--aniso-air-reg", type=float, default=0.0,
                   help="with --zones: needle penalty weight in the air (surroundings, behind a facet; 0 = as --aniso-reg)")
    p.add_argument("--aniso-air-max", type=float, default=3.0, help="longest / middle axis ratio allowed freely there")
    p.add_argument("--pose-lr", type=float, default=0.0, help="per-view pose correction (0 = off)")
    p.add_argument("--pose-reg", type=float, default=1e-6)
    p.add_argument("--pose-steps", type=int, default=5000, help="optimise the poses for this many steps, then freeze")
    p.add_argument("--appearance-lr", type=float, default=0.0, help="per-frame colour gain/offset (0 = off)")
    return p.parse_args(argv)


def main(argv=None):
    a = parse_args(argv)
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
    zones = None
    if a.zones:
        from .gsplat_zones import ZoneMap
        zones = ZoneMap(a.zones, device)
    try:
        train_ids, held = eval_split([v["name"] for v in views.views], a.eval_every)
        if held:
            print(f"holding out {len(held)} of {len(views)} views for evaluation (every {a.eval_every}th photo)", flush=True)
        params = train(a, views, device, train_ids, zones)
        if zones is not None:
            from .gsplat_zones import zone_counts
            print(f"zones {json.dumps(zone_counts(zones, params['means']))}", flush=True)
        if held:
            r = evaluate(params, views, held, device, a.cap > 2_000_000, zones)
            print(f"eval psnr {r['psnr']:.3f} ssim {r['ssim']:.4f} views {r['views']}", flush=True)
            if "wall" in r:
                w = r["wall"]
                print(f"eval wall psnr {w['psnr']:.3f} ssim {w['ssim']:.4f} views {w['views']}", flush=True)
        print("writing splats", flush=True)
        n = write_ply(params, a.out)
    except torch.cuda.OutOfMemoryError as e:
        print(f"GSPLAT_OOM {str(e).splitlines()[0][:200]}", flush=True)
        return OOM_EXIT
    print(f"wrote {n} splats; peak VRAM {torch.cuda.max_memory_reserved() >> 20} MB", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
