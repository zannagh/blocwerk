"""HTTP surface of the wall-stitch sidecar.

The contract is fixed by the .NET client in src/Blocwerk.Core/Stitching: camelCase
JSON, bearer auth on everything except /healthz, and asynchronous jobs.
"""
from __future__ import annotations

import hmac
import json
import logging
import os
from typing import List, Optional

from fastapi import APIRouter, Depends, FastAPI, Header, HTTPException, Request
from fastapi.responses import FileResponse, JSONResponse
# starlette's own class: fastapi.UploadFile is a subclass, so isinstance against the
# fastapi one misses the parts starlette actually puts in the form.
from starlette.datastructures import UploadFile

from . import ingest
from .config import Settings, load_settings
from .errors import MESSAGES
from .imaging import content_type_for
from .models import JobCreated, JobOptions, JobState
from .runner import JobContext, PipelineRunner, SubprocessPipelineRunner
from .store import JobStore, is_valid_job_id, new_job_id
from .worker import JobQueueFull, JobWorkerPool, TtlReaper

logging.basicConfig(level=os.environ.get("WALLSTITCH_LOG_LEVEL", "INFO"))
log = logging.getLogger("wallstitch")

ARTIFACTS = {"ortho.png", "angled.png", "display-ortho.jpg", "display-angled.jpg"}


def create_app(settings: Optional[Settings] = None,
               runner: Optional[PipelineRunner] = None) -> FastAPI:
    settings = settings or load_settings()
    store = JobStore(settings.data_dir)
    pool = JobWorkerPool(store, runner or SubprocessPipelineRunner(settings),
                         settings.workers, settings.queue_limit)
    reaper = TtlReaper(store, settings.job_ttl_seconds, settings.reaper_interval_seconds)

    app = FastAPI(title="Blocwerk wall-stitch sidecar", docs_url=None, redoc_url=None)
    app.state.settings = settings
    app.state.store = store
    app.state.pool = pool
    app.state.reaper = reaper

    @app.on_event("startup")
    def _startup() -> None:
        orphans = store.fail_orphans("interrupted", MESSAGES["interrupted"])
        if orphans:
            log.warning("marked %d interrupted job(s) as failed after restart", orphans)
        pool.start()
        reaper.start()

    @app.on_event("shutdown")
    def _shutdown() -> None:
        reaper.stop()
        pool.stop()

    app.include_router(_public_router())
    app.include_router(_jobs_router(), dependencies=[Depends(_require_bearer)])
    return app


# ---- auth --------------------------------------------------------------------

async def _require_bearer(request: Request, authorization: str = Header(default="")) -> None:
    expected = request.app.state.settings.auth_token
    scheme, _, token = (authorization or "").partition(" ")
    if scheme.lower() != "bearer" or not hmac.compare_digest(token.strip(), expected):
        raise HTTPException(status_code=401, detail="unauthorized",
                            headers={"WWW-Authenticate": "Bearer"})


# ---- routes ------------------------------------------------------------------

def _public_router() -> APIRouter:
    router = APIRouter()

    @router.get("/healthz")
    def healthz(request: Request) -> JSONResponse:
        state = request.app.state
        checks = {"pipelineImports": _pipeline_imports(), "dataDirWritable": _writable(state.settings.data_dir)}
        ready = all(checks.values())
        return JSONResponse(
            status_code=200 if ready else 503,
            content={"status": "ok" if ready else "degraded", "ready": ready,
                     "checks": checks, "queueDepth": state.pool.depth,
                     "workers": state.settings.workers})

    return router


