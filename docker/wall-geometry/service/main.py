"""HTTP API: Blocwerk compute job protocol v1 (docker/compute-jobs-protocol.md), kinds solve + textures.

The protocol machinery (queue, auth, callbacks, limits, status/files/cancel, streaming upload) is the
shared package docker/compute-jobs-py; this module only parses the two kinds' requests.
"""
import json
import os

from computejobs.geometry import GeometryError, check_geometry
from computejobs.service import ComputeService, bad, callback_url, loads_strict
from computejobs.upload import PhotoRejected, StreamingUpload, json_field, text_field
from fastapi import Request

from wallgeometry import __version__
from wallgeometry.request import RequestError, parse_request
from wallgeometry.textures import TextureError, output_pixels, validate_params

from .photos import BadPhoto, strip
from .runner import run_job
from .settings import settings

PHOTO_EXT = {".jpg", ".jpeg", ".png"}  # sniffed from the bytes; the extension only has to be one of these
TEXTURE_FIELDS = {"geometry", "options", "callbackUrl"}
SOLVE_MAX_BYTES = 32 << 20  # a solve request is marker coordinates only: a few hundred KB in practice


async def _read_capped(request, limit):
    body = bytearray()
    async for chunk in request.stream():
        body += chunk
        if len(body) > limit:
            bad(f"solve request exceeds {limit >> 20} MB", 413)
    return bytes(body)


async def _solve(svc, request: Request):
    raw = await _read_capped(request, SOLVE_MAX_BYTES)
    try:
        doc = loads_strict(raw)
    except (ValueError, UnicodeDecodeError, RecursionError):
        bad("body must be JSON (the solve request document)")
    if not isinstance(doc, dict):
        bad("body must be a JSON object")
    cb = callback_url(doc.pop("callbackUrl", None))
    try:  # validate now so the client gets a 422 instead of a failed job
        parse_request(doc, {"max_photos": settings.max_photos})
    except RequestError as e:
        bad(str(e), 422)

    def write(d):
        with open(os.path.join(d, "request.json"), "w") as fh:
            json.dump(doc, fh)
    return await svc.enqueue("solve", write, cb)


def _photo_writer(job_dir):
    """process_photo hook: strip metadata (no re-encode), refuse oversized images, write."""
    def store(stem, _ext, raw):
        try:
            kind, clean, (w, h) = strip(raw)
        except BadPhoto as e:
            raise PhotoRejected(422, str(e)) from e
        if w * h > settings.max_image_pixels:
            raise PhotoRejected(413, f"{w}x{h} exceeds {settings.max_image_pixels // 1_000_000} MP")
        name = f"photo_{stem}.{'png' if kind == 'png' else 'jpg'}"
        with open(os.path.join(job_dir, name), "wb") as fh:
            fh.write(clean)
        return {"file": name, "width": w, "height": h}
    return store


def _check_textures(geometry, options, photos):
    """-> (clean options, {camera name: stored file}) or a 4xx."""
    try:
        check_geometry(geometry, textures=True)
        params = validate_params(options)
    except (GeometryError, TextureError) as e:
        bad(str(e), 422)
    budget = settings.textures_max_pixels
    if output_pixels(geometry, params) > budget:
        bad(f"the textures would exceed {budget // 1_000_000} MP in total: raise mmPerPx or lower "
            "maxSidePx / extraMarginMm", 422)
    if not photos:
        bad("no 'photos' files uploaded")
    cams = {c["image"]: c for c in geometry["cameras"]}
    used = {k: v for k, v in photos.items() if k in cams}  # extra photos are ignored
    if not used:
        bad("no uploaded photo name matches a camera 'image' of the geometry document", 422)
    for name, p in used.items():
        c = cams[name]
        if (p["width"], p["height"]) != (c["width"], c["height"]):
            bad(f"photo {name} is {p['width']}x{p['height']} but was solved as {c['width']}x{c['height']}", 422)
    return params, {k: v["file"] for k, v in used.items()}


async def _textures(svc, request: Request):
    m = svc.jobs()
    job = svc.create_job("textures", None)
    try:
        upload = StreamingUpload(fields=TEXTURE_FIELDS, photo_ext=PHOTO_EXT,
                                 process_photo=_photo_writer(job.dir), kind="textures")
        fields, photos = await upload.read(request)
        geometry = json_field(fields, "geometry")
        if geometry is None:
            bad("'geometry' (a solved wall-geometry document) is required", 422)
        params, stored = _check_textures(geometry, json_field(fields, "options"), photos)
        job.callback_url = callback_url(text_field(fields, "callbackUrl"))
        for p in photos.values():
            if p["file"] not in stored.values():
                os.remove(os.path.join(job.dir, p["file"]))
        with open(os.path.join(job.dir, "geometry.json"), "w") as fh:
            json.dump(geometry, fh)
        with open(os.path.join(job.dir, "inputs.json"), "w") as fh:
            json.dump({"photos": stored, "options": params}, fh)
    except BaseException:
        m.discard(job)
        raise
    m.submit(job)
    return {"jobId": job.id, "status": job.status}


def _timeout(kind):
    return settings.solve_timeout_s if kind == "solve" else settings.textures_timeout_s


service = ComputeService(
    name="wall-geometry", version=__version__, kinds={"solve": _solve, "textures": _textures},
    runner=run_job, timeout_for=_timeout,
    elsewhere={"splat": "kind 'splat' is not served here: it is implemented by the separate splat-worker "
                        "(same protocol, see compute-jobs-protocol.md)"},
    info_extra=lambda: {"limits": {"maxPhotos": settings.max_photos,
                                   "maxPhotoMb": settings.max_photo_bytes >> 20,
                                   "maxRequestMb": settings.max_request_bytes >> 20,
                                   "maxImageMegapixels": settings.max_image_pixels // 1_000_000,
                                   "texturesMaxMegapixels": settings.textures_max_pixels // 1_000_000}})
app = service.app
