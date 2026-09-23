"""A toy compute service for the protocol tests (runner must be importable by spawned children)."""
import json
import os
import subprocess
import time

from computejobs.child import JobError, run_in_child
from computejobs.service import ComputeService, bad, callback_url, loads_strict


def _body(job_dir, progress):
    with open(os.path.join(job_dir, "in.json")) as fh:
        req = json.load(fh)
    mode = req.get("mode", "ok")
    progress(0.2, "warm-up", "step 1/2")
    if mode == "fail":
        raise JobError("sfm-mapping", "only 3/14 images registered")
    if mode == "invalid":
        raise ValueError("bad thing")
    if mode == "crash":
        raise KeyError(f"internal detail at {job_dir}")
    if mode in ("sleep", "grandchild"):
        if mode == "grandchild":  # a tool the job started: must die with the job
            p = subprocess.Popen(["sleep", "120"])
            with open(os.path.join(job_dir, "grandchild.pid"), "w") as fh:
                fh.write(f"{p.pid} {os.getpid()}")
        time.sleep(req.get("seconds", 60))
    progress(0.9, "export")
    with open(os.path.join(job_dir, "out.json"), "w") as fh:
        json.dump({"echo": req.get("echo")}, fh)
    return {"echo": req.get("echo"), "files": ["out.json"]}


def run_job(kind, job_dir, conn):
    run_in_child(_body, job_dir, conn, (ValueError,))


async def _echo(svc, request):
    try:
        doc = loads_strict(await request.body())
    except ValueError:
        bad("body must be JSON")
    cb = callback_url(doc.pop("callbackUrl", None))

    def write(d):
        with open(os.path.join(d, "in.json"), "w") as fh:
            json.dump(doc, fh)
    return await svc.enqueue("echo", write, cb)


def make_service():
    return ComputeService(name="dummy", version="0.0.1", kinds={"echo": _echo}, runner=run_job,
                          timeout_for=lambda kind: 20,
                          elsewhere={"splat": "served by the splat-worker"},
                          info_extra=lambda: {"backend": "test"})
