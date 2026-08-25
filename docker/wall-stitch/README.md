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

The full-resolution masters are ~45 MB PNGs. The `display-*.jpg` copies are ~2000 px on
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
| `WALLSTITCH_MAX_CANVAS_MPX` | `80` | Ceiling on the orthophoto canvas. `0` disables it. See **Memory** |
| `WALLSTITCH_STRIP_BUDGET_MB` | `96` | Strip-local buffer budget for the full-resolution composite |
| `WALLSTITCH_PNG_COMPRESSION` | `3` | zlib level for the PNG masters. Lossless at every level |
| `WALLSTITCH_PIPELINE_THREADS` | `4` | Thread cap for OpenCV/BLAS inside the pipeline. `0` leaves them unbounded |
| `WALLSTITCH_MEMLOG` | unset | Set to `1` to log RSS at each pipeline checkpoint |
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

## Memory

A stitch is the memory-hungriest thing this app does, and almost all of it is
proportional to the **canvas area**, not to the number of photos: the orthophoto
canvas, its coverage mask, the density grids and the seam-tracing buffers are each
one array the size of the finished image. On the reference four-photo wall the canvas
is 9363x5188 (48.6 Mpx), and one canvas-sized uint8 BGR buffer is 146 MB.

Measured on the reference wall (4 photos, `--legacy-masks`, warm cache, 4 threads),
before and after the memory work:

| | peak RSS | wall clock |
| --- | --- | --- |
| before | 2854 MB | 4 min 01 s |
| after | 2529 MB | 1 min 29 s |

The peak moved only 11% because peak RSS is a **ratchet**: it records the largest
single moment in the run, and the arena the earlier stages claimed gets reused rather
than returned. What actually changed is visible with `WALLSTITCH_MEMLOG=1`, which
reports RSS at each checkpoint. On the automatic path (5 photos, warm cache):

```
before plane discovery      420 MB
after plane discovery      3052 MB   <- the peak now lives here
after undistort            3529 MB
before composite           3723 MB
strip start                 830 MB   <- everything below is the part this work touched
after composite             846 MB
after crop                 1098 MB
before angled view         1240 MB
done                       1240 MB
```

Composite, crop, seam verification and the angled projection together now run inside
~1.25 GB, where before they were what pinned the job. **The peak is now set by
`auto_planes.discover`**, which is untouched by this work - see *What is still
expensive* below.

What was costing the most, and what changed:

- **The canvas was copied whole** to write `wall-orthophoto-full.png`, an uncropped
  diagnostic nothing downstream reads. Now behind `--emit-full-png`, off by default;
  its dimensions are still reported, from the mask.
- **`crop_usable` returns a view**, so the whole canvas stayed reachable behind the
  crop for the rest of the run - and the rest of the run is where the peak is. The
  crop is now compacted and the canvas released.
- **`trace_seams` built three full-canvas float64 arrays** (~300 MB each here),
  because `np.where(wood, resp, np.nan)` promotes float32 to float64 through a Python
  float. Now one float32 array, reused for the row profile.
- **The strip composite allocated per strip and per frame**: a zeroed copy of the
  accumulator plus three boolean-indexed temporaries. Now in-place `np.divide`/`np.clip`
  into buffers allocated once, with the strip height sized from
  `WALLSTITCH_STRIP_BUDGET_MB` so the composite's peak is flat in canvas width rather
  than growing with it.
- **Density maps were stacked** - one grid per frame plus a copy of all of them. Now
  reduced with `np.maximum` one frame at a time, which is bit-identical. They stay
  float64 on purpose: narrowing them to float32 flipped cells across the 0.85
  preference-mask threshold that feeds the graph cut, which moved the seam and changed
  2.5% of the delivered pixels by up to 39 levels. Not worse - but not the same
  picture, which is not a trade this was allowed to make.
- **The angled view re-read the ortho master from disk** and drew hold overlays at full
  resolution before shrinking the result to a 2200 px review figure. It now takes the
  master in memory and draws on the downscaled panel.

The delivered pixels are unchanged: against the pre-change build, `wall-orthophoto.png`
differs in **13 pixels out of 34.9 million, by one level each**, and the kickboard and
left-return masters are bit-identical. Those 13 pixels come from the adaptive strip
height changing `warpAffine`'s fixed-point coordinate rounding at strip boundaries.
The rectification metrics are unchanged (seam angle rms 0.0613 -> 0.0612 deg).

Two knobs *can* trade quality, and neither does anything until it binds:

- `WALLSTITCH_MAX_CANVAS_MPX` (default 80) lowers the output scale if the canvas would
  exceed it. The reference wall is 48.6 Mpx, so the default is a no-op there. When it
  does bite it logs a warning with the factor and records `canvas_cap` in the report -
  a soft result is never a silent one. This is the only hard bound on a job's memory:
  without it, how much a stitch needs is decided by how big the photographed wall is.
- `WALLSTITCH_PIPELINE_THREADS` (default 4) caps OpenCV's and BLAS's thread pools.
  Left unbounded they open one thread per host core, each with its own scratch arena,
  which multiplies transient memory and starves the app container for no throughput
  gain - the heavy stages are memory-bandwidth bound well before they are core bound.

