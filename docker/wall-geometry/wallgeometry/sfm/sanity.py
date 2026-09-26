"""Geometric sanity of the wall facets, after score.judge (metric frame, mm). A climbing wall is one connected
shell of panels that meet at edges and folds and carry holds; four kinds of planes pass the score without being
one (the first real markerless run: 8 facets instead of 5):

- a duplicate: a near-coplanar slab (<= 5 deg, <= 60 mm) over a bigger accepted facet (the hold layer of the main
  wall, as big as the wall itself), whatever its point count -> "feature on a bigger facet";
- a cut: a plane crossing a bigger accepted facet INSIDE both outlines (>= 150 mm inside the facet's, >= 50 mm
  inside its own) with >= 10 % of its points > 50 mm on either side of it, rather than meeting it at an edge or
  a fold -> "cuts through a bigger facet", unless it carries STRONG hold support (10 % of the hits);
- behind: a near-parallel plane > 150 mm BEHIND a bigger accepted facet (away from the cameras), over its outline
  + 300 mm -> "behind a bigger facet" (the room wall seen past a panel), unless it carries CLEAR hold support;
- a floating plane: no accepted wall plane (or the floor) within 150 mm of its points -> "floating", unless
  it carries CLEAR hold support (a room wall, a rafter, a shelf seen past the wall).

Anchored to an active model, the reference facets are the prior: a plane matching one (normal <= 5 deg, offset
<= 50 mm, >= half of its points over that facet's extent + 150 mm) is preferred (never "floating"; a slab over a
bigger facet that matches ANOTHER reference facet, e.g. a panel 59 mm proud of the main wall, is kept); a plane
matching none is a new surface only with CLEAR hold support (a rebuild can add a panel, but a panel carries holds)
-> "matches no facet of the active model".
"""
import numpy as np
from scipy.spatial import cKDTree

from .gravity import floor_plane
from .planes import plane_ab

STRONG_HOLDS, CLEAR_HOLDS, MIN_HITS = 0.10, 0.03, 10  # share of all hold hits (and a floor of hits)
ADJACENT_MM = 150.0
CUT_MARGIN_MM, CUT_SIDE_MM, CUT_SIDE_SHARE, CUT_MIN_POINTS = 150.0, 50.0, 0.1, 20
PARALLEL_COS = np.cos(np.radians(10.0))
DUP_COS, DUP_MM, DUP_OVER = np.cos(np.radians(5.0)), 60.0, 0.6
REF_COS, REF_MM, REF_OVER, REF_MARGIN_MM = np.cos(np.radians(5.0)), 50.0, 0.5, 150.0
BEHIND_OVER_MM, BEHIND_SHARE = 300.0, 0.3
SAMPLE = 3000


