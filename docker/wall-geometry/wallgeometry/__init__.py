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


def solve_document(request_doc, progress=None, limits=None):
    """Request JSON (dict) -> (wall-geometry document dict, internal solution). Raises RequestError."""
    from .request import parse_request
    from .solver import solve

    req = parse_request(request_doc, limits)
    sol = solve(req, progress or (lambda *_: None))
    return document_from_solution(sol, progress), sol
