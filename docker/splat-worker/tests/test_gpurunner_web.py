"""The runner's local status page (web.py, page.py): loopback by default, its endpoints, the pause forms, the local
token for the switch, refused foreign hosts and cross-site posts, odd Content-Lengths and damaged history rows; the
capped job history and the running job's ETA (history.py)."""
import http.client
import json
import socket

import pytest

from splatworker.gpurunner import uitoken, web
from splatworker.gpurunner.control import AFTER_JOB, PAUSED, RUNNING, PauseControl
from splatworker.gpurunner.history import JobHistory, JobStatus


class StubRunner:
    def __init__(self, tmp_path, current=None):
        self.control = PauseControl(str(tmp_path / "state"))
        self.current = current

    def snapshot(self):
        return {"server": "https://blocwerk.app", "runnerName": "Cellar <PC>", "startedAt": 1.0,
                "pause": self.control.doc(), "busy": self.current is not None, "lastError": None,
                "caps": {"gpuName": "RTX", "vramMb": 16384, "maxQuality": "ultra", "trainer": "gsplat"},
                "current": self.current,
                "jobs": [{"jobId": "j1", "server": "https://blocwerk.app", "outcome": "failed", "startedAt": 2.0,
                          "durationS": 75, "stages": {"train": 60}, "error": "<script>alert(1)</script>"}]}


TOKEN = "t" * 32


@pytest.fixture
def page(tmp_path):
    runner = StubRunner(tmp_path)
    ui = web.StatusPage(runner, "127.0.0.1", 0, token=TOKEN)
    yield runner, ui
    ui.close()


def call(ui, method, path, body=None, headers=None, token=TOKEN):
    """A request; POSTs carry the token unless token=None."""
    conn = http.client.HTTPConnection("127.0.0.1", ui.port, timeout=5)
    h = {"Host": f"127.0.0.1:{ui.port}", **({"X-Runner-Token": token} if token and method == "POST" else {}),
         **(headers or {})}
    if body is not None:
        h["Content-Type"] = "application/x-www-form-urlencoded"
    conn.request(method, path, body=body, headers=h)
    r = conn.getresponse()
    out = (r.status, r.getheader("Location"), r.read().decode())
    call.cookie = r.getheader("Set-Cookie")
    conn.close()
    return out


def test_settings_default_to_loopback_and_are_configurable():
    assert web.ui_settings({}) == ("127.0.0.1", 8190)
    assert web.ui_settings({"RUNNER_UI_HOST": "0.0.0.0", "RUNNER_UI_PORT": "8191"}) == ("0.0.0.0", 8191)
    assert web.ui_settings({"RUNNER_UI_PORT": "0"})[1] == 0
    assert web.ui_settings({"RUNNER_UI_PORT": "nope"})[1] == 8190


def test_the_default_binding_is_loopback_and_a_taken_port_is_not_fatal(tmp_path):
    runner = StubRunner(tmp_path)
    assert web.start_status_page(runner, {"RUNNER_UI_PORT": "0"}) is None
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        port = s.getsockname()[1]
    ui = web.start_status_page(runner, {"RUNNER_UI_PORT": str(port)})
    try:
        assert ui.httpd.server_address[0] == "127.0.0.1" and ui.url == f"http://127.0.0.1:{port}/"
        assert web.start_status_page(runner, {"RUNNER_UI_PORT": str(port)}) is None  # taken: no crash
    finally:
        ui.close()


def test_the_page_the_fragment_and_the_json(page):
    runner, ui = page
    status, _, html = call(ui, "GET", "/")
    assert status == 200 and "<title>Blocwerk 3D runner</title>" in html and "Waiting for jobs" in html
    assert "Cellar &lt;PC&gt;" in html and "&lt;script&gt;alert(1)" in html and "<script>alert" not in html
    assert 'action="pause"' in html and "1 min 15 s" in html
    status, _, frag = call(ui, "GET", "/fragment")
    assert status == 200 and "<html" not in frag and "Jobs (newest first)" in frag
    status, _, body = call(ui, "GET", "/status.json")
    assert status == 200 and json.loads(body)["pause"]["mode"] == RUNNING
    assert call(ui, "GET", "/nope")[0] == 404


def test_the_forms_pause_and_resume(page):
    runner, ui = page
    assert call(ui, "POST", "/pause", "mode=after-job")[:2] == (303, "/")
    assert runner.control.mode == PAUSED  # no job running: after-job is a pause now
    assert "Resume" in call(ui, "GET", "/")[2]
    assert call(ui, "POST", "/resume", "")[0] == 303 and runner.control.mode == RUNNING
    runner.control.set_busy(True)
    runner.current = {"jobId": "j2", "stage": "train", "progress": {"fraction": 0.5, "step": 5, "totalSteps": 10},
                      "etaS": 125, "stages": {"download": 3}, "startedAt": 1.0, "elapsedS": 10}
    html = call(ui, "GET", "/")[2]
    assert "Finish this job, then pause" in html and "2 min 05 s" in html and "5 / 10" in html
    call(ui, "POST", "/pause", "mode=after-job")
    assert runner.control.mode == AFTER_JOB and "Pausing after the current job" in call(ui, "GET", "/")[2]
    call(ui, "POST", "/pause", "mode=now")
    assert runner.control.mode == PAUSED and runner.control.now.is_set()


