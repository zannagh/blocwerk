"""HTTP API: Blocwerk compute job protocol v1 (docker/compute-jobs-protocol.md), kind `splat`."""
import json
import os
import platform
import shutil

from computejobs.service import ComputeService, bad, callback_url
from computejobs.upload import json_field, text_field
from fastapi import Request

from . import __version__, brush, colmap, gpu, gsplat_trainer, trainers
from .frames import split
from .options import OptionsError, parse_options, validate_geometry
from .runner import run_job
from .settings import settings
from .upload import SplatUpload

TOOLS = {}


def _probe_tools():
    """Resolve and version the external tools once at startup (details: authenticated /v1/info;
    /health only says "degraded" when they are missing). The trainer is SPLAT_TRAINER's (trainers.py)."""
    try:
        trainer = trainers.select()
    except ValueError as e:
        TOOLS.update(ok=False, error=str(e))
        service.log.error("%s", e)
        return
    probe = ("gsplat", settings.gsplat_python, gsplat_trainer.tool_version) if trainer == "gsplat" else \
        ("brush", settings.brush_bin, brush.tool_version)
    for key, path, version in (probe, ("colmap", settings.colmap_bin, colmap.tool_version)):
        resolved = shutil.which(path) or (path if os.path.exists(path) else None)
        TOOLS[key] = {"path": resolved, "version": version(resolved) if resolved else None}
    TOOLS["trainer"] = trainer
    TOOLS["ok"] = all(TOOLS[k]["version"] for k in (trainer, "colmap"))
    if trainer == "gsplat":
        TOOLS["gpuBackend"], TOOLS["gpu"] = "cuda", gpu.vram()
    else:
        TOOLS["gpuBackend"] = "metal" if platform.system() == "Darwin" else "vulkan"
    if not TOOLS["ok"]:
        service.log.error("splat tools missing: %s (set BRUSH_BIN or GSPLAT_PYTHON / COLMAP_BIN; gsplat also "
                          "needs a CUDA device)", TOOLS)


def _info_extra():
    return {"tools": TOOLS, "limits": {"maxPhotos": settings.max_photos,
                                       "maxPhotoMb": settings.max_photo_bytes >> 20,
                                       "maxRequestMb": settings.max_request_bytes >> 20,
                                       "timeoutS": settings.splat_timeout_s, "resultTtlS": settings.result_ttl_s}}


async def _splat(svc, request: Request):
    if TOOLS and not TOOLS.get("ok"):
        bad("the splat tools (trainer / COLMAP) are not available on this worker; see /v1/info", 503)
    job = svc.create_job("splat", None)
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


service = ComputeService(
    name="splat-worker", version=__version__, kinds={"splat": _splat}, runner=run_job,
    timeout_for=lambda kind: settings.splat_timeout_s,
    elsewhere={k: f"kind '{k}' is served by wall-geometry, not the splat-worker" for k in ("solve", "textures")},
    info_extra=_info_extra, on_startup=_probe_tools, ready=lambda: not TOOLS or bool(TOOLS.get("ok")))
app = service.app
