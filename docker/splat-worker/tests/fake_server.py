"""A scripted stand-in for the Blocwerk runner API (/api/runners/*, the contract of
src/Blocwerk.Web/Endpoints/RunnerApiEndpoints*.cs): hello, claim (204 when idle), a Range-capable bundle
download, progress, a result upload that decodes Content-Encoding: gzip, and fail. Tests script errors
per route (queues of (status, body, headers)) and read what the runner sent."""
import gzip
import hashlib
import json
import re
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


class FakeServer:
    def __init__(self, bundle=b"", job_id="j1", key=None, quality="draft"):
        self.bundle, self.job_id, self.key, self.quality = bundle, job_id, key, quality
        self.requests, self.results, self.progress, self.fails, self.hellos, self.claims = [], [], [], [], [], []
        self.jobs_left = 1
        self.errors = {"hello": [], "claim": [], "bundle": [], "progress": [], "result": [], "fail": []}
        self.drop_bundle_after = None  # bytes: the first download breaks off there (resume test)
        self.gone_on_stage = None  # progress with this stage answers 410
        self.on_progress = None  # callable(doc), e.g. to signal the runner mid-training
        self.lock = threading.Lock()
        self.httpd = ThreadingHTTPServer(("127.0.0.1", 0), self._handler())
        self.url = f"http://127.0.0.1:{self.httpd.server_address[1]}"
        threading.Thread(target=self.httpd.serve_forever, daemon=True).start()

    def close(self):
        self.httpd.shutdown()
        self.httpd.server_close()

    def job(self):
        return {"jobId": self.job_id, "quality": self.quality, "leaseSeconds": 300, "bundleBytes": len(self.bundle),
                "bundleSha256": hashlib.sha256(self.bundle).hexdigest()}

    def route(self, path):
        m = re.fullmatch(r"/api/runners/(hello|claim)|/api/runners/jobs/([^/]+)/(bundle|progress|result|fail)", path)
        if not m:
            return None, None
        return (m.group(1), None) if m.group(1) else (m.group(3), m.group(2))

    def respond(self, handler, method, path, body, headers):
        name, job = self.route(path)
        if name is None:
            return 404, {"error": "no route"}, {}
        if self.key and headers.get("Authorization") != f"Bearer {self.key}":
            return 401, {"title": "Unauthorized"}, {}
        if self.errors[name]:
            return self.errors[name].pop(0)
        if job is not None and job != self.job_id:
            return 404, {"title": "Not Found"}, {}
        return getattr(self, f"_{name}")(handler, body, headers)

    def _hello(self, _h, body, _headers):
        self.hellos.append(json.loads(body))
        return 200, {"runnerId": "r", "name": "test", "pollSeconds": 25}, {}

    def _claim(self, _h, body, _headers):
        self.claims.append(json.loads(body) if body else None)
        with self.lock:
            if self.jobs_left <= 0:
                return 204, None, {}
            self.jobs_left -= 1
        return 200, self.job(), {}

    def _bundle(self, handler, _body, headers):
        m = re.fullmatch(r"bytes=(\d+)-", headers.get("Range") or "")
        start = int(m.group(1)) if m else 0
        if start > len(self.bundle):
            return 416, None, {}
        part = self.bundle[start:]
        status = 206 if m else 200
        extra = {"Content-Type": "application/zip"}
        if m:
            extra["Content-Range"] = f"bytes {start}-{len(self.bundle) - 1}/{len(self.bundle)}"
        if self.drop_bundle_after is not None:  # headers promise everything, the connection breaks off
            cut, self.drop_bundle_after = self.drop_bundle_after, None
            return status, part[:cut], {**extra, "Content-Length": str(len(part)), "X-Drop": "1"}
        return status, part, extra

    def _progress(self, _h, body, _headers):
        doc = json.loads(body)
        self.progress.append(doc)
        if self.on_progress:
            self.on_progress(doc)
        if self.gone_on_stage and doc.get("stage") == self.gone_on_stage:
            return 410, {"title": "Gone"}, {}
        return 200, {"cancel": False, "leaseSeconds": 300}, {}

    def _result(self, _h, body, headers):
        encoding = (headers.get("Content-Encoding") or "").lower()
        decoded = gzip.decompress(body) if encoding == "gzip" else body
        self.results.append({"raw": body, "body": decoded, "headers": dict(headers)})
        return 200, {"ok": True}, {}

    def _fail(self, _h, body, _headers):
        self.fails.append(json.loads(body))
        return 200, {"ok": True}, {}

    def _handler(self):
        server = self

        class Handler(BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.1"

            def log_message(self, *a):
                pass

            def _any(self):
                n = int(self.headers.get("Content-Length") or 0)
                body = self.rfile.read(n) if n else b""
                server.requests.append((self.command, self.path, dict(self.headers), body))
                status, out, headers = server.respond(self, self.command, self.path, body, self.headers)
                raw = json.dumps(out).encode() if isinstance(out, (dict, list)) else (out or b"")
                self.send_response(status)
                drop = headers.pop("X-Drop", None)
                for k, v in headers.items():
                    self.send_header(k, v)
                if "Content-Length" not in headers:
                    self.send_header("Content-Length", str(len(raw)))
                if drop:
                    self.send_header("Connection", "close")
                self.end_headers()
                self.wfile.write(raw)
                if drop:
                    self.close_connection = True

            do_GET = do_POST = do_PUT = _any

        return Handler
