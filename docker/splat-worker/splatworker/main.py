"""HTTP API: Blocwerk compute job protocol v1 (docker/compute-jobs-protocol.md).

Kinds: `splat` (all-in-one: photos -> splat on this machine) and the split for 3D runners (split_api.py):
`splat-prepare` (photos [+ anchor photos] -> training bundle + prepared.json + sparse.zip) and
`splat-finish` (a runner's trained scene + prepared.json -> the same files as `splat`), both CPU only.
SPLAT_WORKER_MODE=cpu serves the split only and needs no GPU and no trainer.
"""
import json
import os
import platform
import shutil

from computejobs.service import ComputeService, bad, callback_url
from computejobs.upload import json_field, text_field
from fastapi import Request

from . import __version__, brush, colmap, gpu, gsplat_trainer, split_api, trainers
from .anchors import split_anchors
from .frames import split
from .options import OptionsError, parse_options, validate_geometry
from .runner import run_job
from .settings import settings
from .upload import SplatUpload

TOOLS = {}
PREPARE_OUTPUTS = ("sparse.zip", "anchors")


def _probe(key, path, version):
    resolved = shutil.which(path) or (path if os.path.exists(path) else None)
    TOOLS[key] = {"path": resolved, "version": version(resolved) if resolved else None}


def _probe_tools():
    """Resolve and version the external tools once at startup (details: authenticated /v1/info;
    /health only says "degraded" when a tool this worker's role needs is missing). The trainer is
    SPLAT_TRAINER's (trainers.py); SPLAT_WORKER_MODE=cpu needs COLMAP only (no trainer, no GPU)."""
    TOOLS["mode"] = settings.worker_mode
    if settings.worker_mode not in split_api.MODES:
        TOOLS.update(ok=False, error=f"SPLAT_WORKER_MODE={settings.worker_mode!r} is not one of {split_api.MODES}")
        service.log.error("%s", TOOLS["error"])
        return
    if settings.worker_mode == "cpu":
        _probe("colmap", settings.colmap_bin, colmap.tool_version)
        TOOLS.update(trainer=None, ok=bool(TOOLS["colmap"]["version"]))
        if not TOOLS["ok"]:
            service.log.error("COLMAP missing: %s (set COLMAP_BIN)", TOOLS)
        return
    try:
        trainer = trainers.select()
    except ValueError as e:
        TOOLS.update(ok=False, error=str(e))
        service.log.error("%s", e)
        return
    probe = ("gsplat", settings.gsplat_python, gsplat_trainer.tool_version) if trainer == "gsplat" else \
        ("brush", settings.brush_bin, brush.tool_version)
    for key, path, version in (probe, ("colmap", settings.colmap_bin, colmap.tool_version)):
        _probe(key, path, version)
    TOOLS["trainer"] = trainer
    TOOLS["ok"] = all(TOOLS[k]["version"] for k in (trainer, "colmap"))
    TOOLS["maxQuality"] = "max"
    if trainer == "gsplat":
        TOOLS["gpuBackend"], TOOLS["gpu"] = "cuda", gpu.vram()
        if ((TOOLS["gpu"] or {}).get("totalMb") or 0) >= 0.98 * trainers.ULTRA_MIN_VRAM_MB:
            TOOLS["maxQuality"] = "ultra"
    else:
        TOOLS["gpuBackend"] = "metal" if platform.system() == "Darwin" else "vulkan"
    if not TOOLS["ok"]:
        service.log.error("splat tools missing: %s (set BRUSH_BIN or GSPLAT_PYTHON / COLMAP_BIN; gsplat also "
                          "needs a CUDA device)", TOOLS)


def _health_extra():
    """maxQuality on /health: the app offers Ultra without a 3D runner when this worker trains it.
    prepareOutputs: what splat-prepare returns and accepts beyond the bundle (the app keys markerless captures
    on it): sparse.zip for solve-sfm, and anchor photos."""
    extra = {"prepareOutputs": list(PREPARE_OUTPUTS)}
    if TOOLS.get("ok") and TOOLS.get("maxQuality"):
        extra["maxQuality"] = TOOLS["maxQuality"]
    return extra


def _info_extra():
    return {"tools": TOOLS, "limits": {"maxPhotos": settings.max_photos,
                                       "maxPhotoMb": settings.max_photo_bytes >> 20,
                                       "maxRequestMb": settings.max_request_bytes >> 20,
                                       "maxResultMb": settings.max_result_bytes >> 20,
                                       "timeoutS": settings.splat_timeout_s, "resultTtlS": settings.result_ttl_s}}


async def _photos_job(svc, request: Request, kind):
    """`splat` and `splat-prepare`: the same photos + geometry + options upload."""
    split_api.require_tools(kind, TOOLS)
    job = svc.create_job(kind, None)
    try:
        fields, photos = await SplatUpload(job.dir).read(request)
        stills, frames = split(photos)  # vf_* = auxiliary video frames (frames.py): never aligned, never counted
        stills, anchors = split_anchors(stills)  # a00, a01, ... = anchor photos (anchors.py): prepare only
        if anchors and kind != "splat-prepare":
            bad(f"anchor photos ({', '.join(anchors[:3])}, ...) are only accepted by splat-prepare", 422)
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
            json.dump({"photos": photos, "anchors": anchors, "options": options.to_dict()}, fh)
    except BaseException:
        svc.jobs().discard(job)
        raise
    svc.jobs().submit(job)
    return {"jobId": job.id, "status": job.status}


async def _splat(svc, request: Request):
    return await _photos_job(svc, request, "splat")


async def _prepare(svc, request: Request):
    return await _photos_job(svc, request, "splat-prepare")


async def _finish(svc, request: Request):
    return await split_api.finish_job(svc, request, TOOLS)


service = ComputeService(
    name="splat-worker", version=__version__,
    kinds={"splat": _splat, "splat-prepare": _prepare, "splat-finish": _finish}, runner=run_job,
    timeout_for=lambda kind: settings.splat_timeout_s,
    elsewhere={k: f"kind '{k}' is served by wall-geometry, not the splat-worker" for k in ("solve", "solve-sfm", "textures")},
    info_extra=_info_extra, on_startup=_probe_tools, ready=lambda: not TOOLS or bool(TOOLS.get("ok")),
    health_extra=_health_extra)
app = service.app