`WALLSTITCH_PNG_COMPRESSION` is purely time-for-bytes: PNG is lossless at every level,
and on a 7648x4864 master level 3 is 44.5 MB in 5 s where level 9 is 41.2 MB in 75 s.

### Sizing the container

`docker-compose.yml` sets `mem_limit` on the service (default `6g`). Set it from the
box: the limit exists so a runaway stitch takes *this container* down rather than
letting the kernel OOM killer pick a victim across the whole host. A SIGKILLed
pipeline is reported to the user as `out_of_memory` rather than `pipeline_failed`.

Keep `WALLSTITCH_WORKERS` at 1 unless the limit is generous: the peak is per job, so
two concurrent stitches want twice the ceiling.

### What is still expensive

`auto_planes.discover` is now the high-water mark, at ~3.0 GB on a five-photo wall even
with every pairwise match served from cache. Two things drive it, and neither is
addressed here:

- **Full-resolution SIFT.** `auto_mask.promote` and `stitch_wall.match_pair` build a
  scale-space pyramid over a whole 12 Mpx frame to keep the keypoints that fall inside
  an overlap mask covering a fraction of it. On a cold run the first pair-match alone
  takes the process from 1.3 GB to 3.3 GB. Cropping each frame to the overlap bounding
  box plus a margin before detection would cut that roughly in proportion to the
  overlap, with the same keypoints; it is the obvious next move.
- **Pairwise matching is quadratic in the photo count.** Discovery matches every
  unordered pair: 6 for four photos, 10 for five, 66 for twelve. Measured on a cold
  five-photo run (M-series laptop, 4 threads), the ten pairs took **177 s** - 2 s for a
  pair with little overlap, 62 s for a pair carrying four candidate planes - and the
  whole job took 4 min 29 s.

  Extrapolating the same per-pair cost to twelve photos puts pairwise matching alone at
  **around 20 minutes**, against a default `WALLSTITCH_JOB_TIMEOUT_SECONDS` of 30. That
  fits on this hardware, with little margin, and the margin is what a slower deployment
  box spends. So the **twelve-photo ceiling advertised by `WALLSTITCH_MAX_PHOTOS` is a
  time risk before it is a memory one** - if uploads that size are expected, time a real
  one on the target hardware before trusting the default timeout.

  Memory does *not* grow the same way: pairs are matched one at a time and the allocator
  reuses the arena, so a five-photo automatic run peaks at about the same place a
  four-photo one does.

Capping OpenCV's thread count (`WALLSTITCH_PIPELINE_THREADS`) was tried against this
and does **not** move the peak: at 2 threads the cold four-photo run peaked at 3930 MB
against 3886 MB at 4. The transients are single large allocations, not per-thread
arenas. The setting is kept because it still bounds CPU contention with the app
container, but it is not a memory fix.

### Where the time and memory actually go

Set `WALLSTITCH_MEMLOG=1` and the pipeline logs its RSS at each checkpoint
(`before plane discovery`, `after composite`, `after crop`, ...). That is the first
thing to look at when a job is killed: it says which stage was live, which is what
decides whether the fix is a smaller canvas, fewer photos or a bigger limit.

## Facets

`auto_planes.discover` returns a **ranked list** of planes. Delivery does not: it takes
`planes[0]` as the main span and `planes[1]` as the kickboard, and until recently
everything from `planes[2]` on was found, accepted, ranked and then dropped without a
word. On the reference wall that silently discarded the surface carrying the steeper
right-hand panel.

`facets.py` closes the mechanism half of that gap. It rectifies any accepted plane the
same way `stitch_planes.kickboard` does the kickboard, sharing the main span's in-plane
horizontal so every facet is level against the same reference, and records each facet's
yaw, tilt and dihedral to the span so a layout pass can place them side by side later.

It is **off by default** (`--emit-facets`), because on real input the planes it is handed
are not clean enough to ship:

- On the reference wall, plane 2 spans photos 4 and 5 and does contain the steeper right
  panel — but its mask also picks up attic floor, plywood offcuts and roof timbers, which
  are planar too and which discovery has no reason to reject. The fitted normal is then
  dominated by that clutter and lands **4° from the main span's**, so the panel rectifies
  keystoned rather than flat.
- Nothing in the pipeline distinguishes *a climbing surface* from *any plane*. A floor is
  a perfectly good plane.

Two things would have to land before facets are deliverable:

1. **A surface constraint.** Rejecting planes whose normal is far from horizontal would
   drop floors and ceilings outright, which is most of the contamination here. The
   pipeline already recovers gravity implicitly (plumb-line calibration, seam azimuth),
   so the information is available.
2. **Better coverage of the facet itself.** Plane 2 is fitted from a single pair, 4–5.
   `plane_normal_from_pairs` now resolves the twofold decomposition ambiguity using
   `filterHomographyDecompByVisibleRefpoints` — without that it chose the mirrored plane
   and rectified it to a 68360×483598 canvas — but one pair still leaves the normal
   fitted to whatever correspondences dominate. The README's own advice for the left
   return applies here: two or three more frames that see the panel properly.

The guards in `facets.py` (`MAX_UNIT_EXTENT`, `MAX_ASPECT`, `MAX_FACET_MPX`) exist because
a degenerate normal produces an unbounded canvas, and an unbounded canvas is an
out-of-memory kill rather than a bad picture.

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