def test_foreign_hosts_and_cross_site_posts_are_refused(page):
    runner, ui = page
    assert call(ui, "GET", "/", headers={"Host": "evil.example:8190"})[0] == 403  # DNS rebinding
    assert call(ui, "POST", "/pause", "mode=now", {"Origin": "https://evil.example"})[0] == 403
    assert call(ui, "POST", "/pause", "mode=now", {"Sec-Fetch-Site": "cross-site"})[0] == 403
    assert runner.control.mode == RUNNING
    assert call(ui, "POST", "/pause", "mode=now", {"Origin": f"http://127.0.0.1:{ui.port}"})[0] == 303
    assert call(ui, "GET", "/", headers={"Host": f"localhost:{ui.port}"})[0] == 200


def test_the_history_keeps_the_newest_jobs(tmp_path):
    h = JobHistory(str(tmp_path / "state"), cap=5)
    assert h.recent() == []
    for i in range(8):
        h.append({"jobId": f"j{i}"})
    assert [d["jobId"] for d in h.recent()] == ["j7", "j6", "j5", "j4", "j3"]
    with open(h.path, "a") as fh:
        fh.write('{"jobId": "torn')  # a crash mid-line
    assert len(h.recent()) == 5 and h.recent(2)[0]["jobId"] == "j7"
    h.append({"jobId": "j8"})
    assert [d["jobId"] for d in h.recent()] == ["j8", "j7", "j6", "j5", "j4"]


def test_the_job_status_times_its_stages_and_estimates_the_rest():
    now = [100.0]
    s = JobStatus({"jobId": "j1", "quality": "max", "wallId": "w"}, "https://x", clock=lambda: now[0])
    s.update({"stage": "download", "fraction": 0.0})
    now[0] = 110.0
    s.update({"stage": "train", "fraction": 0.4})  # resumed from a checkpoint: starts at 40 %
    assert s.snapshot()["etaS"] is None
    now[0] = 140.0
    s.update({"stage": "train", "fraction": 0.5})
    snap = s.snapshot()
    assert snap["etaS"] == 150 and snap["stages"] == {"download": 10.0, "train": 30.0}
    now[0] = 150.0
    rec = s.finish("succeeded", previews=2)
    assert rec["durationS"] == 50.0 and rec["stages"] == {"download": 10.0, "train": 40.0}
    assert rec["outcome"] == "succeeded" and rec["previewsUploaded"] == 2


def test_the_switch_needs_the_token(page, monkeypatch):
    runner, ui = page
    monkeypatch.setattr(web, "is_loopback", lambda address: False)  # a browser on the Docker host, another container
    assert call(ui, "POST", "/pause", "mode=now", token=None)[0] == 403
    assert call(ui, "POST", "/pause", "mode=now", token="wrong-token-wrong-token")[0] == 403
    status, _, html = call(ui, "GET", "/")
    assert status == 200 and call.cookie is None
    assert "needs this runner's token" in html and 'action="pause"' not in html
    assert call(ui, "GET", "/?token=nope")[0] == 403
    assert call(ui, "GET", f"/?token={TOKEN}")[:2] == (303, "/")
    cookie = call.cookie.split(";")[0]
    assert "HttpOnly" in call.cookie and "SameSite=Strict" in call.cookie
    assert 'action="pause"' in call(ui, "GET", "/", headers={"Cookie": cookie})[2]
    assert runner.control.mode == RUNNING
    assert call(ui, "POST", "/pause", "mode=now", headers={"Cookie": cookie}, token=None)[0] == 303
    assert runner.control.mode == PAUSED


def test_this_machines_own_browser_gets_the_cookie(page):
    runner, ui = page  # the test client is on loopback: a native runner's own machine
    status, _, html = call(ui, "GET", "/")
    assert status == 200 and 'action="pause"' in html and call.cookie.startswith(f"{uitoken.COOKIE}={TOKEN}")


def test_the_token_is_made_once_and_kept(tmp_path):
    d = str(tmp_path / "state")
    token = uitoken.load_or_create(d)
    assert len(token) >= 20 and uitoken.load_or_create(d) == token
    assert (tmp_path / "state" / "ui-token").stat().st_mode & 0o077 == 0


@pytest.mark.parametrize("length,expected", [("abc", 400), ("-5", 303), ("999999", 413)])
def test_odd_content_lengths(page, length, expected):
    runner, ui = page
    conn = http.client.HTTPConnection("127.0.0.1", ui.port, timeout=5)
    conn.putrequest("POST", "/resume", skip_host=True)
    for k, v in {"Host": f"127.0.0.1:{ui.port}", "X-Runner-Token": TOKEN, "Content-Length": length}.items():
        conn.putheader(k, v)
    conn.endheaders()
    assert conn.getresponse().status == expected
    conn.close()


def test_damaged_history_rows_do_not_blank_the_page(page):
    runner, ui = page
    bad = [{"jobId": "x", "startedAt": "soon", "durationS": "long", "stages": ["train"], "previewsUploaded": "two"},
           "not a row", {"jobId": "y", "startedAt": 1e30, "durationS": float("nan")}]
    good = runner.snapshot()["jobs"]
    runner.snapshot = lambda: {**StubRunner.snapshot(runner), "jobs": bad + good}
    status, _, html = call(ui, "GET", "/")
    assert status == 200 and "Jobs (newest first)" in html and "1 min 15 s" in html
