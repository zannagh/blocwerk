"""Job bodies. Each job runs in its own child process so a timeout or cancel can kill it outright.

Pipe protocol and process-group handling: computejobs.jobs / computejobs.child.
"""
import json
import os

from computejobs.child import run_in_child


def _solve(job_dir, progress):
    from wallgeometry import solve_document

    from .settings import settings
    with open(os.path.join(job_dir, "request.json")) as fh:
        req = json.load(fh)
    doc, _ = solve_document(req, progress, {"max_photos": settings.max_photos})
    with open(os.path.join(job_dir, "wall-geometry.json"), "w") as fh:
        json.dump(doc, fh)
    return {"geometry": doc, "files": ["wall-geometry.json"]}


def _textures(job_dir, progress):
    from wallgeometry.jobrender import render_job

    from .settings import settings
    return render_job(job_dir, progress, settings.textures_blend_max_bytes, settings.max_image_pixels)


def _solve_sfm(job_dir, progress):
    from .sfm import run_solve_sfm
    return run_solve_sfm(job_dir, progress)


KINDS = {"solve": _solve, "solve-sfm": _solve_sfm, "textures": _textures}


def run_job(kind, job_dir, conn):
    from wallgeometry.request import RequestError
    from wallgeometry.sfm.colmap_io import ModelError
    from wallgeometry.sfm.solve import SfmError
    from wallgeometry.textures import TextureError
    run_in_child(KINDS[kind], job_dir, conn, (RequestError, TextureError, ModelError, SfmError))
