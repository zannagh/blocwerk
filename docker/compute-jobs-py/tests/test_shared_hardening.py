"""Security hardening of the shared package: SSRF guard, strict JSON, error text, env, entry point."""
import os
import subprocess
import sys
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer

import pytest

from computejobs import netguard
from computejobs.child import safe_message, tool_env
from computejobs.settings import settings
from test_protocol import client, svc, wait  # noqa: F401 - fixtures


@pytest.fixture
def strict(monkeypatch):
    monkeypatch.setattr(settings, "callback_allow_private", False)
    monkeypatch.setattr(settings, "callback_allowed_hosts", "")


@pytest.mark.parametrize("url", [
    "ftp://example.org/x", "file:///etc/passwd", "gopher://x", "http://", "http://user:pw@example.org/",
    "https://example.org/a b", "http://example.org:99999/", "javascript:alert(1)", "x" * 3000])
def test_callback_url_syntax_is_refused(strict, url):
    with pytest.raises(netguard.UnsafeTarget):
        netguard.check_literal(url)


@pytest.mark.parametrize("ip", [
    "169.254.169.254", "169.254.0.1", "fe80::1", "fd00:ec2::254", "100.100.100.200", "0.0.0.0", "::",
    "224.0.0.1", "::ffff:169.254.169.254", "168.63.129.16"])
def test_metadata_and_link_local_never_allowed(monkeypatch, ip):
    monkeypatch.setattr(settings, "callback_allow_private", True)
    monkeypatch.setattr(settings, "callback_allowed_hosts", ip)
    with pytest.raises(netguard.UnsafeTarget):
        netguard.check_ip(ip, ip)


@pytest.mark.parametrize("ip", ["127.0.0.1", "10.1.2.3", "172.20.0.5", "192.168.0.10", "100.64.1.1",
                                "::1", "fd12::1", "::ffff:10.0.0.1"])
def test_private_only_when_allowed(strict, monkeypatch, ip):
    with pytest.raises(netguard.UnsafeTarget):
        netguard.check_ip(ip, "h")
    monkeypatch.setattr(settings, "callback_allowed_hosts", "blocwerk, other")
    netguard.check_ip(ip, "blocwerk")  # named host allowed
    with pytest.raises(netguard.UnsafeTarget):
        netguard.check_ip(ip, "h")
    monkeypatch.setattr(settings, "callback_allow_private", True)
    netguard.check_ip(ip, "h")


def test_public_address_allowed(strict):
    netguard.check_ip("93.184.216.34", "example.org")
    netguard.check_literal("https://example.org/cb?x=1")  # names are checked at send time


def test_dns_name_is_resolved_and_checked(strict):
    with pytest.raises(netguard.UnsafeTarget):  # localhost resolves to loopback
        netguard.resolve("localhost", 80)
    with pytest.raises(netguard.UnsafeTarget):
        netguard.post("http://localhost:9/cb", b"{}", {}, 1)


def test_submission_refuses_private_literal(client, strict):
    r = client.post("/v1/jobs/echo", json={"callbackUrl": "http://169.254.169.254/latest/meta-data/"})
    assert r.status_code == 400 and "blocked" in r.json()["detail"]
    r = client.post("/v1/jobs/echo", json={"callbackUrl": "http://127.0.0.1:8080/cb"})
    assert r.status_code == 400 and "private" in r.json()["detail"]


def test_redirects_are_not_followed(monkeypatch):
    hits = []

    class H(BaseHTTPRequestHandler):
        def do_POST(self):
            hits.append(self.path)
            self.rfile.read(int(self.headers["Content-Length"]))
            self.send_response(307)
            self.send_header("Location", f"http://127.0.0.1:{self.server.server_port}/followed")
            self.send_header("Content-Length", "0")
            self.end_headers()

        def log_message(self, *a):
            pass

    srv = HTTPServer(("127.0.0.1", 0), H)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    monkeypatch.setattr(settings, "callback_allow_private", True)
    status = netguard.post(f"http://127.0.0.1:{srv.server_port}/cb", b"{}", {"Content-Type": "application/json"}, 2)
    srv.shutdown()
    assert status == 307 and hits == ["/cb"]


