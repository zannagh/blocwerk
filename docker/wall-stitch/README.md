# wall-stitch sidecar

An HTTP wrapper around the Python wall-stitching pipeline. It turns 2–12 handheld
phone photos of a climbing wall into a geometrically rectified orthophoto plus an
"angled" projection, and can carry a wall's existing holds across onto the new image.

The app never runs the pipeline itself: `Blocwerk.Core.Services.WallStitchClient`
talks to this service over HTTP. The wire contract is fixed by the records in
`src/Blocwerk.Core/Stitching/` — change one side and you must change the other.

## Why a sidecar

The pipeline is OpenCV/NumPy/SciPy and takes minutes and gigabytes per wall. Keeping
it out of the ASP.NET process means a stitch cannot take the site down, and the
pipeline's own dependencies stay pinned to versions it was developed against.

## Vendored pipeline

`pipeline/` is a **vendored copy**, so the image is self-contained and reproducible
and nothing is mounted from a developer's machine:

| in this repo | copied from |
| --- | --- |
| `pipeline/stitch/` (`stitch_wall.py`, `angled_view.py`, `auto_mask.py`, `stitch_planes.py`) | `~/Desktop/wall-photos/work/stitch/` |
| `pipeline/holds_match/` (`remap_holds.py`, `hm_*.py`) | `~/Desktop/wall-photos/work/holds-match/` |

One deliberate local change: `pipeline/holds_match/hm_common.py` carries a
`VENDORED-COPY PATCH` comment. Upstream it hardcodes a developer's home directory for
the work root, the input images and the ONNX model; the vendored copy reads them from
the environment instead, keeping the upstream values as defaults. Delete the patch once
`remap_holds.py` grows real `--old/--new/--holds/--wall` flags — the wrapper already
prefers those flags when they exist.

Refresh the snapshot with `./vendor.sh [UPSTREAM_ROOT]` (default
`~/Desktop/wall-photos/work`). It copies every `.py` file — so modules added upstream are
picked up automatically — and then re-applies the patch above via `vendor_patch.py`,
which is idempotent and tells you if the upstream block has changed shape.

## Pipeline invocation

The pipeline is treated as a black-box CLI. `app/invocation.py` reads each script's own
`--help` at run time and builds an argv from the flags it actually advertises, so this
wrapper keeps working while the pipeline is generalised from "`1..5.jpeg` in a fixed
folder with hand-traced masks" to "arbitrary image list in, output directory out".

Today that resolves to:

```
python stitch_wall.py --src <job>/input --work <job>/work --cache <job>/work/.cache --wall-angle 45
python remap_holds.py --work <job>/work/holds-match      # inputs via the env vars above
```

When the stitcher advertises `--images` (or `--inputs`/`--photos`), the uploaded file
names are appended to it. Until then, uploads are transcoded to JPEG and written into
`<job>/input` as `1.jpeg … N.jpeg`, which is what the current script expects to find.

## Progress

Progress is read off the pipeline's own stdout, not a timer. `app/stages.py` maps the
lines it prints when it *finishes* a step onto a fraction and a stage name
(`calibrating → undistorting → registering → rectifying → blending → projecting`,
then `matching` for hold transfer, which numbers its own `[n/7]` steps). Unrecognised
output leaves progress alone, and progress is monotonic.

## API

Everything except `GET /healthz` needs `Authorization: Bearer <WALLSTITCH_AUTH_TOKEN>`.

| method | path | notes |
| --- | --- | --- |
| `POST` | `/jobs` | multipart: 2–12 `photos`, an `options` JSON part, optional `oldPhoto`. `202 {"jobId","status":"queued"}` |
| `GET` | `/jobs/{jobId}` | status, progress, stage, error, result |
| `GET` | `/jobs/{jobId}/artifacts/{name}` | `ortho.png`, `angled.png`, `display-ortho.jpg`, `display-angled.jpg` |
| `DELETE` | `/jobs/{jobId}` | `204`; removes the job directory. Safe on an unknown job |
| `GET` | `/healthz` | unauthenticated; `200`/`503` plus per-check readiness |

JSON is camelCase. Coordinates are normalised 0..1 **per axis** (aspect is not
preserved); `radius` is normalised against the longer side; `shapePoints` are `{dx,dy}`
offsets from `(x,y)`. `transferHolds: true` requires an `oldPhoto` part.

The full-resolution masters are ~40 MB PNGs. The `display-*.jpg` copies are ~2000 px on
the long edge and are what the app stores in the database and serves to browsers.

`options.holds` carries no wall segmentation, so every hold is matched against the main
span. Kickboard and left-return planes are not exposed over this API.

### Error codes

`error.code` is machine-readable and `error.message` is shown to the end user; neither
ever contains a path or a traceback.

