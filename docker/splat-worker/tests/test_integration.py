"""Opt-in end-to-end run with the real tools (COLMAP + Brush on a GPU):

  SPLAT_INTEGRATION_PHOTOS=/path/to/overlapping/photos [SPLAT_INTEGRATION_GEOMETRY=wall-geometry.json] \
  BRUSH_BIN=... COLMAP_BIN=... python -m pytest tests/test_integration.py

Uses a tiny budget (<= 8 photos at 640 px, 150 steps): it proves the wiring, not the quality.
"""
import glob
import json
import os
import time

import pytest
from fastapi.testclient import TestClient

from computejobs.settings import settings

PHOTOS = os.environ.get("SPLAT_INTEGRATION_PHOTOS")
pytestmark = pytest.mark.skipif(not PHOTOS, reason="set SPLAT_INTEGRATION_PHOTOS to run")


def test_tiny_job_end_to_end(tmp_path):
    from splatworker import main
    from splatworker.runner import run_job
    from splatworker.splatio import SPLAT_DTYPE, read_spz
    settings.work_dir, settings.api_key = str(tmp_path), None
    main.service.runner, main.service.manager, main.service.on_startup = run_job, None, main._probe_tools
    photos = sorted(glob.glob(os.path.join(PHOTOS, "*.jp*g")))[:8]
    files = [("photos", (os.path.basename(p), open(p, "rb").read(), "image/jpeg")) for p in photos]
    files.append(("options", (None, json.dumps({"maxSteps": 150, "maxImageEdge": 640}))))
    geo = os.environ.get("SPLAT_INTEGRATION_GEOMETRY")
    if geo:
        files.append(("geometry", ("g.json", open(geo, "rb").read(), "application/json")))
    with TestClient(main.app) as c:
        assert c.get("/health").json()["status"] == "ok", "tools missing: set BRUSH_BIN / COLMAP_BIN"
        assert c.get("/v1/info").json()["tools"]["ok"]
        r = c.post("/v1/jobs/splat", files=files)
        assert r.status_code == 202, r.text
        job, stages, t0 = r.json()["jobId"], [], time.time()
        while time.time() - t0 < 1800:
            st = c.get(f"/v1/jobs/{job}").json()
            if not stages or stages[-1] != st["stage"]:
                stages.append(st["stage"])
            if st["status"] in ("succeeded", "failed", "cancelled"):
                break
            time.sleep(0.5)
        assert st["status"] == "succeeded", st["error"]
        assert {"sfm-matching", "train", "export"} <= set(stages)
        names = {f["name"]: f["url"] for f in st["result"]["files"]}
        splat = c.get(names["wall.splat"]).content
        assert len(splat) % SPLAT_DTYPE.itemsize == 0 and len(splat) // 32 == st["result"]["stats"]["splatCount"]
        assert read_spz(c.get(names["wall.spz"]).content)["xyz"].shape[0] == st["result"]["stats"]["splatCount"]
        frame = json.loads(c.get(names["frame.json"]).content)
        assert frame["aligned"] is bool(geo)
