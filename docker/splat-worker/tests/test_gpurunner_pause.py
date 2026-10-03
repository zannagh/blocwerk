"""The pause switch against the fake server: a paused runner makes no claims, probes no trainer (no torch) and
says hello with `paused: true`; the switch survives a restart; "finish then pause" ends the running job first;
"pause now" hands it back as a free pause; a job claimed while the pause came in goes straight back; every
finished job lands in the history."""
import sys
import threading
import time

import pytest
from test_gpurunner import CAPS, KEY, brush_ok, fast, make_bundle, server  # noqa: F401 - fixtures

from splatworker import brush, procs
from splatworker.gpurunner import loop
from splatworker.gpurunner.client import Client
from splatworker.gpurunner.control import AFTER_JOB, PAUSED, RUNNING, PauseControl, state_dir
from splatworker.gpurunner.loop import Runner


class FakeCaps:
    """Counts trainer probes (the real one imports torch in a child process)."""
    trainer = "brush"

    def __init__(self):
        self.probes = 0

    @property
    def usable(self):
        self.probes += 1
        return True

    def __call__(self):
        self.probes += 1
        return dict(CAPS)

    def lite(self):
        return dict(CAPS)


@pytest.fixture
def idle_fast(monkeypatch):
    monkeypatch.setattr(loop, "IDLE_TICK_S", 0.02)
    monkeypatch.setattr(loop, "PAUSED_HELLO_S", 0.05)


def until(cond, timeout=10.0):
    end = time.monotonic() + timeout
    while not cond():
        assert time.monotonic() < end, "timed out"
        time.sleep(0.01)


def start(runner):
    result = {}
    t = threading.Thread(target=lambda: result.setdefault("code", runner.run()), daemon=True)
    t.start()
    return t, result


def stop(runner, t, result):
    runner.stop()
    t.join(10)
    assert not t.is_alive() and result["code"] == 0


def test_a_paused_runner_makes_no_claims_probes_nothing_and_resumes(tmp_path, fast, server, idle_fast,  # noqa: F811
                                                                    monkeypatch):
    monkeypatch.setattr(brush, "train", brush_ok)
    srv = server(make_bundle(tmp_path))
    work = str(tmp_path / "w")
    PauseControl(state_dir(work)).pause()  # paused before the process starts
    caps = FakeCaps()
    runner = Runner(Client(srv.url, KEY), work, caps, max_jobs=1)
    t, result = start(runner)
    until(lambda: len(srv.hellos) >= 3)
    assert srv.claims == [] and caps.probes == 0 and "torch" not in sys.modules
    assert all(h["paused"] is True and h["gpuName"] == "Test GPU" for h in srv.hellos)

    runner.control.resume()
    t.join(20)
    assert result["code"] == 0 and runner.outcomes == ["succeeded"]
    assert srv.hellos[-1]["paused"] is False and len(srv.claims) == 1 and caps.probes >= 1
    assert PauseControl(state_dir(work)).mode == RUNNING


def test_the_switch_survives_a_restart(tmp_path):
    d = str(tmp_path / "state")
    c = PauseControl(d)
    assert c.mode == RUNNING and c.claiming
    c.set_busy(True)
    c.pause(now=False)
    assert c.mode == AFTER_JOB and not c.now.is_set()
    again = PauseControl(d)  # a new process holds no job: an after-job pause is a pause
    assert again.mode == PAUSED and again.now.is_set() and again.since is not None
    again.resume()
    assert PauseControl(d).mode == RUNNING
    (tmp_path / "state" / "pause.json").write_text("{not json")
    assert PauseControl(d).mode == RUNNING


def test_finish_then_pause_ends_the_job_and_claims_no_more(tmp_path, fast, server, idle_fast,  # noqa: F811
                                                          monkeypatch):
    def train(*a, **k):
        runner.control.pause(now=False)
        assert runner.control.mode == AFTER_JOB
        return brush_ok(*a, **k)

    monkeypatch.setattr(brush, "train", train)
    srv = server(make_bundle(tmp_path))
    srv.jobs_left = 2
    runner = Runner(Client(srv.url, KEY), str(tmp_path / "w"), FakeCaps())
    t, result = start(runner)
    until(lambda: runner.control.paused and srv.hellos and srv.hellos[-1]["paused"])
    assert runner.outcomes == ["succeeded"] and len(srv.results) == 1 and len(srv.claims) == 1 and not srv.fails
    stop(runner, t, result)
    assert len(srv.claims) == 1


def test_pause_now_hands_the_running_job_back_for_free(tmp_path, fast, server, idle_fast, monkeypatch):  # noqa: F811
    def train(bin_path, dataset, out, steps, edge, cache, log, report, *a, **k):
        report(0.2, "step 1000/5000")
        runner.control.pause()
        assert procs.current_stop.wait(5)
        raise procs.ToolStopped("train", "Brush was stopped")

    monkeypatch.setattr(brush, "train", train)
    srv = server(make_bundle(tmp_path))
    srv.jobs_left = 2
    runner = Runner(Client(srv.url, KEY), str(tmp_path / "w"), FakeCaps())
    t, result = start(runner)
    until(lambda: srv.hellos and srv.hellos[-1]["paused"])
    stop(runner, t, result)
    assert runner.outcomes == ["paused"] and len(srv.claims) == 1
    assert srv.fails == [{"reason": "the runner was paused", "retryable": True, "shutdown": True, "pause": True}]
    last = runner.history.recent()[0]
    assert last["outcome"] == "paused" and last["error"] == "the runner was paused"


def test_a_job_claimed_while_pausing_goes_straight_back(tmp_path, fast, server, idle_fast, monkeypatch):  # noqa: F811
    monkeypatch.setattr(brush, "train", lambda *a, **k: pytest.fail("trained while paused"))
    srv = server(make_bundle(tmp_path))
    client = Client(srv.url, KEY)
    claim = client.claim

    def claim_then_pause(q):
        job = claim(q)
        runner.control.pause()  # the owner pressed pause during the long poll
        return job

    client.claim = claim_then_pause
    runner = Runner(client, str(tmp_path / "w"), FakeCaps())
    t, result = start(runner)
    until(lambda: srv.fails and srv.hellos[-1]["paused"])
    stop(runner, t, result)
    assert srv.fails == [{"reason": "the runner was paused", "retryable": True, "shutdown": True, "pause": True}]
    assert not [p for _, p, _, _ in srv.requests if p.endswith("/bundle")]


def test_a_finished_job_is_recorded_with_its_stages(tmp_path, fast, server, monkeypatch):  # noqa: F811
    monkeypatch.setattr(brush, "train", brush_ok)
    srv = server(make_bundle(tmp_path))
    srv.job = lambda: {**type(srv).job(srv), "wallId": "w1", "captureId": "c1"}
    runner = Runner(Client(srv.url, KEY), str(tmp_path / "w"), lambda: dict(CAPS), max_jobs=1)
    assert runner.run() == 0
    rec = runner.history.recent()[0]
    assert (rec["jobId"], rec["wallId"], rec["captureId"], rec["quality"]) == ("j1", "w1", "c1", "draft")
    assert rec["outcome"] == "succeeded" and rec["server"] == srv.url and rec["error"] is None
    assert {"download", "train", "upload"} <= set(rec["stages"]) and rec["durationS"] >= 0
    assert runner.snapshot()["jobs"][0]["jobId"] == "j1" and runner.snapshot()["current"] is None