`too_few_usable_images`, `insufficient_overlap`, `no_dominant_plane`, `unreadable_image`,
`image_too_small`, `hold_transfer_failed`, `timeout`, `out_of_memory`, `cancelled`,
`interrupted` (job was mid-flight when the service restarted), `pipeline_failed`.

Request-time rejections are `400` with the same `{code, message}` shape:
`too_few_photos`, `too_many_photos`, `photo_too_large`, `request_too_large`,
`unsupported_photo_type`, `empty_photo`, `unreadable_photo`, `old_photo_required`,
`invalid_options`. A full queue is `503 queue_full`.

## Environment

| variable | default | meaning |
| --- | --- | --- |
| `WALLSTITCH_AUTH_TOKEN` | — | **Required.** Shared bearer token, ≥16 chars. The service refuses to start without it |
| `WALLSTITCH_DATA_DIR` | `/data/jobs` | One directory per job; artifacts and state live here |
| `WALLSTITCH_PIPELINE_DIR` | `/opt/pipeline` | Where the vendored pipeline lives |
| `WALLSTITCH_ONNX_MODEL` | `/opt/models/climbingcrux.onnx` | Hold-detector weights; only needed for hold transfer |
| `WALLSTITCH_WORKERS` | `1` | Concurrent jobs. Keep at 1–2: a stitch is CPU- and memory-heavy |
| `WALLSTITCH_QUEUE_LIMIT` | `16` | Queued jobs before `POST /jobs` answers `503` |
| `WALLSTITCH_MIN_PHOTOS` / `WALLSTITCH_MAX_PHOTOS` | `2` / `12` | Accepted photo count |
| `WALLSTITCH_MAX_PHOTO_BYTES` | `67108864` (64 MB) | Per-photo upload cap |
| `WALLSTITCH_MAX_REQUEST_BYTES` | `805306368` (768 MB) | Total upload cap |
| `WALLSTITCH_JOB_TIMEOUT_SECONDS` | `1800` (30 min) | A job over this is killed and fails as `timeout` |
| `WALLSTITCH_JOB_TTL_SECONDS` | `86400` (24 h) | Job directories older than this are deleted |
| `WALLSTITCH_REAPER_INTERVAL_SECONDS` | `900` | How often the TTL reaper sweeps |
| `WALLSTITCH_DISPLAY_MAX_EDGE` | `2000` | Long edge of the display copies, px |
| `WALLSTITCH_DISPLAY_JPEG_QUALITY` | `88` | Display-copy JPEG quality |
| `WALLSTITCH_LOG_LEVEL` | `INFO` | Python log level |

### What the app needs

The .NET side reads `Blocwerk:WallStitch:BaseUrl` and `Blocwerk:WallStitch:AuthToken`,
which fall back to these environment variables on the **app** container:

```
WALLSTITCH__BASEURL=http://wall-stitch:8080/
WALLSTITCH__AUTHTOKEN=<the same value as WALLSTITCH_AUTH_TOKEN>
```

Both are already wired up in `docker/docker-compose.yml`; put the token itself in
`docker/.env` (which is gitignored) and never in a committed file:

```
WALLSTITCH_AUTH_TOKEN=$(openssl rand -hex 32)
```

The compose service is `wall-stitch`, listening on `8080` on the compose network only —
no host port is published. Job data lives in the named volume `wall-stitch-jobs`.

## Development

```bash
python3.12 -m venv .venv && .venv/bin/pip install -r requirements-dev.txt
.venv/bin/python -m pytest                       # contract + unit tests, no pipeline needed

# the one end-to-end test; skips cleanly when deps or samples are unavailable
WALLSTITCH_SAMPLE_DIR=~/Desktop/wall-photos .venv/bin/python -m pytest tests/test_smoke_e2e.py -m e2e

# run it
WALLSTITCH_AUTH_TOKEN=dev-token-0123456789 WALLSTITCH_DATA_DIR=/tmp/wall-stitch-jobs \
  WALLSTITCH_PIPELINE_DIR=$PWD/pipeline \
  .venv/bin/uvicorn app.asgi:app --port 8080
```

The smoke test copies the sample photos into a temp directory; it never writes to the
sample directory.

## Operational notes

- **Single instance.** The queue and worker pool are in-process. Scaling to two
  replicas would give each its own queue and each its own view of the job volume.
- **Restarts.** Job state is written to `state.json` in the job directory. On startup
  anything still `queued`/`running` is marked `failed` with `interrupted`, so a poll
  never hangs forever waiting on a job no worker owns.
- **Disk.** The reaper is the only thing bounding the volume; the app should also
  `DELETE /jobs/{id}` once it has pulled the artifacts it needs.
- **OpenCV build.** `opencv-contrib-python-headless` rather than the GUI build: the
  pipeline uses no `imshow`/`namedWindow`, and headless avoids pulling GUI libraries
  into the image. Same upstream version.
