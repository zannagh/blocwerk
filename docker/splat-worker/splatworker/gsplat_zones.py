"""The wall zones inside the gsplat trainer (torch; zones.py has the geometry and the numpy side).

ZonedMCMC is gsplat's MCMCStrategy with one change: where relocated ("teleported") and new splats are
sampled. MCMC samples them in proportion to the live splats' opacity, i.e. wherever the scene is dense,
and a room around a wall is most of any photo: without zones ~80 % of the cap went to the room. Here the
sampling weight is opacity x the zone's weight: WALL 1, SURROUND 1 until it holds surround_share of the
cap (then 0), OUTSIDE 0. OUTSIDE splats keep their sparse init and are trained, never multiplied: they
explain the room and whatever stands in front of the wall, so that is not painted into the wall.

The facets are opaque (see_through, gsplat_train --behind-reg): on a pixel whose ray hits a facet, what
shows through it (1 - the opacity in front of the facet's slab back) is penalised. Without it, plain
plywood was fitted by a translucent slab plus big room splats far behind the wall that the export cuts: a
see-through, bleached wall.
"""
import json

import torch
from gsplat.relocation import compute_relocation
from gsplat.strategy import MCMCStrategy
from gsplat.strategy.ops import _multinomial_sample, _update_param_with_optimizer

from .zones import OUTSIDE, SURROUND, WALL, classify


