"""HTTP client for the Blocwerk runner API (stdlib only: urllib / http.client).

Status codes: 401/403 = the key is refused (Unauthorized: the runner exits 3); 404/410 = the job is no
longer this runner's (Gone: drop it, go on); 408/409/429/5xx and network errors = Transient (retry,
honouring Retry-After); anything else 4xx = Rejected (413 too large, 415 encoding, 422 invalid, 400).
"""
import gzip
import hashlib
import http.client
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
UPLOAD_TIMEOUT_S = 600
CHUNK = 1 << 20
NETWORK_ERRORS = (urllib.error.URLError, socket.timeout, ConnectionError, TimeoutError, OSError,
                  http.client.HTTPException)


def pause(event, seconds):
    """Waits `seconds` or until `event` is set (a shutdown ends a backoff at once). Patched by the tests."""
    event.wait(seconds)


class Unauthorized(Exception):
    """401 / 403: the key is invalid or was revoked."""


class Gone(Exception):
    """404 / 410 on a job: it is no longer this runner's (cancelled, requeued, lost eligibility)."""


class Stopped(Exception):
    """The transfer was interrupted on purpose (the job's stop event)."""


class Rejected(Exception):
    """A 4xx the runner cannot fix by retrying (413 too large, 415 encoding, 422 invalid, 400)."""

    def __init__(self, status, text):
        super().__init__(f"{status}: {text}")
        self.status = status


class Transient(Exception):
    """Network error, 408 / 409 / 429 / 5xx: retry later. retry_after: the server's Retry-After (s) or None."""

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


class StoppableReader:
    """A file for urllib's request body that aborts the upload once `stop` is set."""

    def __init__(self, fh, stop):
        self.fh, self.stop = fh, stop

    def read(self, n=-1):
        if self.stop is not None and self.stop.is_set():
            raise Stopped("upload stopped")
        return self.fh.read(n)


def gzip_file(src, dest, stop=None, level=6):
    """Compresses src to dest in chunks (checks `stop` between them); returns dest's size."""
    with open(src, "rb") as fin, gzip.GzipFile(dest, "wb", compresslevel=level, mtime=0) as fout:
        while chunk := fin.read(8 * CHUNK):
            if stop is not None and stop.is_set():
                raise Stopped("compression stopped")
            fout.write(chunk)
    return os.path.getsize(dest)


class Client:
    def __init__(self, server, key, gzip_upload=True):
        if not key or not key.startswith("bwr_"):
            raise ValueError("BWR_KEY must be set to a runner key (bwr_...)")
        self.server, self.key, self.gzip_upload = server, key, gzip_upload

    def _request(self, method, path, body=None, headers=None, timeout=DEFAULT_TIMEOUT_S):
        h = {"Authorization": f"Bearer {self.key}", "User-Agent": f"blocwerk-runner/{__version__}"}
        h.update(headers or {})
        req = urllib.request.Request(self.server + path, data=body, headers=h, method=method)
        try:
            return urllib.request.urlopen(req, timeout=timeout)
        except urllib.error.HTTPError as e:
            text = e.read(2000).decode("utf-8", "replace") if e.fp else ""
            self._raise(e.code, text, e.headers)
        except Stopped:
            raise
        except NETWORK_ERRORS as e:
            raise Transient(f"{method} {path}: {getattr(e, 'reason', e)}") from e

    @staticmethod
    def _raise(code, text, headers):
        if code in (401, 403):
            raise Unauthorized(text[:200] or "unauthorized")
        if code in (404, 410):
            raise Gone(f"{code}: {text[:200]}")
        if code in (408, 409, 429) or code >= 500:
            raise Transient(f"HTTP {code}: {text[:200]}", _retry_after(headers) or (10 if code == 409 else None))
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

    def fail(self, job_id, reason, retryable, shutdown=False):
        self._json("POST", f"/api/runners/jobs/{job_id}/fail",
                   {"reason": str(reason)[:1000], "retryable": bool(retryable), "shutdown": bool(shutdown)})

    def download_bundle(self, job_id, dest, expected_bytes=None, expected_sha=None, max_bytes=8 << 30, stop=None):
        """Streams the bundle to `dest`, resuming a partial file with a Range request; checks the size and
        sha256 at the end (a mismatch deletes the file). Returns the byte count."""
        have = os.path.getsize(dest) if os.path.exists(dest) else 0
        if expected_bytes is not None and have > int(expected_bytes):
            have = 0
        if expected_bytes is None or have < int(expected_bytes):
            have = self._fetch(job_id, dest, have, max_bytes, stop)
        if expected_bytes is not None and have != int(expected_bytes):
            raise Transient(f"bundle truncated ({have} of {expected_bytes} bytes)")
        if expected_sha and _sha256(dest) != str(expected_sha).lower():
            os.remove(dest)
            raise Transient("bundle checksum mismatch")
        return have

    def _fetch(self, job_id, dest, have, max_bytes, stop):
        headers = {"Range": f"bytes={have}-"} if have else {}
        try:
            r = self._request("GET", f"/api/runners/jobs/{job_id}/bundle", headers=headers)
        except Rejected as e:
            if e.status == 416:  # the partial file does not fit the bundle: start over
                os.remove(dest)
                raise Transient("bundle range refused; restarting the download") from e
            raise
        with r:
            if r.status != 206:
                have = 0
            elif have:
                log.info("resuming the bundle download at %.1f MB", have / 1e6)
            with open(dest, "ab" if have else "wb") as fh:
                try:
                    while chunk := r.read(CHUNK):
                        if stop is not None and stop.is_set():
                            raise Stopped("download stopped")
                        have += len(chunk)
                        if have > max_bytes:
                            raise Rejected(413, "bundle larger than the runner accepts")
                        fh.write(chunk)
                except NETWORK_ERRORS as e:
                    raise Transient(f"bundle download interrupted at {have} bytes: {e}") from e
        return have

    def upload_result(self, job_id, path, stats, stop=None):
        """PUT the trained scene (gzip-compressed on the fly to a temp file unless disabled or refused with
        415). Returns the bytes sent."""
        if self.gzip_upload:
            gz = path + ".gz"
            if not os.path.exists(gz):
                gzip_file(path, gz + ".part", stop)
                os.replace(gz + ".part", gz)
            try:
                return self._put(job_id, gz, stats, {"Content-Encoding": "gzip"}, stop)
            except Rejected as e:
                if e.status != 415:
                    raise
                log.warning("the server refused a gzip upload (415): sending it uncompressed")
                self.gzip_upload = False
        return self._put(job_id, path, stats, {}, stop)

    def _put(self, job_id, path, stats, extra, stop):
        size = os.path.getsize(path)
        headers = {"Content-Type": "application/octet-stream", "Content-Length": str(size),
                   "X-Blocwerk-Stats": json.dumps(stats, separators=(",", ":")), **extra}
        with open(path, "rb") as fh:
            body = StoppableReader(fh, stop)
            with self._request("PUT", f"/api/runners/jobs/{job_id}/result", body, headers, UPLOAD_TIMEOUT_S) as r:
                r.read()
        return size


def _sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as fh:
        while chunk := fh.read(CHUNK):
            digest.update(chunk)
    return digest.hexdigest()
