"""Kind `solve-sfm` (multipart): `request` (the JSON request, README "Solve from features"), `sparse` (splat-prepare's
sparse.zip), optional `callbackUrl`. The zip is held in memory only until its part ends (like a photo), capped
at SFM_MAX_SPARSE_MB, checked for its four files, and written as the job's sparse.zip."""
import json
import os
import zipfile

from computejobs.service import bad, callback_url
from computejobs.upload import MAX_FIELD_BYTES, StreamingUpload, json_field, text_field

from wallgeometry.request import RequestError
from wallgeometry.sfm.colmap_io import FILES
from wallgeometry.sfm.request import parse_sfm_request

from .settings import settings

FIELDS = {"request", "callbackUrl"}


class SparseUpload(StreamingUpload):
    """One `sparse` file part (the zip) + the fields; the zip may be bigger than a photo."""

    def __init__(self, job_dir):
        self.path = os.path.join(job_dir, "sparse.zip")
        super().__init__(fields=FIELDS, photo_ext={".zip"}, process_photo=self._store, photo_field="sparse",
                         kind="solve-sfm")

    def on_part_data(self, data, start, end):
        p = self.part
        p.data += data[start:end]
        limit = settings.sfm_max_sparse_bytes if p.name == self.photo_field else MAX_FIELD_BYTES
        if len(p.data) > limit and not self.error:
            self.error = (413, f"part {p.filename or p.name!r} exceeds {limit >> 20} MB")

    def _store(self, _stem, _ext, raw):
        if os.path.exists(self.path):
            bad("send exactly one 'sparse' file")
        with open(self.path, "wb") as fh:
            fh.write(raw)
        return {"bytes": len(raw)}


def check_zip(path):
    try:
        with zipfile.ZipFile(path) as z:
            names = set(z.namelist())
    except zipfile.BadZipFile:
        bad("'sparse' is not a zip file", 422)
    if names != set(FILES):
        bad(f"'sparse' must be splat-prepare's sparse.zip ({', '.join(FILES)})", 422)


async def solve_sfm_job(svc, request):
    job = svc.create_job("solve-sfm", None)
    try:
        fields, files = await SparseUpload(job.dir).read(request)
        doc = json_field(fields, "request")
        if doc is None:
            bad("'request' (the solve-sfm request JSON) is required", 422)
        try:
            parse_sfm_request(doc)
        except RequestError as e:
            bad(str(e), 422)
        if not files:
            bad("'sparse' (splat-prepare's sparse.zip) is required", 422)
        check_zip(os.path.join(job.dir, "sparse.zip"))
        job.callback_url = callback_url(text_field(fields, "callbackUrl"))
        with open(os.path.join(job.dir, "request.json"), "w") as fh:
            json.dump(doc, fh)
    except BaseException:
        svc.jobs().discard(job)
        raise
    svc.jobs().submit(job)
    return {"jobId": job.id, "status": job.status}


def run_solve_sfm(job_dir, progress):
    """Job body: unpack, solve, keep only the document."""
    import shutil

    from wallgeometry.sfm import solve_sfm_document
    from wallgeometry.sfm.colmap_io import extract_sparse
    with open(os.path.join(job_dir, "request.json")) as fh:
        req = json.load(fh)
    model_dir = extract_sparse(os.path.join(job_dir, "sparse.zip"), os.path.join(job_dir, "sparse"),
                               2 * settings.sfm_max_sparse_bytes)
    try:
        doc, _ = solve_sfm_document(req, model_dir, progress)
    finally:
        shutil.rmtree(model_dir, ignore_errors=True)
        os.remove(os.path.join(job_dir, "sparse.zip"))
    with open(os.path.join(job_dir, "wall-geometry.json"), "w") as fh:
        json.dump(doc, fh)
    return {"geometry": doc, "files": ["wall-geometry.json"]}