class ZoneMap:
    """zones.json (zones.spec + toWorldMm: COLMAP -> world mm) on the device."""

    def __init__(self, path, device):
        with open(path) as fh:
            self.spec = json.load(fh)
        M = torch.tensor(self.spec["toWorldMm"], dtype=torch.float32, device=device)
        self.lin, self.shift = M[:3, :3], M[:3, 3]
        self.device = device
        f = self.spec["facets"]
        self.normals = torch.tensor([x["n"] for x in f], dtype=torch.float32, device=device)  # (facets, 3)
        self.offsets = (torch.tensor([x["o"] for x in f], dtype=torch.float32, device=device) * self.normals).sum(1)
        self.planes = [(*(torch.tensor(x[k], dtype=torch.float32, device=device) for k in ("o", "u", "v", "n")), x["ext"])
                       for x in f]

    def world(self, xyz):
        return xyz @ self.lin.T + self.shift

    @torch.no_grad()
    def classify(self, means):
        return classify(self.world(means.detach()), self.spec, torch)

    @torch.no_grad()
    def facet_hit(self, K, viewmat, w, h, stride=1):
        """(h, w) long: per pixel the facet its ray hits first inside the facet's outline, -1 for none. With
        stride > 1 only every stride-th pixel's (from stride // 2), i.e. image[stride // 2::stride, ...]'s."""
        R, t = viewmat[:3, :3], viewmat[:3, 3]
        gh, gw = -(-(h - stride // 2) // stride), -(-(w - stride // 2) // stride)  # the pixels stride // 2 + i stride
        off = stride // 2 + 0.5
        ys, xs = torch.meshgrid(torch.arange(gh, device=self.device, dtype=torch.float32) * stride + off,
                                torch.arange(gw, device=self.device, dtype=torch.float32) * stride + off, indexing="ij")
        d_cam = torch.stack([(xs - K[0, 2]) / K[0, 0], (ys - K[1, 2]) / K[1, 1], torch.ones_like(xs)], -1)
        d = (d_cam.reshape(-1, 3) @ R) @ self.lin.T  # camera -> COLMAP (R^T d) -> world directions
        c = self.world((-R.T @ t)[None])[0]
        best = torch.full((gh * gw,), float("inf"), device=self.device)
        hit = torch.full((gh * gw,), -1, dtype=torch.long, device=self.device)
        for i, (o, u, v, n, ext) in enumerate(self.planes):
            denom = d @ n
            s = ((o - c) @ n) / torch.where(denom.abs() < 1e-9, torch.full_like(denom, 1e-9), denom)
            p = c + s[:, None] * d - o
            a, b = p @ u, p @ v
            a0, a1, b0, b1 = ext
            first = (s > 0) & (a > a0) & (a < a1) & (b > b0) & (b < b1) & (s < best)
            best = torch.where(first, s, best)
            hit = torch.where(first, torch.full_like(hit, i), hit)
        return hit.reshape(gh, gw)

    def wall_pixels(self, K, viewmat, w, h):
        """(h, w) bool: pixels whose ray hits a facet inside its outline (occlusion ignored): the pixels the
        wall-only metrics score."""
        return self.facet_hit(K, viewmat, w, h) >= 0

    @torch.no_grad()
    def behind(self, means):
        """(n, 1 + facets) float: 1, then per facet 1 where a splat lies more than slab_back_mm behind its plane.
        Rendered as extra channels: the opacity, and per facet the part of it from behind that facet."""
        back = float(self.spec["params"]["slab_back_mm"])
        far = ((self.world(means.detach()) @ self.normals.T - self.offsets) < -back).float()
        return torch.cat([torch.ones_like(far[:, :1]), far], 1)


def _weights(zone, cap, share):
    w = torch.ones(len(zone), device=zone.device)
    w[zone == OUTSIDE] = 0
    if int((zone == SURROUND).sum()) >= share * cap:
        w[zone == SURROUND] = 0
    return w


def _sample(opacities, weights, idx, n):
    probs = opacities[idx] * weights[idx]
    if float(probs.sum()) <= 0:  # nothing in the wall zones (a misaligned wall): plain MCMC
        probs = opacities[idx]
    return idx[_multinomial_sample(probs, n, replacement=True)]


@torch.no_grad()
def _clone_into(params, optimizers, sampled, binoms, min_opacity, dead=None):
    """gsplat's relocate (dead given: the dead take the sampled splats' place) or sample_add (appended),
    both splitting the sampled splats' opacity and scale as MCMC does."""
    opacities = torch.sigmoid(params["opacities"])
    new_op, new_sc = compute_relocation(opacities=opacities[sampled], scales=torch.exp(params["scales"])[sampled],
                                        ratios=torch.bincount(sampled)[sampled] + 1, binoms=binoms)
    new_op = torch.clamp(new_op, max=1.0 - torch.finfo(torch.float32).eps, min=min_opacity)

    def param_fn(name, p):
        if name == "opacities":
            p[sampled] = torch.logit(new_op)
        elif name == "scales":
            p[sampled] = torch.log(new_sc)
        if dead is None:
            return torch.nn.Parameter(torch.cat([p, p[sampled]]), requires_grad=p.requires_grad)
        p[dead] = p[sampled]
        return torch.nn.Parameter(p, requires_grad=p.requires_grad)

    def optimizer_fn(key, v):
        if dead is None:
            return torch.cat([v, torch.zeros((len(sampled), *v.shape[1:]), device=v.device)])
        v[sampled] = 0
        return v

    _update_param_with_optimizer(param_fn, optimizer_fn, params, optimizers)


class ZonedMCMC(MCMCStrategy):
    """MCMCStrategy whose relocation and growth sample the wall zones only (set .zones and .share)."""
    zones: object = None
    share: float = 0.1
    last_counts: dict = None

    def _zone_weights(self, params):
        zone = self.zones.classify(params["means"])
        self.last_counts = {"wall": int((zone == WALL).sum()), "surround": int((zone == SURROUND).sum()),
                            "outside": int((zone == OUTSIDE).sum())}
        return _weights(zone, self.cap_max, self.share)

    @torch.no_grad()
    def _relocate_gs(self, params, optimizers, binoms):
        opacities = torch.sigmoid(params["opacities"].flatten())
        dead = opacities <= self.min_opacity
        n = int(dead.sum())
        if n:
            w = self._zone_weights(params)
            sampled = _sample(opacities, w, (~dead).nonzero(as_tuple=True)[0], n)
            _clone_into(params, optimizers, sampled, binoms, self.min_opacity, dead.nonzero(as_tuple=True)[0])
        return n

    @torch.no_grad()
    def _add_new_gs(self, params, optimizers, binoms):
        current = len(params["means"])
        n = max(0, min(self.cap_max, int(1.05 * current)) - current)
        if n:
            opacities = torch.sigmoid(params["opacities"].flatten())
            w = self._zone_weights(params)
            sampled = _sample(opacities, w, torch.arange(current, device=opacities.device), n)
            _clone_into(params, optimizers, sampled, binoms, self.min_opacity)
        return n


def zone_counts(zones, means):
    zone = zones.classify(means)
    return {name: int((zone == i).sum()) for i, name in enumerate(("wall", "surround", "outside"))}


def see_through(behind_img, hit, stride=1):
    """Mean over the wall pixels of what shows through the facet the pixel's ray hits: 1 - the opacity in front
    of it (its slab back), from the (h, w, 1 + facets) channels of ZoneMap.behind and facet_hit(..., stride).
    0 where the facets are opaque, as the real wall is: not the room behind, nor the background."""
    img = behind_img[stride // 2::stride, stride // 2::stride][:hit.shape[0], :hit.shape[1]]
    mask = (hit >= 0).float()
    seen = 1 - img[..., 0] + img[..., 1:].gather(-1, hit.clamp_min(0)[..., None])[..., 0]
    return (seen * mask).sum() / mask.sum().clamp_min(1)


def masked_scores(out, gt, mask):
    """(PSNR, SSIM) over the mask's pixels only (None when the view shows no wall)."""
    from .gsplat_model import ssim_map
    if int(mask.sum()) < 100:
        return None
    m = mask[..., None].expand_as(out)
    mse = ((out - gt) ** 2)[m].mean().clamp_min(1e-10)
    s = ssim_map(out.permute(2, 0, 1)[None], gt.permute(2, 0, 1)[None])[0].permute(1, 2, 0)[m].mean()
    return float(-10 * torch.log10(mse)), float(s)
