"""Auxiliary video frames and the image-pair list that matches them.

A walk-along video's frames arrive as `photos` whose name starts with FRAME_PREFIX (`vf_0001.jpg`,
... in video order). They exist for coverage only (side and below views): they are trained on, but
never used to align the splat with the wall geometry (that uses the marker photos' solved cameras),
never count towards the photo minimum, and a frame COLMAP cannot register is dropped silently.

Matching them exhaustively is what blows the memory/time budget (160 images = 12720 pairs), so the
pair list is:
- photo x photo: exhaustive (the photos are few and taken far apart);
- frame x frame: each frame with its next `neighbours` frames (a walk is a sequence; no loop detection
  and no vocabulary tree needed: the photos close the loops);
- frame x photo: every `stride`-th frame with every photo (ties the walk to the photos' frame).
"""
FRAME_PREFIX = "vf_"


def is_frame(stem):
    return stem.startswith(FRAME_PREFIX)


def split(stems):
    """(photo stems, frame stems), each sorted (frames in video order)."""
    stems = sorted(stems)
    return [s for s in stems if not is_frame(s)], [s for s in stems if is_frame(s)]


def build_pairs(photos, frames, neighbours=6, stride=4):
    """Image-name pairs (a, b) with a < b, deduplicated, in a stable order. `photos` and `frames` are
    the image names as COLMAP knows them (frames in video order)."""
    pairs = []
    for i, a in enumerate(photos):
        pairs += [(a, b) for b in photos[i + 1:]]
    for i, a in enumerate(frames):
        pairs += [(a, b) for b in frames[i + 1:i + 1 + max(0, neighbours)]]
    for i in range(0, len(frames), max(1, stride)):
        pairs += [(frames[i], p) for p in photos]
    seen, out = set(), []
    for a, b in pairs:
        key = (a, b) if a < b else (b, a)
        if a != b and key not in seen:
            seen.add(key)
            out.append(key)
    return out


def pair_count(n_photos, n_frames, neighbours=6, stride=4):
    """How many pairs build_pairs makes (for the progress label and memory/time estimates)."""
    ff = sum(min(neighbours, n_frames - 1 - i) for i in range(n_frames)) if n_frames else 0
    fp = -(-n_frames // max(1, stride)) * n_photos
    return n_photos * (n_photos - 1) // 2 + ff + fp
