"""The compute envelope of the runner kinds `splat-prepare` / `splat-finish` (Blocwerk server: RunnerComputeEnvelope;
docker/compute-jobs-protocol.md): the splat-worker's HTTP job carried through the runner API instead.

The job's bundle is a zip of `parts.json` and the part files, stored uncompressed:
  {"version": 1, "service": "splat", "kind": "splat-prepare", "parts": [{"name": "photos", "file": "IMG_1.jpg"},
   {"name": "options", "json": {...}}, {"name": "geometry", "file": "geometry.json"}, ...]}
`name` is the multipart part of the HTTP request (`photos` repeated, `options`, `geometry` for splat-prepare; `prepared`,
`trainStats`, `splat` for splat-finish), `file` an entry of the zip (its base name is the HTTP file name), `json` the value
of a JSON part inline. unpack() builds the job directory exactly as the HTTP upload does (job_inputs.py, split_api.py,
upload.SplatUpload.store); the result is a zip of `status.json` (the GET /v1/jobs/{id} body of the finished job) and the
files its `result.files` lists, which pack_result() writes.

Only listed entries are read; a path that escapes, an unlisted entry or too many entries or bytes refuse the bundle (it
comes from a server the key trusts, but a runner is a machine in somebody's home)."""
import json
import os
import re
import shutil
import zipfile
from datetime import datetime, timezone

from computejobs.service import SAFE_NAME
from computejobs.upload import MAX_FIELD_BYTES
from fastapi import HTTPException

from ..finish_upload import FILE_NAMES, MAX_JSON_BYTES
from ..job_inputs import write_photo_inputs
from ..portable import check_names
from ..settings import settings
from ..split_api import write_finish_inputs
from ..upload import PHOTO_EXT, SplatUpload

MANIFEST, STATUS = "parts.json", "status.json"
VERSION, SERVICE = 1, "splat"
PARTS = {"splat-prepare": ("photos", "geometry", "options"), "splat-finish": ("prepared", "splat", "trainStats")}
UNSAFE = re.compile(r"(^|/)\.\.?(/|$)|^/|\\|:")


class EnvelopeError(Exception):
    """The bundle is not a valid envelope of the job's kind (reported as a failure that no retry fixes)."""


def _check_manifest(doc, kind):
    if not isinstance(doc, dict) or doc.get("version") != VERSION or doc.get("service") != SERVICE:
        raise EnvelopeError(f"{MANIFEST} is not a version {VERSION} envelope of the {SERVICE} service")
    if doc.get("kind") != kind:
        raise EnvelopeError(f"the envelope is for kind {doc.get('kind')!r}, the job is {kind!r}")
    parts = doc.get("parts")
    if not isinstance(parts, list):
        raise EnvelopeError(f"{MANIFEST} lacks its parts")
    for p in parts:
        if not isinstance(p, dict) or p.get("name") not in PARTS[kind] or ("file" in p) == ("json" in p) \
                or ("file" in p and (not isinstance(p["file"], str) or UNSAFE.search(p["file"]))):
            raise EnvelopeError(f"{MANIFEST} has an invalid part: {str(p)[:80]}")
    return parts


def _read_entry(zf, info, limit):
    if info.file_size > limit:
        raise EnvelopeError(f"{info.filename[:80]} is larger than {limit >> 20} MB")
    return zf.read(info)


def _fields(zf, entries, parts, kind):
    """{name: bytes} of the JSON parts (inline or as a file); `splat` and the photos are not fields."""
    fields = {}
    for p in parts:
        name = p["name"]
        if name in ("photos", "splat"):
            continue
        if name in fields:
            raise EnvelopeError(f"part {name!r} given twice")
        limit = MAX_JSON_BYTES if kind == "splat-finish" else MAX_FIELD_BYTES
        fields[name] = _read_entry(zf, entries[p["file"]], limit) if "file" in p else json.dumps(p["json"]).encode()
        if len(fields[name]) > limit:
            raise EnvelopeError(f"part {name!r} is larger than {limit >> 20} MB")
    return fields


