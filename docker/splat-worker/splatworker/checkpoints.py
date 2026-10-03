"""Training checkpoints and preview steps of a 3D runner's gsplat job. Stdlib only: the trainer's own Python
(gsplat_train.py, gsplat_checkpoint.py) imports it as well as the worker.

Checkpoints: gsplat_train.py saves its whole state (splats, optimisers, MCMC state, RNGs, step) every
`every` steps, and once more at the end, into a directory keyed by the job id AND the bundle's sha256
(job_dir), so a runner that claims the same job again (a CUDA error, a restart, a lost lease) resumes there
and never from another bundle's run. One checkpoint is `ckpt-<step>.pt` plus `ckpt-<step>.json` (its
signature: the options that shape the training). The .json is written last: a .pt without one is
incomplete. Only the newest checkpoint is kept (one is ~1 GB at ultra).

Previews: preview_steps(total, fractions) are the steps at which the trainer also writes the splats so far
(`preview-<step>-of-<total>.ply`), which the runner uploads while training goes on.
"""
import glob
import json
import os
import re
import shutil
import time
from dataclasses import dataclass, field

CKPT_META = re.compile(r"ckpt-(\d+)\.json$")
PREVIEW = re.compile(r"preview-(\d+)-of-(\d+)\.ply$")
PREVIEW_ROUND = 500  # preview steps are rounded to this
MIN_PREVIEW_STEP = 3000  # earlier previews are a blur: not worth a finish on the server
DEFAULT_PREVIEWS = "0.14,0.4"  # ultra (50k steps): 7000 and 20000
# The trainer's options that do not change what it trains (paths, the image cache, these very options).
NOT_SIGNED = frozenset({"data", "out", "cache_mb", "zones", "checkpoint_dir", "checkpoint_every", "preview_dir",
                        "preview_at"})


@dataclass(frozen=True)
class TrainResume:
    """What a runner job hands the trainer: where its checkpoints live (None = off), how often to save one,
    where to write previews (None = off) and at which fractions of the steps."""
    checkpoint_dir: str = None
    checkpoint_every: int = 0
    preview_dir: str = None
    preview_fractions: tuple = field(default_factory=tuple)


def job_dir(root, job_id, bundle_sha):
    """<root>/<job id>-<first 16 hex of the bundle sha256>, or None without both (never resume blindly)."""
    job = re.sub(r"[^A-Za-z0-9-]", "", str(job_id or ""))[:64]
    sha = re.sub(r"[^0-9a-f]", "", str(bundle_sha or "").lower())[:16]
    if not root or not job or len(sha) < 16:
        return None
    return os.path.join(root, f"{job}-{sha}")


def signature(options):
    """The canonical text of the options that shape a training (a checkpoint resumes only the same one)."""
    doc = {k: v for k, v in options.items() if k not in NOT_SIGNED}
    doc["zoned"] = bool(options.get("zones"))
    return json.dumps(doc, sort_keys=True, separators=(",", ":"))


def paths(directory, step):
    """(.pt, .json) of the checkpoint after `step` steps."""
    return os.path.join(directory, f"ckpt-{step}.pt"), os.path.join(directory, f"ckpt-{step}.json")


def latest(directory, sig):
    """(step, .pt path) of the newest complete checkpoint with this signature, or None."""
    best = None
    for meta in glob.glob(os.path.join(glob.escape(directory or ""), "ckpt-*.json")):
        m = CKPT_META.search(meta)
        if not m:
            continue
        try:
            with open(meta) as fh:
                doc = json.load(fh)
        except (OSError, ValueError):
            continue
        step, pt = int(m.group(1)), paths(directory, int(m.group(1)))[0]
        complete = doc.get("signature") == sig and doc.get("step") == step and os.path.exists(pt)
        if complete and (best is None or step > best[0]):
            best = (step, pt)
    return best


def newest_step(directory):
    """The step of the newest complete checkpoint in `directory` (any signature), or None. What a runner that hands
    its job back on a shutdown tells the server it would resume from."""
    best = None
    for meta in glob.glob(os.path.join(glob.escape(directory or ""), "ckpt-*.json")):
        m = CKPT_META.search(meta)
        if m and os.path.exists(paths(directory, int(m.group(1)))[0]) and (best is None or int(m.group(1)) > best):
            best = int(m.group(1))
    return best


