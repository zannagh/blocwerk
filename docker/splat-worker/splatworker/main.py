"""HTTP API: Blocwerk compute job protocol v1 (docker/compute-jobs-protocol.md).

Kinds: `splat` (all-in-one: photos -> splat on this machine), and the split for 3D runners:
`splat-prepare` (photos -> training bundle + prepared.json, CPU only) and `splat-finish` (a runner's
trained scene + prepared.json -> the same files as `splat`, CPU only).
"""
import json
import os
import platform
import shutil

from computejobs.service import ComputeService, bad, callback_url
from computejobs.upload import json_field, text_field
from fastapi import Request

from . import __version__, brush, colmap
from .finish_upload import FinishUpload
from .frames import split
from .options import OptionsError, parse_options, validate_geometry
from .runner import run_job
from .settings import settings
from .upload import SplatUpload

TOOLS = {}
NEEDS = {"splat": ("brush", "colmap"), "splat-prepare": ("colmap",), "splat-finish": ()}
PREPARED_KEYS = ("version", "options", "photoCentres", "registered", "photoStems", "frameStems", "points",
                 "matcher", "cameraGroups", "sfm")


def _probe_tools():
    """Resolve and version the external tools once at startup (details: authenticated /v1/info;
    /health only says "degraded" when a tool this worker's mode needs is missing)."""
    for key, path, probe in (("brush", settings.brush_bin, brush.tool_version),
                             ("colmap", settings.colmap_bin, colmap.tool_version)):
        resolved = shutil.which(path) or (path if os.path.exists(path) else None)
        TOOLS[key] = {"path": resolved, "version": probe(resolved) if resolved else None}
    needed = ("colmap",) if settings.worker_mode == "cpu" else ("brush", "colmap")
    TOOLS["ok"] = all(TOOLS[k]["version"] for k in needed)
    TOOLS["mode"] = settings.worker_mode
    TOOLS["gpuBackend"] = "metal" if platform.system() == "Darwin" else "vulkan"
    if not TOOLS["ok"]:
        service.log.error("splat tools missing: %s (set BRUSH_BIN / COLMAP_BIN)", TOOLS)


def _info_extra():
    return {"tools": TOOLS, "limits": {"maxPhotos": settings.max_photos,
                                       "maxPhotoMb": settings.max_photo_bytes >> 20,
                                       "maxRequestMb": settings.max_request_bytes >> 20,
                                       "maxResultMb": settings.max_result_bytes >> 20,
                                       "timeoutS": settings.splat_timeout_s, "resultTtlS": settings.result_ttl_s}}


def _require_tools(kind):
    if kind == "splat" and settings.worker_mode == "cpu":
        bad("this worker runs in cpu mode (SPLAT_WORKER_MODE=cpu): it serves splat-prepare and "
            "splat-finish only; training runs on a 3D runner", 503)
    missing = [t for t in NEEDS[kind] if TOOLS and not (TOOLS.get(t) or {}).get("version")]
    if kind == "splat" and TOOLS and not TOOLS.get("ok"):
        bad("the splat tools (Brush / COLMAP) are not available on this worker; see /v1/info", 503)
    if missing:
        bad(f"{' / '.join(missing)} not available on this worker; see /v1/info", 503)


async def _photos_job(svc, request: Request, kind):
    """`splat` and `splat-prepare`: the same photos + geometry + options upload."""
    _require_tools(kind)
    job = svc.create_job(kind, None)
    try:
        fields, photos = await SplatUpload(job.dir).read(request)
        stills, frames = split(photos)  # vf_* = auxiliary video frames (frames.py): never aligned, never counted
        try:
            options = parse_options(json_field(fields, "options"))
            geometry = json_field(fields, "geometry")
            if geometry is not None:
                validate_geometry(geometry)
                if not {c["image"] for c in geometry["cameras"]} & set(stills):
                    raise OptionsError("no photo file name matches a camera 'image' of the geometry document")
        except OptionsError as e:
            bad(str(e), 422)
        if len(stills) < settings.min_photos:
            extra = f" and {len(frames)} video frames" if frames else ""
            bad(f"need at least {settings.min_photos} photos (got {len(stills)}{extra})", 422)
        job.callback_url = callback_url(text_field(fields, "callbackUrl"))
        if geometry is not None:
            with open(os.path.join(job.dir, "geometry.json"), "w") as fh:
                json.dump(geometry, fh)
        with open(os.path.join(job.dir, "inputs.json"), "w") as fh:
            json.dump({"photos": photos, "options": options.to_dict()}, fh)
    except BaseException:
        svc.jobs().discard(job)
        raise
    svc.jobs().submit(job)
    return {"jobId": job.id, "status": job.status}


async def _splat(svc, request: Request):
    return await _photos_job(svc, request, "splat")


async def _prepare(svc, request: Request):
    return await _photos_job(svc, request, "splat-prepare")


def _check_prepared(doc):
    if not isinstance(doc, dict) or doc.get("version") != 1 or any(k not in doc for k in PREPARED_KEYS):
        bad("'prepared' is not a prepared.json of this worker version (run splat-prepare again)", 422)
    try:
        parse_options({k: v for k, v in (doc["options"] or {}).items() if v is not None})
        if doc.get("geometry") is not None:
            validate_geometry(doc["geometry"])
    except OptionsError as e:
        bad(f"prepared: {e}", 422)


async def _finish(svc, request: Request):
    _require_tools("splat-finish")
    job = svc.create_job("splat-finish", None)
    try:
        fields, _name = await FinishUpload(job.dir).read(request)
        prepared = json_field(fields, "prepared")
        _check_prepared(prepared)
        stats = json_field(fields, "trainStats")
        with open(os.path.join(job.dir, "prepared.json"), "w") as fh:
            json.dump(prepared, fh)
        with open(os.path.join(job.dir, "trainStats.json"), "w") as fh:
            json.dump(stats if isinstance(stats, dict) else {}, fh)
    except BaseException:
        svc.jobs().discard(job)
        raise
    svc.jobs().submit(job)
    return {"jobId": job.id, "status": job.status}


service = ComputeService(
    name="splat-worker", version=__version__,
    kinds={"splat": _splat, "splat-prepare": _prepare, "splat-finish": _finish}, runner=run_job,
    timeout_for=lambda kind: settings.splat_timeout_s,
    elsewhere={k: f"kind '{k}' is served by wall-geometry, not the splat-worker" for k in ("solve", "textures")},
    info_extra=_info_extra, on_startup=_probe_tools, ready=lambda: not TOOLS or bool(TOOLS.get("ok")))
app = service.app
