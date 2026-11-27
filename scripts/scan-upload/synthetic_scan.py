#!/usr/bin/env python3
# Copyright (c) 2026, zannagh. All rights reserved.
# See License in the project root for license information.
"""Build a synthetic iPhone scan package (blocwerk-scan/1) and upload it to a Blocwerk instance.

    synthetic_scan.py build  --wall WALL_ID --out DIR [--frames 300] [--video-mb 4]
    synthetic_scan.py upload --base https://host --wall WALL_ID --dir DIR [--stop-after N] [--corrupt NAME]

The upload reads the personal API key (write access) from BLOCWERK_API_KEY. It begins with the manifest, PUTs only the
files the server says are missing, then commits. Redirects are refused, so the key is never sent anywhere else. Run it again after an interruption: it sends only what is still
missing. --stop-after N stops after N files (to test resume); --corrupt NAME sends that file with one byte flipped and
expects the server to refuse it. The video pieces are random bytes, not real HEVC: the server does not decode video yet.
Standard library only.
"""
import argparse
import hashlib
import json
import os
import random
import struct
import sys
import urllib.error
import urllib.parse
import urllib.request
import uuid
import zlib

START = 1000.0
FPS = 30
DEPTH_EVERY = 6  # depth at 5 Hz


def png(width, height, bit_depth, rows):
    """A greyscale PNG (bit depth 8 or 16) from rows of bytes."""
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)
    raw = b"".join(b"\x00" + row for row in rows)
    header = struct.pack(">IIBBBBB", width, height, bit_depth, 0, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b"")


def depth_png(distance_mm):
    rows = [b"".join(struct.pack(">H", distance_mm + (x % 7)) for x in range(256)) for _ in range(192)]
    return png(256, 192, 16, rows)


def confidence_png():
    return png(256, 192, 8, [bytes([2] * 256) for _ in range(192)])


def mesh_ply():
    vertices = [(-2.0, 0.0, -1.5), (2.0, 0.0, -1.5), (2.0, 3.0, -1.5), (-2.0, 3.0, -1.5)]
    header = ("ply\nformat binary_little_endian 1.0\nelement vertex 4\nproperty float x\nproperty float y\n"
              "property float z\nelement face 2\nproperty list uchar int vertex_indices\nend_header\n").encode()
    body = b"".join(struct.pack("<fff", *v) for v in vertices)
    body += struct.pack("<Biii", 3, 0, 1, 2) + struct.pack("<Biii", 3, 0, 2, 3)
    return header + body


def pose(i):
    """Walking sideways along the wall at 1.5 m distance; identity rotation, column-major camera-to-world."""
    return [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, -1.8 + 3.6 * i / 300, 1.5, 0.0, 1]


