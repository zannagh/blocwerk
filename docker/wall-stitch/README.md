# wall-stitch sidecar

An HTTP wrapper around the Python wall-stitching pipeline. It turns a handheld sweep of
a climbing wall — 2 to 48 phone photos — into a metric fronto-parallel **flat master**
plus a curved photographic **natural master** for display, detects the holds on it, and
can carry the wall's existing holds across onto the new image.

The app never runs the pipeline itself: `Blocwerk.Core.Services.WallStitchClient`
talks to this service over HTTP. The wire contract is fixed by the records in
`src/Blocwerk.Core/Stitching/` — change one side and you must change the other.

## Why a sidecar

The pipeline is OpenCV/NumPy/SciPy and takes minutes and gigabytes per wall. Keeping
it out of the ASP.NET process means a stitch cannot take the site down, and the
pipeline's own dependencies stay pinned to versions it was developed against.

## Vendored pipeline

`pipeline/` is a **vendored copy**, so the image is self-contained and reproducible and
nothing is mounted from a developer's machine:

| in this repo | copied from |
| --- | --- |
| `pipeline/wall_pipeline.py` (the CLI) | `~/Desktop/wall-photos/work/stitch/` |
| `pipeline/wallpipe/*.py` (register, rectify, compose, finish, cylinder, detect, carryover) | `~/Desktop/wall-photos/work/stitch/wallpipe/` |

The pipeline takes every input and every output as an argument, so unlike the previous
generation the vendored copy carries **no local patch**. `vendor_patch.py` is now a
*check* rather than an edit: it fails the vendor if any developer-home path (a
`/Users/...` literal, an `expanduser(...)` call, a `~/Desktop` reference) reaches the
snapshot in executable code, because such a path only fails once the container is
already running.

Refresh the snapshot with `./vendor.sh [UPSTREAM_ROOT]` (default
`~/Desktop/wall-photos/work`). It copies the entrypoint plus every `.py` file of the
`wallpipe` package — so modules added upstream are picked up automatically — and then
runs the check.

## Pipeline invocation

The pipeline is treated as a black-box CLI. `app/invocation.py` reads the script's own
`--help` at run time and builds an argv from the flags it actually advertises, so an
older vendored snapshot keeps working rather than dying on an unrecognised argument.

One job is one invocation:

```
python wall_pipeline.py \
    --input-dir <job>/input --output-dir <job>/work --cache-dir <job>/work/.cache \
    --curve gentle --wall-width-m 5.5 --wall-height-m 2.5 \
    --work-mp 0.7 --compose-mp 2.5 --max-canvas-mpx 40 --nfeat 12000 \
    --onnx /opt/models/climbingcrux.onnx \
    [--prior-holds <job>/work/prior/holds.json --prior-image <job>/old-photo.jpg]
```

`--prior-holds` and `--prior-image` are passed **only together**; without them the
pipeline skips carryover, which is exactly the fresh-wall case. Uploads are transcoded
to JPEG into `<job>/input` as `1.jpeg … N.jpeg`, and filename order is sweep order.

### The natural view is a plug, not a stage

**The display projection is not settled.** A reviewer rejected the cylindrical remap as
over-morphed, and the choice is open between a direct photographic panorama and a flat
developed view. So `--natural NAME` selects it, and the same projection that renders the
view also carries the hold coordinates onto it — a view whose coordinates cannot be
derived from the flat base's is a second source of truth, not a display view.

| `--natural` | what it does |
| --- | --- |
| `flat` (default) | the flat base itself, no reprojection. One of the two candidates, and it cannot misrepresent the wall |
| `cylindrical` | the provisional curved view. Geometry is correct (centre-derivative normalised) but the presets are guesses |

Adding a candidate is a class in `wallpipe/projections.py` and a line in `PROJECTIONS`;
no other stage knows which one ran. On the sidecar, `WALLSTITCH_NATURAL` sets the
default and `options.natural` overrides per job — deliberately an open string, so a new
projection does not need both sides redeployed to try.

The **flat base is settled**; everything measured hangs off it. Every hold coordinate in
the result is normalised against `flat.jpg`, never against the display view.

### What it produces

`manifest.json` (`blocwerk.wall-pipeline/1`) is the contract: the sidecar looks artifact
filenames up in it rather than hardcoding them. The ones that reach the API are

