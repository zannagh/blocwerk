"""The runner's local status page and pause switch (stdlib http.server on its own thread; page.py renders it).

    GET  /             the page (works without JavaScript; with it, it refreshes itself every 2 s)
    GET  /fragment     the page's live part (the refresh)
    GET  /status.json  the same as JSON (Runner.snapshot: no key, no secrets)
    POST /pause        form field mode=now (stop the running job, handed back for free) or after-job
    POST /resume

RUNNER_UI_PORT (8190; 0 = off) and RUNNER_UI_HOST (127.0.0.1). No authentication: it binds to loopback by
default. In Docker the container's loopback is not the host's, so bind 0.0.0.0 inside and publish to the host's
loopback only: `-e RUNNER_UI_HOST=0.0.0.0 -p 127.0.0.1:8190:8190`. Requests naming another host (DNS rebinding)
and cross-site form posts are refused. Two runners on one machine: one port each."""
import json
import logging
import os
import socket
import threading
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from . import page

log = logging.getLogger("gpurunner")
DEFAULT_HOST, DEFAULT_PORT = "127.0.0.1", 8190
LOCAL_NAMES = {"localhost", "127.0.0.1", "::1"}
MAX_FORM_BYTES = 1024
HEADERS = {"Cache-Control": "no-store", "X-Content-Type-Options": "nosniff", "X-Frame-Options": "DENY",
           "Referrer-Policy": "no-referrer",
           "Content-Security-Policy": "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; "
                                      "connect-src 'self'; form-action 'self'; frame-ancestors 'none'"}


def ui_settings(env=None):
    """(host, port); port 0 = no status page."""
    env = os.environ if env is None else env
    host = (env.get("RUNNER_UI_HOST") or DEFAULT_HOST).strip()
    try:
        port = int(str(env.get("RUNNER_UI_PORT", DEFAULT_PORT)).strip() or DEFAULT_PORT)
    except ValueError:
        log.warning("RUNNER_UI_PORT=%r is not a port: using %d", env.get("RUNNER_UI_PORT"), DEFAULT_PORT)
        port = DEFAULT_PORT
    return host, max(0, port)


def _hostname(value):
    """The host of a Host header / an Origin's netloc, without port or IPv6 brackets."""
    value = (value or "").strip().lower()
    if value.startswith("["):
        return value[1:value.find("]")] if "]" in value else value
    return value.rsplit(":", 1)[0] if value.count(":") == 1 else value


class _Server(ThreadingHTTPServer):
    daemon_threads = True

    def __init__(self, address, handler, family):
        self.address_family = family
        super().__init__(address, handler)


class StatusPage:
    def __init__(self, runner, host=DEFAULT_HOST, port=DEFAULT_PORT):
        self.runner = runner
        self.allowed = LOCAL_NAMES | ({host.lower()} if host not in ("0.0.0.0", "::", "") else set())
        family = socket.AF_INET6 if ":" in host else socket.AF_INET
        self.httpd = _Server((host, port), self._handler(), family)
        self.host, self.port = host, self.httpd.server_address[1]
        self.thread = threading.Thread(target=self.httpd.serve_forever, name="status-page", daemon=True)
        self.thread.start()

    @property
    def url(self):
        host = f"[{self.host}]" if ":" in self.host else self.host
        return f"http://{host}:{self.port}/"

    def close(self):
        self.httpd.shutdown()
        self.httpd.server_close()

    def _handler(self):
        status = self

        class Handler(BaseHTTPRequestHandler):
            server_version = "blocwerk-runner"
            sys_version = ""

            def log_message(self, *a):
                pass

            def _send(self, code, body=b"", ctype="text/html; charset=utf-8", extra=None):
                self.send_response(code)
                headers = {**HEADERS, "Content-Type": ctype, "Content-Length": str(len(body)), **(extra or {})}
                for k, v in headers.items():
                    self.send_header(k, v)
                self.end_headers()
                if self.command != "HEAD":
                    self.wfile.write(body)

            def _local(self):
                return _hostname(self.headers.get("Host")) in status.allowed

            def _same_origin(self):
                if (self.headers.get("Sec-Fetch-Site") or "same-origin") not in ("same-origin", "none"):
                    return False
                origin = self.headers.get("Origin")
                if not origin or origin == "null":
                    return origin is None
                return urllib.parse.urlsplit(origin).netloc.lower() == (self.headers.get("Host") or "").lower()

            def do_GET(self):  # noqa: N802 - http.server's naming
                if not self._local():
                    return self._send(403, b"forbidden host", "text/plain")
                path = self.path.split("?", 1)[0]
                if path == "/status.json":
                    body = json.dumps(status.runner.snapshot(), default=str).encode()
                    return self._send(200, body, "application/json")
                if path in ("/", "/fragment"):
                    snap = status.runner.snapshot()
                    html = page.render(snap) if path == "/" else page.render_main(snap)
                    return self._send(200, html.encode())
                return self._send(404, b"not found", "text/plain")

            do_HEAD = do_GET

            def do_POST(self):  # noqa: N802
                if not self._local() or not self._same_origin():
                    return self._send(403, b"forbidden", "text/plain")
                n = int(self.headers.get("Content-Length") or 0)
                if n > MAX_FORM_BYTES:
                    return self._send(413, b"too large", "text/plain")
                form = urllib.parse.parse_qs(self.rfile.read(n).decode("utf-8", "replace")) if n else {}
                path = self.path.split("?", 1)[0]
                control = status.runner.control
                if path == "/pause":
                    control.pause(now=(form.get("mode") or ["now"])[0] != "after-job")
                elif path == "/resume":
                    control.resume()
                else:
                    return self._send(404, b"not found", "text/plain")
                return self._send(303, b"", "text/plain", {"Location": "/"})

        return Handler


def start_status_page(runner, env=None):
    """The running StatusPage, or None (RUNNER_UI_PORT=0, or the port is taken: the runner works on without it)."""
    host, port = ui_settings(env)
    if port == 0:
        return None
    try:
        ui = StatusPage(runner, host, port)
    except OSError as e:
        log.warning("status page not started on %s:%d (%s): set RUNNER_UI_PORT to a free port (0 = off)", host, port, e)
        return None
    log.info("status page and pause switch: %s", ui.url)
    return ui