def build(args):
    rng = random.Random(args.seed)
    files = {}
    seconds = args.frames / FPS
    pieces = max(1, int(seconds // 60) + 1)
    for p in range(pieces):
        files[f"video/seg0-{p:03d}.mov"] = rng.randbytes(args.video_mb * 1024 * 1024)
    frames, depth = [], []
    for i in range(args.frames):
        t = START + i / FPS
        frames.append({"t": t, "segment": 0, "pose": pose(i),
                       "intrinsics": {"fx": 2780.0, "fy": 2780.0, "cx": 1920.0, "cy": 1080.0}, "tracking": "normal"})
        if i % DEPTH_EVERY == 0:
            name = f"d{i:06d}.png"
            files[f"depth/{name}"] = depth_png(1500)
            files[f"confidence/{name}"] = confidence_png()
            depth.append({"t": t, "segment": 0, "file": f"depth/{name}", "confidenceFile": f"confidence/{name}",
                          "width": 256, "height": 192})
    files["mesh/arkit.ply"] = mesh_ply()
    end = START + seconds + 0.1
    manifest = {
        "schemaVersion": "blocwerk-scan/1", "scanId": str(uuid.UUID(int=rng.getrandbits(128), version=4)),
        "wallId": args.wall, "synthetic": True, "startedAt": "2026-11-27T10:00:00Z",
        "app": {"version": "0.0-synthetic", "build": "1"}, "device": {"model": "iPhone17,1", "osVersion": "26.1"},
        "video": {"codec": "hevc", "width": 3840, "height": 2160, "fps": FPS},
        "exposureLock": {"exposureDurationSeconds": 1 / 120, "iso": 200, "whiteBalanceRed": 1.9, "whiteBalanceGreen": 1.0,
                         "whiteBalanceBlue": 2.1, "lensPosition": 0.8, "lockedAt": START},
        "wallBox": [[-2, 0, -1.5], [2, 0, -1.5], [2, 3, -1.5]],
        "segments": [{"id": 0, "startTime": START, "endTime": end,
                      "videos": [{"file": f"video/seg0-{p:03d}.mov", "startTime": START + 60 * p} for p in range(pieces)]}],
        "frames": frames, "depth": depth, "mesh": {"file": "mesh/arkit.ply"},
        "files": [{"name": n, "bytes": len(b), "sha256": hashlib.sha256(b).hexdigest()} for n, b in sorted(files.items())],
    }
    for name, data in files.items():
        path = os.path.join(args.out, name)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as f:
            f.write(data)
    with open(os.path.join(args.out, "manifest.json"), "w") as f:
        json.dump(manifest, f)
    print(f"Built scan {manifest['scanId']}: {len(files)} files, {sum(map(len, files.values())) // 1024} kB in {args.out}")


class NoRedirect(urllib.request.HTTPRedirectHandler):
    """Refuses every redirect, so the API key in Authorization never goes to another address."""

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise urllib.error.HTTPError(req.full_url, code, f"refused a redirect to {newurl}", headers, None)


OPENER = urllib.request.build_opener(NoRedirect)


def call(method, url, key, body=None, headers=None):
    request = urllib.request.Request(url, data=body, method=method, headers={"Authorization": f"Bearer {key}", **(headers or {})})
    try:
        with OPENER.open(request, timeout=600) as response:
            return response.status, json.load(response)
    except urllib.error.HTTPError as error:
        if 300 <= error.code < 400:
            return error.code, {"message": error.reason}
        text = error.read().decode(errors="replace")
        try:
            return error.code, json.loads(text)
        except json.JSONDecodeError:
            return error.code, {"message": text}


def upload(args):
    key = os.environ.get("BLOCWERK_API_KEY")
    if not key:
        sys.exit("Set BLOCWERK_API_KEY to a personal API key with write access.")
    with open(os.path.join(args.dir, "manifest.json"), "rb") as f:
        manifest_bytes = f.read()
    manifest = json.loads(manifest_bytes)
    base = f"{args.base.rstrip('/')}/api/walls/{args.wall}/scans"
    status, state = call("POST", base, key, manifest_bytes, {"Content-Type": "application/json"})
    if status != 200:
        sys.exit(f"begin refused ({status}): {json.dumps(state, indent=1)}")
    missing = state["missing"]
    print(f"Scan {state['scanId']}: {len(missing)} of {state['totalFiles']} file(s) missing")
    hashes = {f["name"]: f["sha256"] for f in manifest["files"]}
    for sent, name in enumerate(missing):
        if args.stop_after is not None and sent >= args.stop_after:
            sys.exit(f"Stopped after {sent} file(s), as asked; run again to resume.")
        with open(os.path.join(args.dir, name), "rb") as f:
            data = f.read()
        if name == args.corrupt:
            data = bytes([data[0] ^ 0xFF]) + data[1:]
        url = f"{base}/{state['scanId']}/files/{urllib.parse.quote(name)}"
        status, answer = call("PUT", url, key, data, {"Content-Type": "application/octet-stream", "X-Content-SHA256": hashes[name]})
        if name == args.corrupt:
            print(f"{name} (corrupted): {status} {answer.get('message')}")
            sys.exit(0 if status == 422 else f"expected 422 for the corrupted file, got {status}")
        if status != 200:
            sys.exit(f"{name} refused ({status}): {answer.get('message')}")
    status, state = call("POST", f"{base}/{state['scanId']}/commit", key, b"{}", {"Content-Type": "application/json"})
    print(f"commit: {status} {state.get('status') or state.get('message')} {state.get('stage') or ''}")
    sys.exit(0 if status == 200 else 1)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    b = commands.add_parser("build")
    b.add_argument("--wall", required=True)
    b.add_argument("--out", required=True)
    b.add_argument("--frames", type=int, default=300)
    b.add_argument("--video-mb", type=int, default=4)
    b.add_argument("--seed", type=int, default=7)
    u = commands.add_parser("upload")
    u.add_argument("--base", required=True)
    u.add_argument("--wall", required=True)
    u.add_argument("--dir", required=True)
    u.add_argument("--stop-after", type=int)
    u.add_argument("--corrupt")
    args = parser.parse_args()
    build(args) if args.command == "build" else upload(args)


if __name__ == "__main__":
    main()
