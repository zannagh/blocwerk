"""FastAPI app factory for the protocol endpoints (docker/compute-jobs-protocol.md).

A service registers its kinds as `async handler(svc, request) -> Response` and typically ends a
handler with `await svc.enqueue(kind, write_inputs, callback_url)`.
"""
import inspect
import json
import logging
import mimetypes
import os
import re
from contextlib import asynccontextmanager

from fastapi import Depends, FastAPI, HTTPException, Request
from fastapi.responses import FileResponse, JSONResponse
from starlette.datastructures import UploadFile

from . import PROTOCOL, netguard
from .jobs import TERMINAL, JobManager, QueueFull
from .limits import BodyLimitMiddleware
from .security import require_api_key
from .settings import settings

SAFE_NAME = re.compile(r"^[A-Za-z0-9_.-]{1,128}$")
MEDIA_TYPES = {".jpg": "image/jpeg", ".jpeg": "image/jpeg", ".json": "application/json",
               ".png": "image/png", ".splat": "application/octet-stream",
               ".spz": "application/octet-stream", ".ply": "application/octet-stream"}


def bad(msg, code=400):
    raise HTTPException(status_code=code, detail=msg)


def callback_url(url):
    """Validate a client-supplied callbackUrl (syntax + IP-literal SSRF check; DNS is checked, once
    and pinned, when the callback is actually sent: netguard.post)."""
    if url is None or url == "":
        return None
    try:
        return netguard.check_literal(url)
    except netguard.UnsafeTarget as e:
        bad(str(e))


def _no_constants(name):
    raise ValueError(f"{name} is not valid JSON")


def loads_strict(raw):
    """json.loads without NaN / Infinity (Python accepts them by default; nothing here wants them)."""
    return json.loads(raw, parse_constant=_no_constants)


async def read_json_part(form, key):
    v = form.get(key)
    if v is None:
        return None
    raw = await v.read() if isinstance(v, UploadFile) else v
    try:
        return loads_strict(raw)
    except (ValueError, UnicodeDecodeError, RecursionError):
        bad(f"'{key}' must be JSON")


def media_type(name):
    ext = os.path.splitext(name)[1].lower()
    return MEDIA_TYPES.get(ext) or mimetypes.guess_type(name)[0] or "application/octet-stream"


class ComputeService:
    """name/version for /health; kinds {kind: handler}; runner + timeout_for for the JobManager;
    elsewhere {kind: detail} answers 501 (kind served by another worker); info_extra() -> dict for the
    authenticated GET /v1/info; ready() -> bool turns /health's status into "degraded" when False."""

    def __init__(self, *, name, version, kinds, runner, timeout_for, elsewhere=None, info_extra=None,
                 on_startup=None, ready=None):
        self.name, self.version, self.kinds = name, version, kinds
        self.runner, self.timeout_for = runner, timeout_for
        self.elsewhere = elsewhere or {}
        self.info_extra = info_extra
        self.ready = ready
        self.on_startup = on_startup
        self.manager = None
        self.log = logging.getLogger(name)

        @asynccontextmanager
        async def lifespan(_app):
            self.startup()
            yield

        # no /docs, /redoc, /openapi.json: they would be unauthenticated (the protocol doc is the spec)
        self.app = FastAPI(title=f"Blocwerk {name}", version=version, lifespan=lifespan,
                           docs_url=None, redoc_url=None, openapi_url=None)
        self.app.add_middleware(BodyLimitMiddleware)
        self._routes()

    def startup(self):
        logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")
        if not settings.api_key:
            self.log.warning("COMPUTE_API_KEY is not set: the API is OPEN. Only acceptable on a private network.")
        if self.on_startup:
            self.on_startup()
        if self.manager is None:
            self.manager = JobManager(self.runner, self.timeout_for)

    def jobs(self):
        if self.manager is None:
            self.startup()
        return self.manager

    def create_job(self, kind, callback):
        try:
            return self.jobs().create(kind, callback)
        except QueueFull:
            bad(f"queue full ({settings.max_queued} jobs waiting); retry later", 429)

    async def enqueue(self, kind, write_inputs, callback):
        """Create a job, let write_inputs(job_dir) (sync or async) store its inputs, queue it."""
        m = self.jobs()
        job = self.create_job(kind, callback)
        try:
            res = write_inputs(job.dir)
            if inspect.isawaitable(res):
                await res
        except BaseException:
            m.discard(job)
            raise
        m.submit(job)
        return JSONResponse({"jobId": job.id, "status": job.status}, status_code=202)

    def job(self, job_id):
        job = self.jobs().get(job_id) if SAFE_NAME.match(job_id) else None
        if job is None:
            bad("no such job (unknown, or expired after its TTL)", 404)
        return job

    def health(self):
        """Unauthenticated: only what a load balancer / the app's probe needs. No paths, tool versions,
        limits or queue state (those are in the authenticated /v1/info)."""
        status = "ok" if self.ready is None or self.ready() else "degraded"
        return {"status": status, "service": self.name, "protocol": PROTOCOL, "version": self.version,
                "kinds": list(self.kinds)}

    def info(self):
        body = {**self.health(), "gitSha": settings.git_sha, "auth": bool(settings.api_key),
                "jobs": self.jobs().counts()}
        if self.info_extra:
            body.update(self.info_extra())
        return body

    def _routes(self):
        app, auth = self.app, [Depends(require_api_key)]

        @app.get("/health")
        def health():
            return self.health()

        @app.get("/v1/info", dependencies=auth)
        def info():
            return self.info()

        @app.post("/v1/jobs/{kind}", dependencies=auth, status_code=202)
        async def create_job(kind: str, request: Request):
            if kind in self.elsewhere:
                bad(self.elsewhere[kind], 501)
            handler = self.kinds.get(kind)
            if handler is None:
                bad(f"unknown job kind {kind!r}; this service runs {list(self.kinds)}", 404)
            return await handler(self, request)

        @app.get("/v1/jobs/{job_id}", dependencies=auth)
        def job_status(job_id: str):
            return self.job(job_id).to_status()

        @app.delete("/v1/jobs/{job_id}", dependencies=auth)
        def job_cancel(job_id: str):
            job = self.job(job_id)
            if job.status not in TERMINAL:
                self.jobs().cancel(job_id)
            return job.to_status()

        @app.get("/v1/jobs/{job_id}/files/{name}", dependencies=auth)
        def job_file(job_id: str, name: str):
            job = self.job(job_id)
            if name not in job.files or not SAFE_NAME.match(name):
                bad("no such file for this job", 404)
            return FileResponse(os.path.join(job.dir, name), media_type=media_type(name), filename=name)