* `flat-base.jpg` → **`flat.jpg`**, the metric fronto-parallel composite. Every hold
  coordinate in the result is normalised against this image.
* `natural-{gentle,medium,strong}.jpg`, the cylindrical display views; the requested one
  is also published as **`natural.jpg`**.
* `cameras.json` (`blocwerk.wall-cameras/1`), one homography per frame taking a point in
  the original photo straight to `flat.jpg` pixels.
* `holds-flat.json` / `holds-natural.json`, the detections.
* `carryover.json`, the match of the wall's existing holds onto the new flat master.

Under `--natural cylindrical` the view is always **narrower** than the flat base: the
cylinder normalises on the centre derivative, so the middle of the wall is untouched and
the ends foreshorten. A view that is *wider* means the old edges-stay-at-the-edges
normalisation is back, which anamorphically widens the whole middle of the wall.

### Frame dimensions come from the same decode as the pixels

`compose.source_size` and `util.image_size` decode a frame **in full** to read its size.
That looks wasteful and is not; both cheap alternatives are wrong, and wrong silently:

* a 1/8 decode multiplied back rounds **up per axis**. A 4284x5712 frame reconstructs as
  4288x5712 — a 0.09% stretch in one axis only, i.e. a small *anisotropic* bias folded
  into every compose map;
* `PIL.Image.open(p).size` reads the header without decoding, but reports the **stored**
  orientation and ignores the EXIF rotation `cv2.imread` applies. On these frames it
  answers 5712x4284, the landscape transpose — the same swapped-size defect that once
  made every frame enter the warp 1.333x too wide and 0.75x too short.

The invariant: **dimensions must come from the same decode path as the pixels.** Any
other route is a second source of truth that can disagree, and nothing will report the
disagreement. One extra decode against the 46 the composite already does is not a cost
worth optimising away.

If `metric.py` (plane self-calibration for the metric x/y ratio) is ported from the R&D
tree, note that `solve(...)` takes the source dimensions as parameters and places the
principal point at `(src_w/2, src_h/2)`. It must be given the **EXIF-applied** dims. Fed
the transpose it does not crash — it returns a plausible-looking wrong answer (f = 8435
px, a 53 mm equivalent for a 28 mm lens, instead of f = 5173 px / 32.6 mm). Derive the
dims through `util.image_size` rather than passing them in by hand.

### Refusing a bad composite

The reference frame *is* the mosaic plane, and the ROI decides what the canvas contains.
Get either wrong and the canvas blows past the megapixel cap by orders of magnitude, the
compose scale collapses to fit, every frame enters the warp a couple of hundred pixels
wide, and the blend does a beautiful job on mush. That used to **exit 0**. Two guards now
stand in the way:

* the ROI statistic ignores frames whose warped footprint exceeds 8x the median — on the
  reference sweep 8 of 46 frames see the wall nearly edge-on, and a 2/98 percentile
  cannot survive that much contamination;
* a composite whose frames enter under 400 px on the long side fails as
  `no_dominant_plane` with an actionable message, rather than shipping a radial fan.

### Reference frame

The reference frame *is* the mosaic plane, so it is chosen rather than assumed: the
pipeline scores every frame by how much the sweep has to stretch to reach its plane and
takes the gentlest. On the 46-frame reference sweep the old "middle frame of the sweep"
default scored a 57x p90 stretch and produced a radial smear; the chosen frame scores
2.4x. Override with `--ref` only to reproduce a specific historical run.

## Progress

Progress is read off the pipeline's own stdout, not a timer. `app/stages.py` maps the
lines it prints when it *finishes* a step onto a fraction and a stage name
(`reading → matching → registering → blending → projecting → detecting`, then
`matching holds` when carryover runs, and `packaging`). Every line carries the
pipeline's own `[  12.3s]` elapsed stamp, which the tracker strips before matching.
Unrecognised output leaves progress alone, and progress is monotonic.

## API

Everything except `GET /healthz` needs `Authorization: Bearer <WALLSTITCH_AUTH_TOKEN>`.

