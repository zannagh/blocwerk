"""Blocwerk wall-geometry solver: ArUco marker observations -> metric 3D wall geometry."""
__version__ = "0.1.0"


def document_from_solution(sol, progress=None):
    """Solved (gravity applied) solution -> wall-geometry document."""
    from .export import build_document, to_world
    from .validation import distortion, leave_one_out, per_image_marker_rms, side_check

    progress = progress or (lambda *_: None)
    to_world(sol)
    per_img, per_mk = per_image_marker_rms(sol)
    checks = {"per_image": per_img, "per_marker": per_mk, "side": side_check(sol),
              "distortion": distortion(sol)}
    if sol["req"].options.get("validate"):
        checks["loo"] = leave_one_out(sol, lambda f, s: progress(0.85 + 0.14 * f, s))
    progress(0.99, "writing document")
    return build_document(sol, checks)


def _solve_once(request_doc, progress, limits):
    from .request import parse_request
    from .solver import solve

    req = parse_request(request_doc, limits)
    sol = solve(req, progress)
    return document_from_solution(sol, progress), sol


def solve_document(request_doc, progress=None, limits=None):
    """Request JSON (dict) -> (wall-geometry document dict, internal solution). Raises RequestError.

    An implausible model is solved again without its worst offenders (plausible.py), up to MAX_ROUNDS times; the most
    plausible result wins. Then a marker whose photos disagree so much that it drags the others is left out and all
    of that is done again without it (isolate.py). Both list what they dropped in quality.rejectedObservations.
    """
    from . import isolate

    progress = progress or (lambda *_: None)

    def solve_full(req):
        doc, sol = _solve_once(req, progress, limits)
        if not req.get("options", {}).get("plausibilityRounds", True):
            return doc, sol
        return _plausible(doc, sol, req, progress, limits)

    doc, sol, isolated = isolate.run(*solve_full(request_doc), request_doc, solve_full, progress)
    if isolated:
        q = doc["quality"]
        q["rejectedObservations"] = list(q.get("rejectedObservations") or []) + isolated
    return doc, sol


def _plausible(doc, sol, request_doc, progress, limits):
    from . import isolate, plausible

    why = plausible.problems(doc)
    best, first_why, dropped, records = (doc, sol, why, []), why, {}, []
    for round_no in range(1, plausible.MAX_ROUNDS + 1):
        if not why:
            break
        pick = plausible.offenders(doc, sol, request_doc, dropped)
        if not pick:
            break
        dropped.update(pick)
        records += [plausible.record(k, r, why, round_no) for k, r in pick.items()]
        progress(0.05, f"model implausible ({why[0]}); solving again without {len(dropped)} detection(s)")
        try:
            doc, sol = _solve_once(plausible.without(request_doc, dropped), progress, limits)
        except isolate.TRIAL_ERRORS:
            break  # a retry that fails numerically keeps the best model so far
        why = plausible.problems(doc)
        if len(why) < len(best[2]) or (len(why) == len(best[2]) and not why):
            best = (doc, sol, why, list(records))
    doc, sol, why, kept = best
    if first_why:
        q = doc["quality"]
        q["rejectedObservations"] = list(q.get("rejectedObservations") or []) + kept
        q["plausibility"] = {"problemsFirstSolve": first_why, "problems": why, "droppedDetections": len(kept)}
    return doc, sol
