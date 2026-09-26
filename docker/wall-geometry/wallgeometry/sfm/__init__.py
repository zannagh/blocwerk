"""Wall geometry from a feature reconstruction (no markers): kind `solve-sfm` (README "Solve from features").

colmap_io (sparse.zip) -> planes (sequential RANSAC + IRLS) -> anchors / scale / gravity chains -> wall-facet
decisions (score) -> world frame and facets (world) -> the geometry document v1 (export).
"""


def solve_sfm_document(request_doc, model_dir, progress=None, stems=None):
    """Request JSON (dict) + an unpacked sparse model dir -> (document, internal solution).
    Raises RequestError / ModelError / SfmError (messages safe for the client)."""
    from .colmap_io import load_model
    from .export import build_sfm_document
    from .request import parse_sfm_request
    from .solve import solve_sfm

    progress = progress or (lambda *_: None)
    req = parse_sfm_request(request_doc)
    progress(0.02, "reading the model")
    model = load_model(model_dir, stems)
    sol = solve_sfm(req, model, progress)
    progress(0.95, "writing document")
    return build_sfm_document(sol), sol
