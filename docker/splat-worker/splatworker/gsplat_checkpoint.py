"""Saving and restoring the whole state of a gsplat training (gsplat_train.py, in the trainer's own Python): the
splats, their Adam state and learning-rate schedule, the MCMC strategy's state, the pose correction and frame
appearance with their optimisers, every RNG and the shuffled view order. A restored run continues exactly
where the saved one stopped. Where the files live and when a checkpoint may be resumed: checkpoints.py."""
import os

import torch

from . import checkpoints
from .gsplat_model import make_optimizers


def frame_doc(frame):
    """The training frame (gsplat_data.unit_frame: (centre, s), or None = COLMAP's) as plain numbers."""
    return None if frame is None else {"centre": [float(v) for v in frame[0]], "scale": float(frame[1])}


def same_frame(a, b, tol=1e-6):
    """Whether two frame_doc()s are the same frame (a checkpoint resumes only in the frame it was trained in)."""
    if a is None or b is None:
        return a is None and b is None
    return abs(a["scale"] - b["scale"]) <= tol * max(1.0, abs(b["scale"])) and \
        all(abs(x - y) <= tol * max(1.0, abs(y)) for x, y in zip(a["centre"], b["centre"]))


def save(t, directory, sig):
    """Writes the checkpoint after t.step steps (atomically) and drops the older ones."""
    os.makedirs(directory, exist_ok=True)
    pt, _ = checkpoints.paths(directory, t.step)
    doc = {"step": t.step, "params": {k: v.detach() for k, v in t.params.items()},
           "opts": {k: o.state_dict() for k, o in t.opts.items()}, "sched": t.sched.state_dict(),
           "strategy": t.state, "extra": [o.state_dict() for o in t.extra_opts],
           "pose": None if t.pose is None else t.pose.state_dict(),
           "app": None if t.app is None else t.app.state_dict(),
           "rng": {"numpy": t.rng.bit_generator.state, "torch": torch.get_rng_state(),
                   "cuda": torch.cuda.get_rng_state_all() if torch.cuda.is_available() else []},
           "order": [int(i) for i in t.order], "frame": frame_doc(getattr(t, "frame", None))}
    with open(pt + ".part", "wb") as fh:
        torch.save(doc, fh)
        checkpoints.durable(fh)
    os.replace(pt + ".part", pt)
    checkpoints.commit(directory, t.step, sig)  # fsyncs the directory after both renames


def restore(t, pt, device, lr_scale):
    """Replaces t's fresh state with the checkpoint's; t.sched_state is loaded once the scheduler exists."""
    doc = torch.load(pt, map_location=device, weights_only=False)
    if not same_frame(doc.get("frame"), frame_doc(getattr(t, "frame", None))):
        raise ValueError(f"checkpoint trained in another frame ({doc.get('frame')})")
    t.params = torch.nn.ParameterDict({k: torch.nn.Parameter(v.to(device)) for k, v in doc["params"].items()})
    t.opts = make_optimizers(t.params, lr_scale)
    for k, opt in t.opts.items():
        opt.load_state_dict(doc["opts"][k])
    t.state = doc["strategy"]
    for mod, key in ((t.pose, "pose"), (t.app, "app")):
        if mod is not None and doc.get(key) is not None:
            mod.load_state_dict(doc[key])  # in place: the optimisers keep their parameters
    for opt, state in zip(t.extra_opts, doc["extra"]):
        opt.load_state_dict(state)
    t.rng.bit_generator.state = doc["rng"]["numpy"]
    torch.set_rng_state(doc["rng"]["torch"].cpu())
    if doc["rng"]["cuda"] and torch.cuda.is_available():
        torch.cuda.set_rng_state_all([s.cpu() for s in doc["rng"]["cuda"]])
    t.order, t.step, t.sched_state = list(doc["order"]), int(doc["step"]), doc["sched"]
    return t
