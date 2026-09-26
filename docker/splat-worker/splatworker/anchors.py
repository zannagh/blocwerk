"""Anchor photos (kind `splat-prepare` only): photos of the wall's ACTIVE capture sent along with a new one.

They arrive as `photos` named `a00.jpg`, `a01.jpg`, ... (ANCHOR). COLMAP extracts, matches and maps them like
photos, so the new reconstruction contains cameras whose wall-frame poses the app already knows; their camera
centres go to prepared.json `anchorCentres` and sparse.zip (wall-geometry `solve-sfm` fits the new model to the
wall frame on them). Then `image_deleter` removes them from the model BEFORE undistortion and the training
bundle: anchor pixels never reach training, and they never count towards the photo minimum.
"""
import re

ANCHOR = re.compile(r"^a\d{2,4}$")


def is_anchor(stem):
    return bool(ANCHOR.match(stem))


def split_anchors(stems):
    """(the other stems, the anchor stems), each sorted."""
    stems = sorted(stems)
    return [s for s in stems if not is_anchor(s)], [s for s in stems if is_anchor(s)]