def test_connection_is_pinned_to_the_checked_address(monkeypatch):
    """The name is resolved once; a second (rebinding) answer is never used."""
    answers = iter(["93.184.216.34", "169.254.169.254"])
    monkeypatch.setattr(netguard.socket, "getaddrinfo",
                        lambda host, port, **kw: [(None, None, None, "", (next(answers), port))])
    connected = []

    def fake_connect(addr, timeout=None):
        connected.append(addr)
        raise OSError("stop here")
    monkeypatch.setattr(netguard.socket, "create_connection", fake_connect)
    with pytest.raises(OSError):
        netguard.post("http://rebind.example/cb", b"{}", {}, 1)
    assert connected == [("93.184.216.34", 80)]


@pytest.mark.parametrize("body", [b'{"x": NaN}', b'{"x": Infinity}', b'{"x": -Infinity}'])
def test_json_constants_are_refused(client, body):
    r = client.post("/v1/jobs/echo", content=body, headers={"Content-Type": "application/json"})
    assert r.status_code in (400, 422)


def test_error_text_hides_paths_and_internals(client):
    assert "<job>" in safe_message("cannot open /var/lib/jobs/abc/in.json", "/var/lib/jobs/abc")
    msg = safe_message("tool /opt/brush/brush_app failed reading /Users/me/secret/photo.jpg")
    assert "/opt" not in msg and "/Users" not in msg and "brush_app" in msg and "photo.jpg" in msg
    assert len(safe_message("x" * 5000)) <= 400
    r = client.post("/v1/jobs/echo", json={"mode": "crash"})
    st = wait(client, r.json()["jobId"])
    assert st["status"] == "failed" and st["error"].startswith("internal error (ref ")
    assert "Traceback" not in st["error"] and "/" not in st["error"].split("ref")[0]


def test_tool_env_is_minimal(monkeypatch):
    monkeypatch.setenv("COMPUTE_API_KEY", "leak")
    monkeypatch.setenv("COMPUTE_CALLBACK_SECRET", "leak")
    monkeypatch.setenv("AWS_SECRET_ACCESS_KEY", "leak")
    monkeypatch.setenv("VK_ICD_FILENAMES", "/x.json")
    env = tool_env({"EXTRA": "1"})
    assert "PATH" in env and env["VK_ICD_FILENAMES"] == "/x.json" and env["EXTRA"] == "1"
    assert not any(v == "leak" for v in env.values())


def test_secrets_are_scrubbed_from_the_environment():
    """A fresh interpreter: after loading settings, nothing it starts can see the secrets."""
    here = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    code = ("import os, subprocess, sys; from computejobs.settings import settings; "
            "settings.load(); assert settings.api_key == 'k1' and settings.callback_secret == 's1'; "
            "assert 'COMPUTE_API_KEY' not in os.environ; "
            "out = subprocess.run([sys.executable, '-c', 'import os; print(os.environ.get(\"COMPUTE_API_KEY\"), "
            "os.environ.get(\"COMPUTE_CALLBACK_SECRET\"))'], capture_output=True, text=True).stdout; "
            "print(out.strip())")
    env = {**os.environ, "COMPUTE_API_KEY": "k1", "COMPUTE_CALLBACK_SECRET": "s1", "PYTHONPATH": here}
    r = subprocess.run([sys.executable, "-c", code], capture_output=True, text=True, env=env)
    assert r.returncode == 0, r.stderr
    assert r.stdout.strip() == "None None"


def test_entry_point_refuses_open_bind(tmp_path):
    here = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    env = {k: v for k, v in os.environ.items() if not k.startswith(("COMPUTE_", "ALLOW_OPEN"))}
    env.update({"HOST": "0.0.0.0", "PYTHONPATH": here})
    code = "from computejobs.serve import serve; serve('x:app', 'svc', 8999)"
    r = subprocess.run([sys.executable, "-c", code], capture_output=True, text=True, env=env, timeout=30)
    assert r.returncode == 2 and "refusing to listen on 0.0.0.0" in r.stderr


def test_no_openapi_docs(client):
    for path in ("/docs", "/redoc", "/openapi.json"):
        assert client.get(path).status_code == 404
