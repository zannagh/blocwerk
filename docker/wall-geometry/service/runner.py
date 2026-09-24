"""Job bodies. Each job runs in its own child process so a timeout or cancel can kill it outright.

Pipe protocol and process-group handling: computejobs.jobs / computejobs.child.
"""
import json
import os

from computejobs.child import run_in_child


def _solve(job_dir, progress):
    from wallgeometry import solve_document
    with open(os.path.join(job_dir, "request.json")) as fh:
        req = json.load(fh)
    doc, _ = solve_document(req, progress)
    with open(os.path.join(job_dir, "wall-geometry.json"), "w") as fh:
        json.dump(doc, fh)
    return {"geometry": doc, "files": ["wall-geometry.json"]}


def _textures(job_dir, progress):
    from .settings import settings
    # OpenCV's own decode limit, as a second line behind the header check on arrival
    os.environ["OPENCV_IO_MAX_IMAGE_PIXELS"] = str(settings.max_image_pixels)
    import cv2

    from wallgeometry.sourcemap import encode as encode_source
    from wallgeometry.textures import TextureError, encode_jpeg, encode_png, render_textures
    with open(os.path.join(job_dir, "geometry.json")) as fh:
        doc = json.load(fh)
    with open(os.path.join(job_dir, "inputs.json")) as fh:
        inputs = json.load(fh)
    photos = inputs["photos"]  # name -> stored file name

    def load(name):
        # raw pixel grid: metadata (incl. orientation) was stripped on arrival, as the app does
        img = cv2.imread(os.path.join(job_dir, photos[name]), cv2.IMREAD_COLOR | cv2.IMREAD_IGNORE_ORIENTATION)
        if img is None:
            raise TextureError(f"photo {name} could not be decoded")
        return img

    params = inputs.get("options") or {}
    render_params = {**params, "blendMaxBytes": settings.textures_blend_max_bytes}
    res = render_textures(doc, load, set(photos), render_params, lambda f, s: progress(0.05 + 0.9 * f, s))
    facets, files = [], []
    for r in res:
        name = f"facet_{r['facet']}.jpg"
        with open(os.path.join(job_dir, name), "wb") as fh:
            fh.write(encode_jpeg(r["image"], params.get("jpegQuality", 90)))
        mask_name = f"facet_{r['facet']}_mask.png"
        with open(os.path.join(job_dir, mask_name), "wb") as fh:
            fh.write(encode_png(r["mask"]))
        source_name = f"facet_{r['facet']}_source.json"
        with open(os.path.join(job_dir, source_name), "wb") as fh:
            fh.write(encode_source(r["source"]))
        files += [name, mask_name, source_name]
        facets.append({k: v for k, v in r.items() if k not in ("image", "mask", "source")}
                      | {"file": name, "maskFile": mask_name, "sourceFile": source_name})
    manifest = {"pixelConvention": "column i, row j -> a = aMin + (i + 0.5) * mmPerPx, "
                                   "b = bMax - (j + 0.5) * mmPerPx (facet frame of the geometry); maskFile: same "
                                   "grid, 8-bit gray, 0 = no photo there, 255 = photo, feathered edge; sourceFile: which photo "
                                   "painted each label cell (wallgeometry/sourcemap.py)",
                "facets": facets}
    with open(os.path.join(job_dir, "textures.json"), "w") as fh:
        json.dump(manifest, fh)
    return {**manifest, "files": files + ["textures.json"]}


KINDS = {"solve": _solve, "textures": _textures}


def run_job(kind, job_dir, conn):
    from wallgeometry.request import RequestError
    from wallgeometry.textures import TextureError
    run_in_child(KINDS[kind], job_dir, conn, (RequestError, TextureError))
