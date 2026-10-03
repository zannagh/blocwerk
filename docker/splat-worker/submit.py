#!/usr/bin/env python3
"""Minimal client (stdlib only) for manual runs: submit photos, poll with progress, download results.

  COMPUTE_API_KEY=... ./submit.py --url http://127.0.0.1:8100 --geometry wall-geometry.json \
      --options '{"maxSteps": 5000}' --out ./result photos/*.jpg
"""
import argparse
import json
import mimetypes
import os
import sys
import time
import urllib.error
import urllib.request
import uuid


def _req(url, key, data=None, headers=None, method=None):
    h = dict(headers or {})
    if key:
        h["Authorization"] = f"Bearer {key}"
    return urllib.request.Request(url, data=data, headers=h, method=method)


def _multipart(photos, geometry, options):
    boundary = uuid.uuid4().hex
    parts = []

    def add(name, payload, filename=None, ctype="application/json"):
        disp = f'form-data; name="{name}"' + (f'; filename="{filename}"' if filename else "")
        parts.append(f"--{boundary}\r\nContent-Disposition: {disp}\r\nContent-Type: {ctype}\r\n\r\n".encode()
                     + payload + b"\r\n")
    if options:
        add("options", options.encode())
    if geometry:
        add("geometry", open(geometry, "rb").read(), "geometry.json")
    for p in photos:
        add("photos", open(p, "rb").read(), os.path.basename(p), mimetypes.guess_type(p)[0] or "image/jpeg")
    return b"".join(parts) + f"--{boundary}--\r\n".encode(), f"multipart/form-data; boundary={boundary}"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("photos", nargs="+")
    ap.add_argument("--url", default="http://127.0.0.1:8100")
    ap.add_argument("--geometry")
    ap.add_argument("--options")
    ap.add_argument("--out", default="splat-result")
    a = ap.parse_args()
    key = os.environ.get("COMPUTE_API_KEY")
    body, ctype = _multipart(a.photos, a.geometry, a.options)
    t0 = time.time()
    try:
        with urllib.request.urlopen(_req(f"{a.url}/v1/jobs/splat", key, body, {"Content-Type": ctype})) as r:
            job = json.load(r)["jobId"]
    except urllib.error.HTTPError as e:
        sys.exit(f"submit failed: {e.code} {e.read().decode()}")
    print(f"job {job} accepted after {time.time() - t0:.1f} s upload", flush=True)
    last = None
    while True:
        with urllib.request.urlopen(_req(f"{a.url}/v1/jobs/{job}", key)) as r:
            st = json.load(r)
        line = f"{st['status']:9} {st['progress'] * 100:5.1f}%  {st['stage']}  {st.get('stageDetail') or ''}"
        if line != last:
            print(f"[{time.time() - t0:7.1f}s] {line}", flush=True)
            last = line
        if st["status"] in ("succeeded", "failed", "cancelled"):
            break
        time.sleep(3)
    if st["status"] != "succeeded":
        sys.exit(f"{st['status']}: {st['error'] or st['message']}")
    os.makedirs(a.out, exist_ok=True)
    for f in st["result"]["files"]:
        with urllib.request.urlopen(_req(a.url + f["url"], key)) as r, open(os.path.join(a.out, f["name"]), "wb") as fh:
            fh.write(r.read())
        print("downloaded", os.path.join(a.out, f["name"]))
    print(json.dumps(st["result"]["stats"], indent=1))


if __name__ == "__main__":
    main()
