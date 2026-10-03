"""The runner's local status page and pause switch (stdlib http.server on its own thread; page.py renders it).

    GET  /             the page (works without JavaScript; with it, it refreshes itself every 2 s)
    GET  /fragment     the page's live part (the refresh)
    GET  /status.json  the same as JSON (Runner.snapshot: no key, no secrets)
    POST /pause        form field mode=now (stop the running job, handed back for free) or after-job
    POST /resume

RUNNER_UI_PORT (8190; 0 = off) and RUNNER_UI_HOST (127.0.0.1). It binds to loopback by default. In Docker the
container's loopback is not the host's, so bind 0.0.0.0 inside and publish to the host's loopback only:
`-e RUNNER_UI_HOST=0.0.0.0 -p 127.0.0.1:8190:8190`. Reading needs nothing; the switch (POST) needs the local token
(uitoken.py: the link with `?token=` in the runner log sets a cookie; scripts send `X-Runner-Token`), so another
container on the same Docker network cannot pause the runner. Requests naming another host (DNS rebinding) and
cross-site form posts are refused. Two runners on one machine: one port each."""
import json
import logging
import os
import socket
import threading
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from . import page, uitoken

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


def is_loopback(address):
    """A peer on this machine's (or, in Docker, this container's) own loopback; a browser on the Docker host comes
    through the bridge, so it needs the token link."""
    return address in ("127.0.0.1", "::1", "::ffff:127.0.0.1")


class _Server(ThreadingHTTPServer):
    daemon_threads = True

    def __init__(self, address, handler, family):
        self.address_family = family
        super().__init__(address, handler)


class StatusPage:
    def __init__(self, runner, host=DEFAULT_HOST, port=DEFAULT_PORT, token=None):
        self.runner, self.token = runner, token or uitoken.load_or_create(os.path.dirname(runner.control.path))
        self.allowed = LOCAL_NAMES | ({host.lower()} if host not in ("0.0.0.0", "::", "") else set())
        family = socket.AF_INET6 if ":" in host else socket.AF_INET
        self.httpd = _Server((host, port), self._handler(), family)
        self.host, self.port = host, self.httpd.server_address[1]
        self.thread = threading.Thread(target=self.httpd.serve_forever, name="status-page", daemon=True)
        self.thread.start()

    @property
    def url(self):
        host = {"0.0.0.0": "127.0.0.1", "": "127.0.0.1", "::": "::1"}.get(self.host, self.host)
        host = f"[{host}]" if ":" in host else host
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

            def _from_loopback(self):
                return is_loopback(self.client_address[0])

            def _authorized(self):
                return (uitoken.matches(status.token, self.headers.get("X-Runner-Token"))
                        or uitoken.matches(status.token, uitoken.from_cookie(self.headers.get("Cookie"))))

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
                path, _, query = self.path.partition("?")
                if path == "/status.json":
                    body = json.dumps(status.runner.snapshot(), default=str).encode()
                    return self._send(200, body, "application/json")
                if path not in ("/", "/fragment"):
                    return self._send(404, b"not found", "text/plain")
                given = (urllib.parse.parse_qs(query).get("token") or [None])[0]
                if given is not None:  # the link from the log: remember it, drop it from the address bar
                    if not uitoken.matches(status.token, given):
                        return self._send(403, b"wrong token: use the link in the runner log", "text/plain")
                    cookie = uitoken.set_cookie(status.token)
                    return self._send(303, b"", "text/plain", {"Location": path, "Set-Cookie": cookie})
                authorized = self._authorized()
                grant = not authorized and self._from_loopback()  # this machine's own browser (native runner)
                snap = status.runner.snapshot()
                can = authorized or grant
                html = page.render(snap, can) if path == "/" else page.render_main(snap, can)
                extra = {"Set-Cookie": uitoken.set_cookie(status.token)} if grant else None
                return self._send(200, html.encode(), extra=extra)

            do_HEAD = do_GET

            def do_POST(self):  # noqa: N802
                if not self._local() or not self._same_origin():
                    return self._send(403, b"forbidden", "text/plain")
                if not self._authorized():
                    return self._send(403, b"the pause switch needs the token: open the link in the runner log",
                                      "text/plain")
                try:
                    n = max(0, int(self.headers.get("Content-Length") or 0))
                except ValueError:
                    return self._send(400, b"bad Content-Length", "text/plain")
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
    log.info("status page: %s ; to use its pause switch open %s?token=%s once (sets a cookie; token kept in %s)",
             ui.url, ui.url, ui.token, os.path.join(os.path.dirname(runner.control.path), "ui-token"))
    return ui