def commit(directory, step, sig):
    """Marks the checkpoint after `step` complete (its .pt is already in place) and removes every other one."""
    pt, meta = paths(directory, step)
    tmp = meta + ".part"
    with open(tmp, "w") as fh:
        json.dump({"step": step, "signature": sig, "savedAt": time.time()}, fh)
        durable(fh)
    os.replace(tmp, meta)
    sync_dir(directory)
    drop_checkpoints(directory, keep=(pt, meta))


def durable(fh):
    """Flushes an open file to the disk (before it is renamed into place: a crash never leaves a torn checkpoint)."""
    fh.flush()
    os.fsync(fh.fileno())


def sync_dir(directory):
    """Makes the renames in `directory` durable (POSIX; a no-op where directories cannot be opened)."""
    try:
        fd = os.open(directory, os.O_RDONLY)
    except OSError:
        return
    try:
        os.fsync(fd)
    except OSError:
        pass
    finally:
        os.close(fd)


def drop_checkpoints(directory, keep=()):
    """Removes the directory's checkpoints (but `keep`); the remembered plan stays."""
    for other in glob.glob(os.path.join(glob.escape(directory or ""), "ckpt-*")):
        if other not in keep:
            try:
                os.remove(other)
            except OSError:
                pass


def resume_or_drop(directory, sig, restore, log=print):
    """Restores the newest matching checkpoint with restore(.pt path) and returns its step, or None: there is none, or
    it cannot be loaded (torn, incompatible). Then the directory's checkpoints are dropped and training starts
    afresh, so no later attempt trips over the same file."""
    found = latest(directory, sig) if directory else None
    if found is None:
        return None
    try:
        restore(found[1])
    except Exception as e:  # noqa: BLE001 - any unreadable or incompatible state means: start afresh
        log(f"checkpoint {os.path.basename(found[1])} unusable ({type(e).__name__}: {str(e)[:200]}); starting from step 0")
        drop_checkpoints(directory)
        return None
    return found[0]


def discard(directory):
    """Removes a job's checkpoints (the job succeeded or is over for good)."""
    if directory:
        shutil.rmtree(directory, ignore_errors=True)


def prune(root, max_age_s, now=None):
    """Removes checkpoint directories untouched for longer than max_age_s (a job that never came back);
    returns how many."""
    now = time.time() if now is None else now
    removed = 0
    for d in glob.glob(os.path.join(glob.escape(root or ""), "*")):
        try:
            age = now - os.path.getmtime(d)
        except OSError:
            continue
        if os.path.isdir(d) and age > max_age_s:
            shutil.rmtree(d, ignore_errors=True)
            removed += 1
    return removed


def parse_fractions(text):
    """RUNNER_PREVIEWS: "0.14,0.4" -> (0.14, 0.4); "", "0", "off", "no", "none" -> () (previews off)."""
    text = (text or "").strip().lower()
    if text in ("", "0", "off", "no", "none", "false"):
        return ()
    out = []
    for part in text.split(","):
        try:
            f = float(part)
        except ValueError:
            continue
        if 0 < f < 1:
            out.append(f)
    return tuple(sorted(set(out)))


def preview_steps(total, fractions, min_step=MIN_PREVIEW_STEP, round_to=PREVIEW_ROUND):
    """The steps (sorted, unique) at which a training of `total` steps writes a preview: each fraction of the
    total, rounded to `round_to`, at least `min_step` and before the end."""
    steps = {int(round(f * total / round_to)) * round_to for f in fractions}
    return sorted(s for s in steps if min_step <= s < total)


def preview_name(step, total):
    return f"preview-{step}-of-{total}.ply"


def newest_preview(directory):
    """(step, total, path) of the newest complete preview in `directory`, or None."""
    best = None
    for p in glob.glob(os.path.join(glob.escape(directory or ""), "preview-*.ply")):
        m = PREVIEW.search(p)
        if m and (best is None or int(m.group(1)) > best[0]):
            best = (int(m.group(1)), int(m.group(2)), p)
    return best