def _sample(Y, pl):
    q = Y[pl["inl"]]
    return q[:: max(1, len(q) // SAMPLE)]


def _inside(pl, ab, margin):
    """Per in-plane point (pl's own (a, b)): inside pl's dense outline by at least `margin` mm."""
    if pl["hull"] is None:
        return np.zeros(len(ab), bool)
    return (ab @ pl["hull"][:, :2].T + pl["hull"][:, 2] <= -margin).all(1)


def holds_ok(pl, level):
    share = pl.get("holdHitShare")
    return share is not None and share >= level and pl.get("holdHits", 0) >= MIN_HITS


def _reject(pl, reason):
    pl["accepted"], pl["reason"] = False, reason


def duplicate_of(pl, big, Y):
    """Is pl a near-coplanar slab lying over the bigger plane big (its hold layer, a second fit of it)?"""
    if abs(pl["n"] @ big["n"]) < DUP_COS or big["hull"] is None:
        return False
    q = _sample(Y, pl)
    h = (q - big["c"]) @ big["n"]
    over = _inside(big, plane_ab(q, big["c"], big["n"]), -50.0)
    return abs(float(np.median(h))) <= DUP_MM and over.mean() > DUP_OVER


def cuts(pl, big, Y):
    """Does pl cross big inside both outlines (not at an edge or a fold)?"""
    if abs(pl["n"] @ big["n"]) > PARALLEL_COS:
        return False
    q = _sample(Y, pl)
    h = (q - big["c"]) @ big["n"]
    if min((h > CUT_SIDE_MM).mean(), (h < -CUT_SIDE_MM).mean()) < CUT_SIDE_SHARE:
        return False  # all on one side of it: an edge or a fold neighbour
    x = q[np.abs(h) < CUT_SIDE_MM]
    if len(x) < CUT_MIN_POINTS:
        return False
    inner = _inside(big, plane_ab(x, big["c"], big["n"]), CUT_MARGIN_MM) \
        & _inside(pl, plane_ab(x, pl["c"], pl["n"]), CUT_SIDE_MM)
    return inner.mean() > 0.5


def behind(pl, big, Y):
    """Is pl a near-parallel plane BEHIND the bigger facet big (away from the cameras) over its outline: the room
    wall seen past a panel, never a surface one climbs on?"""
    if abs(pl["n"] @ big["n"]) < PARALLEL_COS:
        return False
    q = _sample(Y, pl)
    over = _inside(big, plane_ab(q, big["c"], big["n"]), -BEHIND_OVER_MM)
    return float(np.median((q - big["c"]) @ big["n"])) < -ADJACENT_MM and over.mean() >= BEHIND_SHARE


def reference_facets(reference, A, b):
    """The reference document's facets in the METRIC frame (world = A @ Y + b): {"id", "o", "u", "v", "n", "e"}."""
    out = []
    for seg in reference.get("segments", []):
        for f in seg.get("facets", []):
            o = A.T @ (np.array(f["origin"], float) - b)
            out.append({"id": str(f["id"]), "o": o, "u": A.T @ np.array(f["u"], float),
                        "v": A.T @ np.array(f["v"], float), "n": A.T @ np.array(f["normal"], float),
                        "e": f["extentMm"]})
    return out


def match_reference(pl, refs, Y):
    """The id of the reference facet pl lies on (the one covering most of its points), or None."""
    q = _sample(Y, pl)
    best, best_over = None, REF_OVER
    for f in refs:
        if abs(pl["n"] @ f["n"]) < REF_COS:
            continue
        rel = q - f["o"]
        if abs(float(np.median(rel @ f["n"]))) > REF_MM:
            continue
        a, bb, e, m = rel @ f["u"], rel @ f["v"], f["e"], REF_MARGIN_MM
        over = ((a > e["aMin"] - m) & (a < e["aMax"] + m) & (bb > e["bMin"] - m) & (bb < e["bMax"] + m)).mean()
        if over >= best_over:
            best, best_over = f["id"], over
    return best


def _near(tree, pts, gap):
    return tree.query(pts, distance_upper_bound=gap)[0].min() < gap


def _on_floor(pl, Y, floor):
    if floor is None:
        return False
    h = (_sample(Y, pl) - floor["c"]) @ floor["n"]
    return float(np.percentile(np.abs(h), 1)) <= ADJACENT_MM


def check(planes, Y, up, cams_mm, spread_mm, refs=None):
    """Applies the rules to the accepted planes (biggest first); sets refFacet on the planes when anchored."""
    order = sorted((p for p in planes if p["accepted"]), key=lambda p: -p["npts"])
    for pl in order:
        pl["refFacet"] = match_reference(pl, refs, Y) if refs else None
    kept = []
    for pl in order:
        big = next((k for k in kept if duplicate_of(pl, k, Y)), None)
        if big is not None and (not refs or pl["refFacet"] in (None, big["refFacet"])):
            _reject(pl, "feature on a bigger facet")
        elif any(cuts(pl, k, Y) for k in kept) and not holds_ok(pl, STRONG_HOLDS):
            _reject(pl, "cuts through a bigger facet")
        elif any(behind(pl, k, Y) for k in kept) and not holds_ok(pl, CLEAR_HOLDS):
            _reject(pl, "behind a bigger facet")
        elif refs and pl["refFacet"] is None and not holds_ok(pl, CLEAR_HOLDS):
            _reject(pl, "matches no facet of the active model")
        else:
            kept.append(pl)
    _floating(kept, Y, floor_plane(planes, Y, up, cams_mm, spread_mm), bool(refs))
    return planes


def _floating(kept, Y, floor, anchored):
    """Keeps what connects to the biggest facet through planes within ADJACENT_MM (or the floor); the rest
    needs clear hold support (or, anchored, a reference facet)."""
    if not kept:
        return
    trees = [cKDTree(_sample(Y, p)) for p in kept]
    free = lambda i: (anchored and kept[i]["refFacet"] is not None) or holds_ok(kept[i], CLEAR_HOLDS) \
        or _on_floor(kept[i], Y, floor)
    joined = {0} | {i for i in range(1, len(kept)) if free(i)}
    grown = True
    while grown:
        grown = False
        for i in range(len(kept)):
            if i not in joined and any(_near(trees[j], _sample(Y, kept[i]), ADJACENT_MM) for j in joined):
                joined.add(i)
                grown = True
    for i, pl in enumerate(kept):
        if i not in joined:
            _reject(pl, "floating (no wall surface within 150 mm, no holds)")
