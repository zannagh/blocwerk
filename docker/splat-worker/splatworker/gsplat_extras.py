"""Optional per-view corrections of the gsplat trainer (torch), both off unless asked for:

- PoseCorrection (--pose-lr): a small learned rigid correction per training view (axis-angle + shift, the
  shift in units of the scene scale), applied to COLMAP's world-to-camera matrix. Sharpens what a pose
  error of a pixel or two smears (ghosting). Held-out views keep COLMAP's pose.
- FrameAppearance (--appearance-lr): a per-channel gain and offset per VIDEO FRAME, applied to the
  rendered image before the loss. The walk-along video has its own exposure and white balance per frame;
  the photos have none (identity), so the splat's colours are the photos' and not an average of the two.
"""
import torch


def axis_angle(w):
    """(n, 3) axis-angle -> (n, 3, 3) rotation (Rodrigues; exact at 0)."""
    th = w.norm(dim=1, keepdim=True).clamp_min(1e-12)
    k = w / th
    K = torch.zeros(len(w), 3, 3, device=w.device, dtype=w.dtype)
    K[:, 0, 1], K[:, 0, 2], K[:, 1, 2] = -k[:, 2], k[:, 1], -k[:, 0]
    K = K - K.transpose(1, 2)
    s, c = torch.sin(th)[..., None], torch.cos(th)[..., None]
    return torch.eye(3, device=w.device, dtype=w.dtype)[None] + s * K + (1 - c) * (K @ K)


class PoseCorrection(torch.nn.Module):
    def __init__(self, n_views, scene_scale, device):
        super().__init__()
        self.delta = torch.nn.Parameter(torch.zeros(n_views, 6, device=device))
        self.scale = float(scene_scale)

    def forward(self, i, viewmat):
        d = self.delta[i:i + 1]
        D = torch.eye(4, device=viewmat.device, dtype=viewmat.dtype)
        D = torch.cat([torch.cat([axis_angle(d[:, :3])[0], (d[0, 3:] * self.scale)[:, None]], 1), D[3:]], 0)
        return D @ viewmat

    def report(self):
        with torch.no_grad():
            deg = torch.rad2deg(self.delta[:, :3].norm(dim=1))
            shift = self.delta[:, 3:].norm(dim=1) * self.scale
        return {"rotDegMedian": round(float(deg.median()), 4), "rotDegMax": round(float(deg.max()), 4),
                "shiftMedian": round(float(shift.median()), 5), "shiftMax": round(float(shift.max()), 5)}


class FrameAppearance(torch.nn.Module):
    def __init__(self, is_frame, device):
        super().__init__()
        self.mask = torch.tensor([bool(f) for f in is_frame], device=device)
        self.gain = torch.nn.Parameter(torch.zeros(len(is_frame), 3, device=device))  # log gain
        self.bias = torch.nn.Parameter(torch.zeros(len(is_frame), 3, device=device))

    def forward(self, i, img):
        if not bool(self.mask[i]):
            return img
        return img * torch.exp(self.gain[i]) + self.bias[i]

    def report(self):
        with torch.no_grad():
            g = torch.exp(self.gain[self.mask]) if bool(self.mask.any()) else torch.ones(1, 3)
        return {"frameGainMean": [round(float(v), 4) for v in g.mean(0)]}
