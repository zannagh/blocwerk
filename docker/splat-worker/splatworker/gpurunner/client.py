"""HTTP client for the Blocwerk runner API (stdlib only: urllib / http.client)."""
import hashlib
import json
import logging
import os
import random
import socket
import urllib.error
import urllib.parse
import urllib.request

from .. import __version__

log = logging.getLogger("gpurunner")
CLAIM_TIMEOUT_S = 45  # the server long-polls ~25 s
DEFAULT_TIMEOUT_S = 60
CHUNK = 1 << 20


def pause(event, seconds):
    """Waits `seconds` or until `event` is set (a shutdown ends a backoff at once). Patched by the tests."""
    event.wait(seconds)


class Unauthorized(Exception):
    """401: the key is invalid or was revoked."""


class Gone(Exception):
    """404 / 409 / 410 on a job: it is no longer this runner's (cancelled, requeued, finished)."""


class Rejected(Exception):
    """A 4xx the runner cannot fix by retrying (413 too large, 422 invalid, 400)."""

    def __init__(self, status, text):
        super().__init__(f"{status}: {text}")
        self.status = status


class Transient(Exception):
    """Network error, 5xx or 429: retry later. retry_after: the server's Retry-After (s) or None."""

    def __init__(self, message, retry_after=None):
        super().__init__(message)
        self.retry_after = retry_after


class Backoff:
    """Exponential 1 -> 60 s with +-20 % jitter; reset() after a success."""

    def __init__(self, first=1.0, cap=60.0):
        self.first, self.cap, self.n = first, cap, 0

    def next(self, retry_after=None):
        delay = min(self.cap, self.first * (2 ** min(self.n, 16))) * random.uniform(0.8, 1.2)
        self.n += 1
        return max(delay, float(retry_after or 0))

    def reset(self):
        self.n = 0


def check_server(url, insecure_http=False):
    """The base URL: https, or http only for localhost / 127.0.0.1 / ::1 or with --insecure-http."""
    u = urllib.parse.urlsplit(url.rstrip("/"))
    if u.scheme not in ("http", "https") or not u.hostname:
        raise ValueError(f"not a server URL: {url!r}")
    if u.scheme == "http" and u.hostname not in ("localhost", "127.0.0.1", "::1") and not insecure_http:
        raise ValueError("refusing plain http to a remote server (the key would travel in clear); "
                         "use https or pass --insecure-http")
    return urllib.parse.urlunsplit((u.scheme, u.netloc, u.path.rstrip("/"), "", ""))


def _retry_after(headers):
    try:
        return float(headers.get("Retry-After")) if headers and headers.get("Retry-After") else None
    except ValueError:
        return None


class Client:
    def __init__(self, server, key):
        if not key or not key.startswith("bwr_"):
            raise ValueError("BWR_KEY must be set to a runner key (bwr_...)")
        self.server, self.key = server, key

    def _request(self, method, path, body=None, headers=None, timeout=DEFAULT_TIMEOUT_S):
        h = {"Authorization": f"Bearer {self.key}", "User-Agent": f"blocwerk-runner/{__version__}"}
        h.update(headers or {})
        req = urllib.request.Request(self.server + path, data=body, headers=h, method=method)
        try:
            return urllib.request.urlopen(req, timeout=timeout)
        except urllib.error.HTTPError as e:
            text = e.read(2000).decode("utf-8", "replace") if e.fp else ""
            self._raise(e.code, text, e.headers)
        except (urllib.error.URLError, socket.timeout, ConnectionError, TimeoutError, OSError) as e:
            raise Transient(f"{method} {path}: {getattr(e, 'reason', e)}") from e

    @staticmethod
    def _raise(code, text, headers):
        if code == 401 or code == 403:
            raise Unauthorized(text or "unauthorized")
        if code in (404, 409, 410):
            raise Gone(f"{code}: {text}")
        if code == 429 or code >= 500:
            raise Transient(f"HTTP {code}: {text[:200]}", _retry_after(headers))
        raise Rejected(code, text[:500])

    def _json(self, method, path, doc, timeout=DEFAULT_TIMEOUT_S):
        body = json.dumps(doc).encode()
        with self._request(method, path, body, {"Content-Type": "application/json"}, timeout) as r:
            raw = r.read()
            return r.status, (json.loads(raw) if raw else None)

    def hello(self, caps):
        return self._json("POST", "/api/runners/hello", caps)[1] or {}

    def claim(self, max_quality):
        """The claimed job dict, or None (204: nothing to do)."""
        status, doc = self._json("POST", "/api/runners/claim", {"maxQuality": max_quality}, CLAIM_TIMEOUT_S)
        return doc if status == 200 and doc else None

    def progress(self, job_id, doc):
        return self._json("POST", f"/api/runners/jobs/{job_id}/progress", doc)[1] or {}

    def fail(self, job_id, reason, retryable):
        self._json("POST", f"/api/runners/jobs/{job_id}/fail", {"reason": str(reason)[:1000], "retryable": retryable})

    def download_bundle(self, job_id, dest, expected_bytes=None, expected_sha=None, max_bytes=8 << 30):
        """Streams the bundle to `dest`, checking size and sha256; returns the byte count."""
        digest, n = hashlib.sha256(), 0
        with self._request("GET", f"/api/runners/jobs/{job_id}/bundle", timeout=DEFAULT_TIMEOUT_S) as r, \
                open(dest, "wb") as fh:
            while chunk := r.read(CHUNK):
                n += len(chunk)
                if n > max_bytes:
                    raise Rejected(413, "bundle larger than the runner accepts")
                digest.update(chunk)
                fh.write(chunk)
        if expected_bytes is not None and n != int(expected_bytes):
            raise Transient(f"bundle truncated ({n} of {expected_bytes} bytes)")
        if expected_sha and digest.hexdigest() != str(expected_sha).lower():
            raise Transient("bundle checksum mismatch")
        return n

    def upload_result(self, job_id, path, fmt, stats):
        size = os.path.getsize(path)
        headers = {"Content-Type": "application/octet-stream", "Content-Length": str(size),
                   "X-Blocwerk-Format": fmt, "X-Blocwerk-Stats": json.dumps(stats, separators=(",", ":"))}
        with open(path, "rb") as fh:
            with self._request("PUT", f"/api/runners/jobs/{job_id}/result", fh, headers, timeout=600) as r:
                r.read()
        return size
