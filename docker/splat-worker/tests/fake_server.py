"""A scripted stand-in for the Blocwerk runner API (tests/test_gpurunner.py)."""
import hashlib
import json
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


class FakeServer:
    """routes: {(method, path): handler(req) -> (status, body bytes|dict|None, headers)}; every request
    is recorded as (method, path, headers, body)."""

    def __init__(self, bundle=b"", job_id="j1"):
        self.bundle, self.job_id = bundle, job_id
        self.requests, self.results, self.progress, self.fails = [], [], [], []
        self.jobs_left, self.cancel_on_train, self.hello_errors, self.claim_errors = 1, False, [], []
        self.lock = threading.Lock()
        self.httpd = ThreadingHTTPServer(("127.0.0.1", 0), self._handler())
        self.url = f"http://127.0.0.1:{self.httpd.server_address[1]}"
        threading.Thread(target=self.httpd.serve_forever, daemon=True).start()

    def close(self):
        self.httpd.shutdown()
        self.httpd.server_close()

    def job(self):
        return {"jobId": self.job_id, "quality": "draft", "leaseSeconds": 300, "bundleBytes": len(self.bundle),
                "bundleSha256": hashlib.sha256(self.bundle).hexdigest()}

    def respond(self, method, path, body, headers):
        base = f"/api/runners/jobs/{self.job_id}"
        if path == "/api/runners/hello":
            return self.hello_errors.pop(0) if self.hello_errors else (200, {"runnerId": "r", "name": "test",
                                                                              "pollSeconds": 25}, {})
        if path == "/api/runners/claim":
            if self.claim_errors:
                return self.claim_errors.pop(0)
            with self.lock:
                if self.jobs_left <= 0:
                    return 204, None, {}
                self.jobs_left -= 1
            return 200, self.job(), {}
        if path == base + "/bundle":
            return 200, self.bundle, {"Content-Type": "application/zip"}
        if path == base + "/progress":
            doc = json.loads(body)
            self.progress.append(doc)
            return 200, {"cancel": self.cancel_on_train and doc["stage"] == "train", "leaseSeconds": 300}, {}
        if path == base + "/result":
            self.results.append((body, dict(headers)))
            return 200, {"ok": True}, {}
        if path == base + "/fail":
            self.fails.append(json.loads(body))
            return 200, {}, {}
        return 404, {"error": "no route"}, {}

    def _handler(self):
        server = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *a):
                pass

            def _any(self):
                n = int(self.headers.get("Content-Length") or 0)
                body = self.rfile.read(n) if n else b""
                server.requests.append((self.command, self.path, dict(self.headers), body))
                status, out, headers = server.respond(self.command, self.path, body, self.headers)
                raw = json.dumps(out).encode() if isinstance(out, (dict, list)) else (out or b"")
                self.send_response(status)
                for k, v in headers.items():
                    self.send_header(k, v)
                self.send_header("Content-Length", str(len(raw)))
                self.end_headers()
                self.wfile.write(raw)

            do_GET = do_POST = do_PUT = _any

        return Handler
