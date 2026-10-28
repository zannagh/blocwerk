"""Replay one wall-geometry job INSIDE a throwaway wall-geometry container and print its cgroup memory peak.

    python /measure/replay.py solve      REQ_DIR
    python /measure/replay.py solve-sfm  SFM_DIR            # request.json + sparse.zip
    python /measure/replay.py textures   REQ_DIR PHOTO_DIR QUALITY

Talks to the service on 127.0.0.1:8000 (the container runs with --network none), streams photos from disk (chunked
upload, so this client adds a few MB at most), polls to a terminal state and prints ONE JSON line: kind, status, seconds,
the container's sampled anonymous-memory peak (what the job processes hold), memory.peak (includes page cache) /
memory.max and its oom_kill count (cgroup v2, read from inside the container). Stdlib only:
it runs on the service image as is. `--self-check` builds a multipart body in memory and parses it back.
"""
import http.client
import json
import os
import sys
import threading
import time
import uuid

HOST, PORT = "127.0.0.1", 8000
CHUNK = 1 << 20


def cgroup(name):
    try:
        with open(f"/sys/fs/cgroup/{name}") as fh:
            return fh.read().strip()
    except OSError:
        return None


def oom_kills():
    for line in (cgroup("memory.events") or "").splitlines():
        k, _, v = line.partition(" ")
        if k == "oom_kill":
            return int(v)
    return None


def anon_bytes():
    for line in (cgroup("memory.stat") or "").splitlines():
        k, _, v = line.partition(" ")
        if k == "anon":
            return int(v)
    return 0


class AnonPeak(threading.Thread):
    """Samples the cgroup's anonymous memory (process memory without the page cache the uploaded photos fill: that
    cache is reclaimable and inflates memory.peak) every 0.2 s."""

    def __init__(self):
        super().__init__(daemon=True)
        self.peak, self.stop = 0, False

    def run(self):
        while not self.stop:
            self.peak = max(self.peak, anon_bytes())
            time.sleep(0.2)


def multipart(boundary, fields, files):
    """Yield the body: fields {name: (text, content type)}, files [(field, filename, path)]."""
    for name, (text, ctype) in fields.items():
        yield (f"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"; filename=\"{name}.json\"\r\n"
               f"Content-Type: {ctype}\r\n\r\n").encode() + text.encode() + b"\r\n"
    for field, filename, path in files:
        ctype = "application/zip" if filename.endswith(".zip") else "image/jpeg"
        yield (f"--{boundary}\r\nContent-Disposition: form-data; name=\"{field}\"; filename=\"{filename}\"\r\n"
               f"Content-Type: {ctype}\r\n\r\n").encode()
        with open(path, "rb") as fh:
            while chunk := fh.read(CHUNK):
                yield chunk
        yield b"\r\n"
    yield f"--{boundary}--\r\n".encode()


def request(method, path, body=None, headers=None):
    conn = http.client.HTTPConnection(HOST, PORT, timeout=600)
    conn.request(method, path, body=body, headers=headers or {}, encode_chunked=body is not None and not isinstance(body, bytes))
    resp = conn.getresponse()
    data = resp.read()
    conn.close()
    if resp.status >= 300:
        raise SystemExit(f"{method} {path}: {resp.status} {data[:400]!r}")
    return json.loads(data)


def submit(kind, args):
    if kind == "solve":
        with open(os.path.join(args[0], "solve.json"), "rb") as fh:
            return request("POST", "/v1/jobs/solve", fh.read(), {"Content-Type": "application/json"})
    boundary = uuid.uuid4().hex
    headers = {"Content-Type": f"multipart/form-data; boundary={boundary}"}
    if kind == "solve-sfm":
        with open(os.path.join(args[0], "request.json")) as fh:
            fields = {"request": (fh.read(), "application/json")}
        files = [("sparse", "sparse.zip", os.path.join(args[0], "sparse.zip"))]
    else:
        req_dir, photo_dir, quality = args
        with open(os.path.join(req_dir, "geometry.json")) as fh:
            fields = {"geometry": (fh.read(), "application/json")}
        with open(os.path.join(req_dir, "qualities.json")) as fh:
            options = json.load(fh)[quality]
        if options is not None:
            fields["options"] = (json.dumps(options), "application/json")
        with open(os.path.join(req_dir, "photos.json")) as fh:
            photos = json.load(fh)
        files = [("photos", f"{name}.jpg", os.path.join(photo_dir, stored)) for name, stored in sorted(photos.items())]
    return request("POST", f"/v1/jobs/{kind}", multipart(boundary, fields, files), headers)


def wait_ready():
    for _ in range(120):
        try:
            request("GET", "/health")
            return
        except (OSError, SystemExit):
            time.sleep(0.5)
    raise SystemExit("service did not start")


def main(kind, args):
    wait_ready()
    sampler = AnonPeak()
    sampler.start()
    t0 = time.time()
    job = submit(kind, args)["jobId"]
    while True:
        st = request("GET", f"/v1/jobs/{job}")
        if st["status"] in ("succeeded", "failed", "cancelled"):
            break
        time.sleep(2)
    sampler.stop = True
    result = st.get("result") or {}
    print(json.dumps({"kind": kind, "args": args[2:], "status": st["status"], "error": st.get("error"),
                      "failureKind": st.get("failureKind"), "seconds": round(time.time() - t0, 1),
                      "anonPeakMiB": round(sampler.peak / 2**20),
                      "peakMiB": round(int(cgroup("memory.peak") or 0) / 2**20), "maxMem": cgroup("memory.max"),
                      "oomKills": oom_kills(), "facets": len(result.get("facets") or [])}))


def self_check():
    import email.parser
    import tempfile
    with tempfile.NamedTemporaryFile(suffix=".jpg", delete=False) as fh:
        fh.write(b"\xff\xd8photo")
    body = b"".join(multipart("b0", {"geometry": ("{}", "application/json")}, [("photos", "p01.jpg", fh.name)]))
    os.unlink(fh.name)
    msg = email.parser.BytesParser().parsebytes(b"Content-Type: multipart/form-data; boundary=b0\r\n\r\n" + body)
    parts = [(p.get_param("name", header="content-disposition"), p.get_payload(decode=True)) for p in msg.get_payload()]
    assert parts == [("geometry", b"{}"), ("photos", b"\xff\xd8photo")], parts
    print("self-check ok")


if __name__ == "__main__":
    if sys.argv[1] == "--self-check":
        self_check()
    else:
        main(sys.argv[1], sys.argv[2:])