| method | path | notes |
| --- | --- | --- |
| `POST` | `/jobs` | multipart: 2–48 `photos`, an `options` JSON part, optional `oldPhoto`. `202 {"jobId","status":"queued"}` |
| `GET` | `/jobs/{jobId}` | status, progress, stage, error, result |
| `GET` | `/jobs/{jobId}/artifacts/{name}` | `flat.jpg`, `natural.jpg`, `natural-{gentle,medium,strong}.jpg`, `cameras.json`, `display-flat.jpg`, `display-natural.jpg` |
| `DELETE` | `/jobs/{jobId}` | `204`; removes the job directory. Safe on an unknown job |
| `GET` | `/healthz` | unauthenticated; `200`/`503` plus per-check readiness |

JSON is camelCase. Coordinates are normalised 0..1 **per axis** (aspect is not
preserved); `radius` is normalised against the longer side; `shapePoints` are `{dx,dy}`
offsets from `(x,y)`. `transferHolds: true` requires an `oldPhoto` part.

The `display-*.jpg` copies are ~2000 px on the long edge and are what the app stores in
the database and serves to browsers.

`result.carryover.blocker` is the pipeline's own standing caveat about the accuracy of
the hold match. It is non-empty whenever carryover ran, and the app must not apply the
result unattended while it is: show the operator the overlay and let them confirm.

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
| `WALLSTITCH_MIN_PHOTOS` / `WALLSTITCH_MAX_PHOTOS` | `2` / `48` | Accepted photo count. A real sweep is 40–50 frames |
| `WALLSTITCH_MAX_PHOTO_BYTES` | `67108864` (64 MB) | Per-photo upload cap |
| `WALLSTITCH_MAX_REQUEST_BYTES` | `1610612736` (1.5 GB) | Total upload cap; ~30 MB x 48 frames |
| `WALLSTITCH_JOB_TIMEOUT_SECONDS` | `1800` (30 min) | A job over this is killed and fails as `timeout` |
| `WALLSTITCH_JOB_TTL_SECONDS` | `86400` (24 h) | Job directories older than this are deleted |
| `WALLSTITCH_REAPER_INTERVAL_SECONDS` | `900` | How often the TTL reaper sweeps |
| `WALLSTITCH_DISPLAY_MAX_EDGE` | `2000` | Long edge of the display copies, px |
| `WALLSTITCH_DISPLAY_JPEG_QUALITY` | `88` | Display-copy JPEG quality |
| `WALLSTITCH_NATURAL` | `flat` | Display projection: `flat` or `cylindrical`. NOT settled — see **The natural view is a plug** |
| `WALLSTITCH_CURVE` | `gentle` | Default curvature when the job does not name one (`cylindrical` only) |
| `WALLSTITCH_WORK_MP` | `0.7` | Megapixels per frame during feature matching |
| `WALLSTITCH_COMPOSE_MP` | `2.5` | Megapixels per frame when the flat base is composited |
| `WALLSTITCH_MAX_CANVAS_MPX` | `40` | Ceiling on the flat base. The pipeline lowers its own compose scale to fit. See **Memory** |
| `WALLSTITCH_NFEAT` | `12000` | SIFT features per frame |
| `WALLSTITCH_STRIP_BUDGET_MB` | `96` | Strip-local buffer budget for the full-resolution composite |
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

> The measurements below were taken against the **previous** pipeline generation and are
> kept for the reasoning, not the numbers. What holds unchanged is the shape of the
> problem: cost is proportional to the **canvas area**, not to the photo count, which is
> why `WALLSTITCH_MAX_CANVAS_MPX` is the knob that matters.

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

The masters are JPEG rather than PNG now. On a 40 Mpx flat base that is the difference
between a ~1 MB artifact and a ~45 MB one, and the pipeline's own quality-94 encode is
well below the noise floor of a handheld phone sweep.

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

## Multi-facet composites

The previous pipeline generation tried to split the wall into separately-rectified
facets (`facets.py`, `wall_layout.py`, `--emit-facets`). None of that survives: the
current pipeline composites onto **one** plane, and the wall's real curvature is carried
by the cylindrical natural view rather than by piecewise-planar geometry.

That trade is deliberate. Facet discovery could not tell a climbing surface from any
other plane — attic floor, plywood offcuts and roof timbers are planar too — so the
fitted normals were dominated by clutter and the panels rectified keystoned. The single
plane plus an analytic curve has no such failure mode: it does not have to decide what a
wall is.


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
