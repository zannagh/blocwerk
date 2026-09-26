"""The 3D-runner split over HTTP: which kinds a worker in which role serves (SPLAT_WORKER_MODE), and the
`splat-finish` upload (prepared.json + trainStats + the runner's trained scene). `splat-prepare` takes the
`splat` request as is, plus anchor photos (main._photos_job, anchors.py)."""
import json
import os

from computejobs.service import bad
from computejobs.upload import json_field

from .finish_upload import FinishUpload
from .options import OptionsError, parse_options, validate_geometry
from .prepare import PREPARED_KEYS, PREPARED_VERSION
from .settings import settings

MODES = ("all", "cpu")


def require_tools(kind, tools):
    """503 unless this worker (its role and its probed tools) can run `kind` now."""
    if kind == "splat" and settings.worker_mode == "cpu":
        bad("this worker runs in cpu mode (SPLAT_WORKER_MODE=cpu): it serves splat-prepare and splat-finish "
            "only; the training runs on a 3D runner", 503)
    if not tools:  # not probed (tests)
        return
    if kind == "splat" and not tools.get("ok"):
        bad("the splat tools (trainer / COLMAP) are not available on this worker; see /v1/info", 503)
    if kind == "splat-prepare" and not (tools.get("colmap") or {}).get("version"):
        bad("COLMAP is not available on this worker; see /v1/info", 503)


def check_prepared(doc):
    if not isinstance(doc, dict) or doc.get("version") != PREPARED_VERSION or any(k not in doc for k in PREPARED_KEYS):
        bad("'prepared' is not a prepared.json of this worker version (run splat-prepare again)", 422)
    try:
        if not isinstance(doc["options"], dict):
            raise OptionsError("options must be an object")
        parse_options({k: v for k, v in doc["options"].items() if v is not None})
        if doc.get("geometry") is not None:
            validate_geometry(doc["geometry"])
        for key in ("photoCentres", "frameCentres", "anchorCentres"):
            if key == "anchorCentres" and key not in doc:  # additive: older prepare jobs have none
                continue
            centres = doc[key]
            if not isinstance(centres, dict) or any(
                    not isinstance(c, list) or len(c) != 3 or not all(isinstance(v, (int, float)) for v in c)
                    for c in centres.values()):
                raise OptionsError(f"{key} must map stems to [x, y, z]")
        if not isinstance(doc["photoStems"], list) or not isinstance(doc["frameStems"], list):
            raise OptionsError("photoStems / frameStems must be lists")
    except OptionsError as e:
        bad(f"prepared: {e}", 422)


async def finish_job(svc, request, tools):
    require_tools("splat-finish", tools)
    job = svc.create_job("splat-finish", None)
    try:
        fields, _name = await FinishUpload(job.dir).read(request)
        prepared = json_field(fields, "prepared")
        check_prepared(prepared)
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