def _store_photos(zf, entries, parts, job_dir):
    """Every `photos` part through the HTTP upload's own photo hook (metadata-free re-encode into arrived/)."""
    upload, photos = SplatUpload(job_dir), {}
    files = [p for p in parts if p["name"] == "photos"]
    if len(files) > settings.max_photos:
        raise EnvelopeError(f"too many photos (max {settings.max_photos})")
    for p in files:
        if "file" not in p:
            raise EnvelopeError("a photos part needs a file")
        stem, ext = os.path.splitext(os.path.basename(p["file"]))
        if ext.lower() not in PHOTO_EXT or not SAFE_NAME.match(stem) or stem in photos:
            raise EnvelopeError(f"photo file name {p['file'][:80]!r}: expected a unique <name>.<jpg|png|...>")
        raw = _read_entry(zf, entries[p["file"]], settings.max_photo_bytes)
        try:
            photos[stem] = upload.store(stem, ext.lower(), raw)
        except ValueError as e:  # upload.PhotoRejected
            raise EnvelopeError(f"photo {stem}: {e}") from e
    return photos


def _store_splat(zf, entries, parts, job_dir):
    scenes = [p for p in parts if p["name"] == "splat"]
    if len(scenes) != 1 or "file" not in scenes[0] or os.path.basename(scenes[0]["file"]) not in FILE_NAMES:
        raise EnvelopeError("expected ONE file part 'splat' named splat.ply or splat.spz")
    info = entries[scenes[0]["file"]]
    if info.file_size > settings.max_result_bytes:
        raise EnvelopeError(f"the trained scene exceeds {settings.max_result_bytes >> 20} MB")
    with zf.open(info) as src, open(os.path.join(job_dir, os.path.basename(info.filename)), "wb") as dst:
        shutil.copyfileobj(src, dst)


def unpack(zip_path, job_dir, kind):
    """Builds the job directory the HTTP upload of `kind` builds; returns how many photos it holds (0 for a finish)."""
    if kind not in PARTS:
        raise EnvelopeError(f"{kind!r} is not an envelope kind")
    try:
        with zipfile.ZipFile(zip_path) as zf:
            entries = {i.filename: i for i in zf.infolist() if not i.is_dir()}
            if MANIFEST not in entries:
                raise EnvelopeError(f"the bundle lacks {MANIFEST}")
            parts = _check_manifest(json.loads(_read_entry(zf, entries[MANIFEST], MAX_JSON_BYTES)), kind)
            named = [p["file"] for p in parts if "file" in p]
            if len(set(named)) != len(named) or set(named) != set(entries) - {MANIFEST}:
                raise EnvelopeError(f"the entries of the bundle are not exactly the files {MANIFEST} lists")
            os.makedirs(job_dir, exist_ok=True)
            check_names([os.path.basename(n) for n in named], job_dir)
            fields = _fields(zf, entries, parts, kind)
            if kind == "splat-finish":
                _store_splat(zf, entries, parts, job_dir)
                write_finish_inputs(job_dir, fields)
                return 0
            photos = _store_photos(zf, entries, parts, job_dir)
            write_photo_inputs(job_dir, fields, photos, kind)
            return len(photos)
    except HTTPException as e:  # what the HTTP API answers with 4xx
        raise EnvelopeError(str(e.detail)) from e
    except (zipfile.BadZipFile, ValueError, KeyError) as e:
        raise EnvelopeError(f"bad envelope: {e}") from e


def now_iso():
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


def status_doc(job_id, kind, payload, created):
    """What computejobs.jobs.Job.to_status() returns for the same finished job."""
    result = {k: v for k, v in payload.items() if k != "files"}
    if payload.get("files"):
        result["files"] = [{"name": f, "url": f"/v1/jobs/{job_id}/files/{f}"} for f in payload["files"]]
    return {"jobId": job_id, "kind": kind, "status": "succeeded", "progress": 1.0, "stage": "done",
            "stageDetail": None, "message": None, "error": None, "createdAt": created, "updatedAt": now_iso(),
            "result": result}


def pack_result(job_dir, status, out):
    """Zips status.json and the files its result lists (stored) into `out`; returns the number of entries."""
    names = [f["name"] for f in (status["result"].get("files") or [])]
    with zipfile.ZipFile(out, "w", zipfile.ZIP_STORED) as zf:
        zf.writestr(STATUS, json.dumps(status, separators=(",", ":")))
        for name in names:
            if not SAFE_NAME.match(name):
                raise EnvelopeError(f"the job named an unsafe result file: {name[:80]!r}")
            zf.write(os.path.join(job_dir, name), name)
    return len(names) + 1
