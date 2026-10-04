"""The body of a `textures` job on a job directory: what the wall-geometry service runs in its child process, and
what a 3D runner (docker/splat-worker, gpurunner/textures) runs on the machine with more memory.

Inputs in `job_dir`: `geometry.json` (the solved document), `inputs.json` ({"photos": {camera name: file in job_dir},
"options": validated client options}) and the photo files. Outputs next to them: `facet_<id>.jpg`,
`facet_<id>_mask.png`, `facet_<id>_source.json` and `textures.json` (the manifest the app reads). One implementation,
so a render on a runner is the render of the service, bit for bit.
"""
import json
import os

MANIFEST = "textures.json"
PIXEL_CONVENTION = ("column i, row j -> a = aMin + (i + 0.5) * mmPerPx, "
                    "b = bMax - (j + 0.5) * mmPerPx (facet frame of the geometry); maskFile: same "
                    "grid, 8-bit gray, 0 = no photo there, 255 = photo, feathered edge; sourceFile: which photo "
                    "painted each label cell (wallgeometry/sourcemap.py)")


def render_job(job_dir, progress, blend_max_bytes, max_image_pixels):
    """Renders the job dir's textures; returns the manifest (+ "files": every file written, the manifest last).

    progress(fraction, stage) is called from 0.05 to 0.95; blend_max_bytes: the multi-view blend's memory budget
    (`blendMaxBytes`, wallgeometry.textures.blend_bytes); max_image_pixels: OpenCV's decode limit per photo."""
    # OpenCV's own decode limit, as a second line behind the header check on arrival
    os.environ["OPENCV_IO_MAX_IMAGE_PIXELS"] = str(max_image_pixels)
    import cv2

    from .sourcemap import encode as encode_source
    from .textures import TextureError, encode_jpeg, encode_png, render_textures
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
    render_params = {**params, "blendMaxBytes": blend_max_bytes}
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
    manifest = {"pixelConvention": PIXEL_CONVENTION, "facets": facets}
    with open(os.path.join(job_dir, MANIFEST), "w") as fh:
        json.dump(manifest, fh)
    return {**manifest, "files": files + [MANIFEST]}