def _jobs_router() -> APIRouter:
    router = APIRouter(prefix="/jobs")

    @router.post("", status_code=202)
    @router.post("/", status_code=202, include_in_schema=False)
    async def create_job(request: Request) -> JSONResponse:
        settings: Settings = request.app.state.settings
        store: JobStore = request.app.state.store
        # The form is read by hand rather than through declared parameters: the .NET client
        # sends `options` as a plain field while browsers and curl send it as a named file
        # part, and a declared `Form(str)` rejects the latter with an undocumented 422.
        form = await request.form(max_files=settings.max_photos + 4, max_fields=32)
        photos = [v for v in form.getlist("photos") if isinstance(v, UploadFile)]
        old_photo = form.get("oldPhoto")
        oldPhoto = old_photo if isinstance(old_photo, UploadFile) else None
        parsed = _parse_options(await _text_part(form.get("options")))
        _validate_counts(photos, settings)
        if parsed.transfer_holds and oldPhoto is None:
            _bad("old_photo_required", "transferHolds was requested but no oldPhoto was uploaded.")

        job_id = new_job_id()
        store.create(job_id, parsed.model_dump(mode="json"))
        try:
            saved, old_path = await _land_uploads(store, job_id, photos, oldPhoto, settings)
        except ingest.UploadRejected as rejected:
            store.delete(job_id)
            _bad(rejected.code, rejected.message)

        job = JobContext(job_id, store.job_dir(job_id), parsed, saved, old_path)
        try:
            request.app.state.pool.submit(job)
        except JobQueueFull:
            store.delete(job_id)
            raise HTTPException(status_code=503, detail={
                "code": "queue_full",
                "message": "The stitching service is busy. Try again in a few minutes."})

        return JSONResponse(status_code=202,
                            content=JobCreated(job_id=job_id, status="queued").model_dump(by_alias=True))

    @router.get("/{job_id}")
    def get_job(request: Request, job_id: str) -> JSONResponse:
        record = _load(request, job_id)
        state = JobState(job_id=record["jobId"], status=record["status"],
                         progress=float(record.get("progress") or 0.0),
                         stage=record.get("stage"), error=record.get("error"),
                         result=record.get("result"))
        return JSONResponse(content=state.model_dump(by_alias=True))

    @router.get("/{job_id}/artifacts/{name}")
    def get_artifact(request: Request, job_id: str, name: str) -> FileResponse:
        record = _load(request, job_id)
        if name not in ARTIFACTS or os.path.basename(name) != name:
            raise HTTPException(status_code=404, detail="unknown artifact")
        path = os.path.join(request.app.state.store.artifact_dir(record["jobId"]), name)
        if not os.path.isfile(path):
            raise HTTPException(status_code=404, detail="artifact not ready")
        return FileResponse(path, media_type=content_type_for(name), filename=name)

    @router.delete("/{job_id}", status_code=204)
    def delete_job(request: Request, job_id: str) -> JSONResponse:
        if not is_valid_job_id(job_id):
            raise HTTPException(status_code=404, detail="unknown job")
        request.app.state.pool.cancel(job_id)
        request.app.state.store.delete(job_id)
        return JSONResponse(status_code=204, content=None)

    return router


# ---- helpers -----------------------------------------------------------------

async def _text_part(part) -> str:
    """The `options` part, whether it arrived as a form field or as a named file part."""
    if part is None:
        return "{}"
    if isinstance(part, UploadFile):
        return (await part.read()).decode("utf-8", errors="replace")
    return str(part)


def _bad(code: str, message: str) -> None:
    raise HTTPException(status_code=400, detail={"code": code, "message": message})


def _parse_options(raw: str) -> JobOptions:
    try:
        return JobOptions.model_validate(json.loads(raw or "{}"))
    except (json.JSONDecodeError, ValueError):
        _bad("invalid_options", "The options part is not valid JSON for this API version.")


def _validate_counts(photos: List[UploadFile], settings: Settings) -> None:
    usable = [p for p in photos if p is not None and (p.filename or "").strip()]
    if len(usable) < settings.min_photos:
        _bad("too_few_photos", f"Upload at least {settings.min_photos} photos of the wall.")
    if len(usable) > settings.max_photos:
        _bad("too_many_photos", f"Upload at most {settings.max_photos} photos of the wall.")
    for photo in usable:
        if not ingest.acceptable(photo.filename or "", photo.content_type):
            _bad("unsupported_photo_type",
                 f"'{photo.filename}' is not a JPEG, PNG or HEIC image.")


async def _land_uploads(store: JobStore, job_id: str, photos: List[UploadFile],
                        old_photo: Optional[UploadFile], settings: Settings):
    raw_dir = os.path.join(store.job_dir(job_id), "raw")
    os.makedirs(raw_dir, exist_ok=True)
    os.makedirs(store.upload_dir(job_id), exist_ok=True)
    budget = [0]
    saved: List[str] = []

    usable = [p for p in photos if p is not None and (p.filename or "").strip()]
    for index, (photo, name) in enumerate(zip(usable, ingest.numbered_names(len(usable))), start=1):
        raw = os.path.join(raw_dir, f"{index}{os.path.splitext(photo.filename or '')[1].lower()}")
        await ingest.save_upload(photo, raw, settings.max_photo_bytes, budget,
                                 settings.max_request_bytes)
        target = os.path.join(store.upload_dir(job_id), name)
        ingest.normalise(raw, target)
        saved.append(target)

    old_path = None
    if old_photo is not None and (old_photo.filename or "").strip():
        raw = os.path.join(raw_dir, "old" + os.path.splitext(old_photo.filename or "")[1].lower())
        await ingest.save_upload(old_photo, raw, settings.max_photo_bytes, budget,
                                 settings.max_request_bytes)
        old_path = os.path.join(store.job_dir(job_id), "old-photo.jpg")
        ingest.normalise(raw, old_path)
    return saved, old_path


def _load(request: Request, job_id: str) -> dict:
    record = request.app.state.store.read(job_id)
    if record is None:
        raise HTTPException(status_code=404, detail="unknown job")
    return record


def _pipeline_imports() -> bool:
    try:
        import cv2  # noqa: F401
        import numpy  # noqa: F401
        import scipy  # noqa: F401
        from PIL import Image  # noqa: F401
        return True
    except Exception:  # noqa: BLE001
        return False


def _writable(path: str) -> bool:
    try:
        os.makedirs(path, exist_ok=True)
        return os.access(path, os.W_OK)
    except OSError:
        return False
