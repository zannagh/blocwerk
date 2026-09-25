# splat-worker

Turns a set of wall photos into a **Gaussian splat** (`wall.splat`, `wall.spz`) plus `frame.json`
(the transform into the wall's metric frame, the crop box and run statistics), so users never run
anything locally. Pipeline: COLMAP 3.9–4.x structure-from-motion (CPU SIFT) → Brush 0.3.0 training on
the GPU (wgpu: Metal on macOS, Vulkan on Linux; or gsplat on CUDA for NVIDIA hosts, `SPLAT_TRAINER=gsplat`) → optional similarity alignment to the ArUco solver's
cameras → crop → export.

It speaks the shared **Blocwerk compute job protocol v1** (`../compute-jobs-protocol.md`), like
`wall-geometry`: async jobs, polling, optional signed callbacks, bearer auth, results TTL. The protocol
machinery is the shared package `../compute-jobs-py/computejobs` (one copy for both services); this
directory only adds the `splat` kind. The app knows the worker only as **URL + API key**, so it can
run on any GPU machine: the realistic one for Blocwerk is the owner's Mac.

## API: `POST /v1/jobs/splat` (`multipart/form-data`)

| part | content |
|---|---|
| `photos` | 3–400 files (`.jpg/.jpeg/.png/.webp/.tif`), ≤ `MAX_PHOTO_MB` each. File name stem = the photo's name; to align, it must equal the geometry camera's `image` (e.g. `IMG_2770.jpg`). A stem starting with **`vf_`** (`vf_0001.jpg`, … in video order) is an **auxiliary video frame** (see below): trained on, never aligned with, not counted towards `MIN_PHOTOS`. |
| `geometry` | optional: a solved wall-geometry document (`tools/glyph/wall-geometry.schema.md`) → enables `align` and a crop box from the facets |
| `options` | optional JSON: `quality` (`draft`\|`high`\|`max`; default `high`, see **Quality profiles**), `maxSteps` (the profile's; 100–100000, overrides it), `maxImageEdge` (the profile's photo edge; 480–4096, overrides it), `matcher` (`auto`\|`exhaustive`\|`sequential`\|`pairs`; auto = `pairs` when `vf_` frames came along, else exhaustive up to 150 photos), `cropMarginMm` (400), `spz` (true), `colourMatch` (true), `cleanup` (false: the floater clean-up below, opt-in until it stops darkening the wall; needs `geometry`). Unknown keys → 422. |
| `callbackUrl` | optional (protocol) |

`202 {jobId, status}`; then `GET /v1/jobs/{id}` (status, `progress` 0..1, `stage`, `stageDetail`),
`GET /v1/jobs/{id}/files/{name}`, `DELETE /v1/jobs/{id}` (cancel: kills COLMAP/Brush too, via the
job's process group). Errors: `400` malformed form / bad file name / duplicate / refused `callbackUrl`,
`401`, `413` too many or too large photos / body, `422` undecodable or > `MAX_IMAGE_MEGAPIXELS` photo,
bad options (non-integer, out of range, NaN) or geometry (range-checked: finite numbers, ≤ 64 facets),
< 3 photos, `429` queue full, `503` Brush or COLMAP missing on the worker (`/health` says `degraded`;
details in the authenticated `GET /v1/info`).

**Stages** (`stage`, with `stageDetail` for the progress label):

| stage | progress band | detail from |
|---|---|---|
| `ingest` | 0–2 % | downscale photos to the profile's edge and video frames to its frame edge, camera grouping (photos were already made metadata-free on arrival) |
| `sfm-features` | 2–8 % | COLMAP `Processed file [i/n]` |
| `sfm-matching` | 8–22 % | COLMAP `Processing block [i/n, j/m]` (exhaustive runs in ~n/5-image blocks for granularity) or `image [i/n]` (sequential) or `pairs 250/2680` (pairs) |
| `sfm-mapping` | 22–26 % | COLMAP `Registering image #k (n)` → "9/14 images registered"; with frames, every later detail ends in "; 87/120 video frames registered" |
| `undistort` | 26–28 % | `Undistorting image [i/n]` |
| `train` | 28–95 % | Brush's step counter "step 1200/5000" (Brush prints it only to a TTY, so it runs under a pty) |
| `align` | 95–95.5 % | only with `geometry` |
| `crop`, `export` | 95.5–100 % | |

A failure names its stage and a reason the user can act on, e.g.
`sfm-mapping: only 3/14 images registered (need 7): shoot more overlap …`, `align: only 2 registered
photo(s) have a camera in the geometry document …`, `train: brush_app failed (exit code 101): <the
telling log line>`, `sfm-features: tool not found …`, `…: timed out after 14400 s`,
`sfm-matching: COLMAP exceeded the 3.9 GB memory limit …` (only once the cheapest tier was killed too).

**Result** (`result` of the finished job, also `frame.json`): `frame` = `{aligned, units, matrix`
(column-major 4×4 for three.js `Matrix4.fromArray`), `toViewer` (same, row-major: splat coordinates →
metres with X right, Y up, Z out of the wall, origin = centre of the reference facet), `toWorldMm`
(→ wall-geometry world, mm), `scaleMmPerUnit`, `crop` ([lo, hi] in the viewer frame: facet bounds ±
`cropMarginMm`), `alignment {cameras, residualMmMedian, residualMmMax, perCameraMm}`, `refinement
{method: "plane-icp", applied, reason?, before/after {medianAbsMm, points, facetOffsetMm{facet: mm}},
correction {shiftMm, rotationDeg, scale}, toWorldMm}}` (the fine alignment, `splatworker/refine.py`: a
robust rigid plane-ICP of the opaque, small splat centres onto the facets, holds and volumes trimmed
away; the app prefers its `toWorldMm` when `applied`; on The Attic it moved the splat 9 mm / 0.23° and
cut the bare-wall point-to-plane median from 8.3 to 5.9 mm) and `stats =
{photos, registeredImages, unregistered[] (photos only), videoFrames, videoFramesRegistered, sparsePoints, meanReprojErrorPx, meanTrackLength, matcher, cameraGroups,
memoryBudgetMb, trainMemoryBudgetMb, extractionThreads, matchingTier, memoryRetries[] (below),
colmapGpu (GPU name or null), colmapGpuExtraction, colmapMapper, siftDsp, siftAffineShape, vocabTreeMatching,
splatsTrained, splatCount, steps, trainingSeconds, stageSeconds{…}, alignmentResidualMm, fileBytes}`
(+ with gsplat and `GSPLAT_EVAL_EVERY`: `evalPsnr`, `evalSsim`, `evalViews`, `evalEvery`, `evalWallPsnr`, `evalWallSsim`; with zones `zones`).
Files: `wall.splat` (antimatter15, 32 B/splat, sorted by importance), `wall.spz` (Niantic v2, gzip,
SH degree 0, checked against Spark 2.2's own `writeSpz` and loaded/rendered in a Spark 2.2 viewer), `frame.json`. **The splat files stay in the
COLMAP frame of the run** (rotating SH coefficients is not worth it at degree 0 and the transform is
one matrix); apply `matrix` in the viewer. Without `geometry`: `aligned: false`, identity matrix,
crop = 1–99 % bounds of the splats + 15 %.

**Floater clean-up** (`splatworker/cleanup.py`, CPU, numpy only, a few seconds; `options.cleanup`, OFF
by default for now, needs `geometry`). Right after align + refine + crop, every splat gets a verdict in the
wall world (gravity up). KEPT: splats in a slab of a facet (outline + 60 mm, 60 mm behind to 180 mm in
front, unless the splat sticks out of it: a hair), in the floor/mat band (the kickboard's bottom edge
−120 … +450 mm), and the room's side wall (500 mm behind a side facet's plane, past its outline too:
the window and the clock stay). REMOVED, first rule wins: `aboveWallTop` (above the highest marker
+ 100 mm, over the wall's lateral extent only), `behindWall` (behind a front-facing facet, or below the
floor), `seenThrough` (a free-space carving: 50 mm cells that ≥ 3 cameras (photos and video frames)
look through on their way to surface evidence, ending 250 mm short of it; ≥ 8 when the cell holds
evidence itself), `needle` (long thin splats away from surfaces; very long ones anywhere), `sparse`
(no surface evidence within 100 mm: evidence = cells whose compact splats' opacities sum to ≥ 1).
Thresholds: `CleanupParams`. frame.json gets `cleanup {applied, rawFile, splats, kept, removed{rule:
count}, floorMm, wallTopMm, surfaceCells, carvedCells, cameras, params}` (or `{applied: false,
reason}`; a failed clean-up keeps every splat), `stats.splatsBeforeCleanup`, and the scene before the
clean-up is exported too as **`wall.raw.spz`** (the app keeps it to revert). On The Attic (1.27 M
splats) it removed 1085 above the top, 30382 behind the wall, 6779 seen through, 621 needles and
2168 sparse (3.2 %, but most of the visible fog and lines). Standalone, on an existing scene (filters
the `.spz` byte for byte; carves with the geometry's photo cameras):
`python -m splatworker.cleanup_run --spz wall.spz --frame frame.json --geometry geometry.json --out
wall.clean.spz [--report r.json] [--set carve_min_cameras=4 …]`.

**Privacy.** Phone photos carry GPS. The photo size is checked from the header before decoding (Pillow's
decompression-bomb warning is an error here too). Each photo is decoded *while the upload streams in*, the EXIF
orientation is applied, two facts are read (35 mm focal length; an opaque hash of make/model/lens
for grouping), and the pixels are re-encoded into a fresh JPEG with no EXIF/XMP/ICC/comments. The
original bytes are never written to disk (unlike Starlette's form parser, which spools uploads to temp
files). COLMAP would otherwise read the GPS into its database as pose priors. Only results and tool
logs stay in the job directory until the TTL; nothing is forwarded anywhere.

**Auxiliary video frames (`vf_*`).** The app sends up to 120 frames of an optional walk-along video
next to the marker photos, so the splat also covers the side and below views. They are ingested like
photos but share ONE COLMAP camera (`video_<orient>_<w>x<h>`, no prior: COLMAP estimates it from the
many views). The registration minimum (50 %) counts the photos only; a frame that does not register is
dropped silently (it is not in the model, so undistort and Brush never see it) and the count is
reported in `stageDetail`. **Alignment stays photo-only**: `align` gets the registered cameras whose
stem is not `vf_*` and matches them by name to the geometry's cameras (frames have no solved camera,
and they cannot shift the similarity transform; the frames sit in the same COLMAP reconstruction, so
the one transform places them too). A geometry whose only matching camera is a frame is refused (422).

Matching (`matcher: pairs`, `frames.py`): exhaustive over 160 images is 12720 pairs, so the worker
imports an explicit pair list with `colmap matches_importer`: photo × photo exhaustive, each frame
with its next `FRAME_NEIGHBOURS` frames (sequential, no loop detection or vocabulary tree: the photos
close the loops), and every `FRAME_PHOTO_STRIDE`-th frame with every photo. 40 photos + 120 frames →
780 + 699 + 1200 = 2679 pairs. It runs in chunks of 250 pairs (one COLMAP run each, all under the
memory guard, a progress line per chunk) with the job's matching tier (below). Guided matching holds a
features × features matrix per worker and is the memory hog. Measured (M4, COLMAP 4.2, 2026-09-23;
8 real photos + 20 frames of a synthetic pan over one of them, 1800 px, ~9.5k features/image,
4 threads): the pair list (167 pairs) matched in 6.5 s at a **280 MB** peak, 146/167 pairs verified;
the same 167 pairs with guided matching exceeded the 6 GB cap (6.5 GB, even at 2 threads), and so did
`exhaustive_matcher` (378 pairs, guided) on those 28 images. Feature extraction peaked at 2.5 GB.

**Memory budget and self-tuning** (`resources.py`, `tuning.py`, `sfm.py`). At job start the worker
reads what the machine can give: macOS `sysctl hw.memsize`, `vm_stat` (free + inactive + speculative +
purgeable pages), `sysctl vm.swapusage`; Linux `/proc/meminfo` (MemTotal, MemAvailable, Swap*) and
the container's cgroup (`/sys/fs/cgroup/memory.max` + `memory.current`, page cache from `memory.stat`
counted as reclaimable; v1 `memory.limit_in_bytes`), because the worker runs in Docker. **Budget =
min(available, 60 % of total)**, at least 3 GB (never above 60 % of total), and never above
`SPLAT_MAX_MEMORY_MB` when that is set. It is the memory guard's ceiling for every COLMAP run (Brush
gets the same rule, read again when training starts) and appears in `stageDetail`
(`… ; memory budget 9.6 GB (12.1 GB available of 16.0 GB); 4 threads`) and in `tools.log`.

COLMAP is then fitted to it. Extraction threads: ~620 MB each (2.5 GB at 4). Matching, best tier that
fits 90 % of the budget, from a cost model calibrated on the real 14-photo capture (3 camera groups,
1800 px, 8.2k–13.4k features/photo; M4, COLMAP 4.2, 2026-09-23; peak = physical footprint):

| matching (exhaustive, 14 photos) | matching peak | registered | points | reproj | matching time |
|---|---|---|---|---|---|
| guided, 4 threads | **9.7 GB** | 14/14 | 9060 | 0.78 px | 93 s |
| guided, 2 threads | 6.7 GB | 14/14 | 9054 | 0.78 px | 66 s |
| guided, 1 thread | 6.2 GB | 14/14 | 9022 | 0.78 px | 104 s |
| guided, 1 thread, `max_num_matches` 2048 | 6.7 GB (the cap does not bound it) | 14/14 | 9022 | 0.78 px | 94 s |
| unguided (plain) | 150 MB | 14/14 | 4247 | 0.65 px | 7 s |
| unguided + `point_triangulator` (mapper's poses, `clear_points 0`) | 150 MB (+86 MB) | 14/14 | 4240 | 0.69 px | 7 s |
| unguided + `point_triangulator` keeping two-view tracks | 150 MB (+100 MB) | 14/14 | 12601 | 0.49 px | 7 s |
| unguided, `max_ratio 0.9` | 150 MB | 14/14 | 5647 | 0.72 px | 8 s |
| unguided, `cross_check 0` | 145 MB | 14/14 | 5294 | 0.71 px | 7 s |
| unguided, `max_ratio 0.9` + `cross_check 0` | 160 MB | 14/14 | 7300 | 0.79 px | 11 s |
| **unguided, ratio 0.9, no cross-check + two-view triangulation** | **145 MB** (+86 MB) | 14/14 | **16278** | 0.66 px | 11 s |
| unguided, CPU brute-force matcher | 3.8 GB | 14/14 | 4427 | 0.65 px | 333 s |

One guided pair of 10354 × 12382 features alone peaks at 2.0 GB (~16 B per feature pair), and COLMAP
keeps 2 matchers per thread; model: `16 B × F1 × F2 × (1.95 + 0.5 × threads)` with F1, F2 the two
largest feature counts (6.2 / 7.5 / 10.0 GB at 1 / 2 / 4 threads here). Tiers, best first: **guided
with as many threads as fit** (≤ `COLMAP_THREADS`), guided with 1 thread, then **unguided +
triangulate** (ratio 0.9, no cross-check, then `point_triangulator` on the mapper's model with
`Mapper.tri_ignore_two_view_tracks 0`). A 16 GB Mac with 12 GB free gets guided at 3 threads (est.
8.7 GB); with 4 GB free (the budget this very run got while another agent ran tests) it gets
unguided + triangulate. `stats.matchingTier` says which ran.

**Retry instead of failing.** When the memory guard kills a step, the step runs again one tier down:
matching → the next tier (the matchers skip the pairs already in the database), extraction → half
the threads (COLMAP skips images that already have features). Each step-down is logged and listed in
`stats.memoryRetries` (`{stage, reason: memory|swap, from, to}`); the job only fails when the
cheapest tier is killed too. Verified with a forced 3.9 GB budget: guided 2 threads killed at 4.0 GB
→ guided 1 thread killed at 4.2 GB → unguided + triangulate, 14/14, 16267 points.

**Swap.** The guard also watches the *system*: once swap has grown by `SPLAT_MAX_SWAP_GROWTH_MB`
(2 GB) **within 120 s** *and* the tool itself holds at least half its limit, it is killed (reason
`swap`) and the step steps down like above. It reacts to the growth rate, never to the level (macOS
keeps "swap used" high long after the pressure is gone), and not to swap another workload causes
while the tool is small: without those rules a Brush run at a steady 2.1 GB of 4.5 GB was killed at
step 4600/5000 because a .NET build made the Mac swap. `tools.log` records each run's peak and the
largest swap growth per window (`# peak memory 2488 MB (limit 4082 MB); swap +0 MB at most per 120 s`).

Feature extraction (affine shape + domain-size pooling) peaks at 2.5 GB at 4 threads.

Camera intrinsics prior: the solver's calibrated `K`/`dist` for that photo when `geometry` has it
(scaled; same aspect required), else the EXIF 35 mm focal length; photos sharing lens + orientation +
size share one COLMAP camera (RADIAL). Without either, each photo gets its own camera.

## Quality profiles (`options.quality`, `profiles.py`)

| | `draft` | `high` (default) | `max` |
|---|---|---|---|
| photos' long edge (ingest, undistort, Brush `--max-resolution`) | 1800 | 2400 | 4032 (native 24 MP main lens) |
| video frames' long edge | 1800 | 1280 | 1920 (native) |
| floor when fitting memory (then the next profile down) | 960 | 1920 | 3000 |
| steps | 5000 | 15000 | 30000 |
| `--max-splats` (fitted down to the budget, never below the floor) | ≤ 1 M (floor 250k) | ≤ 2 M (floor 800k) | ≤ 5 M (floor 2 M) |
| `--growth-stop-iter` | Brush default (15000, i.e. never) | 60 % of the steps | 50 % |
| `--growth-select-fraction` | 0.1 (default) | 0.15 | 0.2 |
| `--sh-degree` | 3 (default) | 0 | 0 |
| checkpoints (`--export-every`) | end only | every third | every third |

Video frames stay small on purpose: Brush keeps every training image resident, and a walk-along frame
has far less detail per pixel than a still, so frames are coverage (sides, below), the stills are the
sharpness. **SH degree 0** for `high`/`max` because both exports (`.splat`, `.spz`) keep only the DC
colour: higher bands cost 45 of 59 floats per splat (with gradients and two Adam moments) and let the
DC colour drift from what every view shows. Explicit
`maxSteps` / `maxImageEdge` override the profile's.

**Fitting and step-down** (`tuning.train_plans`). Brush's peak is modelled as 1.9 GB + 5.4 MB per
resident megapixel (at the edge Brush trains) + a per-splat cost, fitted to M4 runs: 14 photos at 1800 px: 2.2 GB; 173 images at 1280 px,
534k splats, SH 3: 3.38 GB (~1 MB per 1000 splats); 173 at 1800 px: killed at 3.43 GB right after
loading; `high` (2400 px, SH 0): 3.85 GB at 0.23 M splats, killed at the 5 GB limit while growing
past ~0.6–0.8 M, so ~2 MB per 1000 splats at that image size (the per-splat cost grows with the
training resolution, not with the SH degree, so SH 0 buys less than its float count suggests). For the requested
profile and each lower one the worker takes the largest photo edge (160 px steps down to the profile's
floor) at which the floor splat count fits 90 % of the budget, then raises the splat cap to what fits.
Profiles that fit nothing are skipped; when none fits, the lowest one runs at its floor and the guard
decides. When the memory guard kills Brush the next plan (profile) runs, like the matching tiers;
`stats.quality`, `qualityRequested`, `trainImageEdge`, `maxSplats`, `trainEstimateMb` and
`memoryRetries` say what happened.

**Expected time** (whole job, 53 stills + 120 video frames, COLMAP 7–10 min included):

| machine | `draft` | `high` | `max` |
|---|---|---|---|
| Apple M4, 16 GB, busy desktop (measured, see below) | ~15 min | ~67 min (SfM 14, train 52 at 4.8 steps/s; peak 5.8 GB, 1.27 M splats, 20.7 MB `.spz`; `SPLAT_MIN_MEMORY_MB=7168`) | does not fit (needs 10+ GB free) |
| RTX 4070 / 4070 Ti class (12 GB VRAM, Linux, Vulkan) | ~10 min | ~25–35 min | ~1.5–2 h, if 5 M splats fit 12 GB (else steps down to `high`) |
| RTX 4090 / L4-L40S class (24 GB) | ~8 min | ~15–20 min | ~45–60 min |

GPU rows are extrapolated from the M4 (a 4090 has ~15–20× its FP32 throughput; Brush is
rasterisation-bound, so time scales roughly with steps × training pixels × splats per pixel) and
are not measured yet; the COLMAP part is CPU-bound and does not shrink with the GPU. On a GPU host,
give the job memory to match (`SPLAT_MAX_MEMORY_MB` unset, container limit ≥ 16 GB for `max`).

## Configuration (env)

| var | default | |
|---|---|---|
| `COMPUTE_API_KEY` | unset | bearer key. **Required** to listen on anything but loopback |
| `ALLOW_OPEN_BIND` | unset | `1` = allow a non-loopback bind without a key (private networks only) |
| `COMPUTE_CALLBACK_SECRET` | unset | HMAC key for signed callbacks; unset = none (the app polls) |
| `CALLBACK_ALLOWED_HOSTS` / `CALLBACK_ALLOW_PRIVATE` | unset | callbacks go to public addresses only, plus these host names / any private address when `1` (protocol doc, SSRF guard) |
| `MAX_IMAGE_MEGAPIXELS` | 100 | photos with a bigger header size are refused |
| `HOST` / `PORT` | 127.0.0.1 / 8100 (image: 0.0.0.0) | listen address |
| `BRUSH_BIN` / `COLMAP_BIN` | `brush_app` / `colmap` | tool paths (the image and `run-native.sh` set them) |
| `MAX_PHOTOS` / `MAX_PHOTO_MB` / `MAX_REQUEST_MB` | 400 / 40 / 4096 | `413` beyond |
| `MAX_QUEUED_JOBS` | 4 | `429` beyond; ONE job runs at a time |
| `SPLAT_TIMEOUT_S` | 14400 | per job; the whole process group is killed |
| `RESULT_TTL_S` | 3600 | finished jobs + files are deleted after this; restart loses everything (re-submit on `404`) |
| `WORK_DIR`, `BRUSH_CACHE_DIR`, `INGEST_MAX_EDGE` (4096), `MIN_PHOTOS` (3) | | |
| `INGEST_JPEG_QUALITY` / `FRAME_JPEG_QUALITY` | 95 / 92 | JPEG quality (1–100) of the metadata-free copy made on arrival, and of the downscaled training images (photos and video frames) the ingest stage writes for COLMAP and Brush |
| `SPLAT_MAX_MEMORY_MB` | 0 (unset) | hard upper bound on the job's memory budget (above; unset = from the machine alone). The budget is the ceiling per tool run (COLMAP step or Brush): a watchdog polls the tool's physical footprint (process + children; libproc on macOS, `/proc` RSS + swap on Linux) every 0.25 s and kills it above; COLMAP steps then step down a tier, Brush fails the job with `train: Brush exceeded the 3.9 GB memory limit …`. On Linux COLMAP additionally gets `RLIMIT_AS` = 1.5× the budget (macOS does not enforce `RLIMIT_AS`, amd64 emulation ignores it); Brush does not (GPU drivers reserve large virtual ranges). Peaks are logged to `tools.log` / `train.log` |
| `SPLAT_MIN_MEMORY_MB` | 0 (= 3 GB) | floor of Brush's memory budget (COLMAP keeps the plain one: a raised budget would pick guided matching on 1 thread, ~10× slower on 170 images), for a machine whose "available" memory is low only because idle apps sit in it (macOS pages them out); still never above 60 % of the total, and the swap guard stays on. `high` needs ~6–6.5 GB and `max` 12+ GB for a 170-image capture (below), so a busy 16 GB Mac otherwise trains `draft` |
| `SPLAT_MAX_SWAP_GROWTH_MB` | 2048 | kill a tool (holding ≥ half its limit) once the system's swap grew by this much within 120 s (`0` = no swap check) |
| `SPLAT_TRAINER` | `brush` | `brush` (Vulkan / Metal) or `gsplat` (CUDA, `Dockerfile.cuda` sets it); see "NVIDIA hosts" below |
| `GSPLAT_PYTHON` | `python3` | the trainer Python with torch + gsplat (`Dockerfile.cuda`: `/opt/gsplat/bin/python`) |
| `SPLAT_PROFILE_OVERRIDE` | unset | a quality (`draft` … `ultra`) that replaces every request's `options.quality`; request `maxSteps` / `maxImageEdge` still apply |
| `SPLAT_VRAM_MB` | 0 (= `nvidia-smi`) | the VRAM gsplat plans with (free = total = this) |
| `COLMAP_MAX_IMAGE_SIZE` | 2400 | longest edge COLMAP's SIFT sees (`FeatureExtraction.max_image_size`, 3.9: `SiftExtraction.*`); photos are already downscaled to `maxImageEdge` first. `0` = COLMAP's default |
| `COLMAP_MAX_FEATURES` | 8192 (`Dockerfile.cuda`: 16384) | SIFT features per image, whatever the image count (raising it also raises jobs past 60 images, e.g. photos + video frames). `0` = the job's own count: 16384 up to 60 images, 8192 beyond. The default keeps a 16 GB M4 safe. A soft cap in COLMAP 4.2 (8192 still yields up to ~13.4k per photo, not from extra orientations: `max_num_orientations 1` changed nothing). Guided matching × features² × threads is what once took a 14-photo job to 25 GB; the budget now picks guided or not (above) |
| `COLMAP_MAX_MATCHES` | 8192 (`Dockerfile.cuda`: 32768) | `FeatureMatching.max_num_matches` (3.9: `SiftMatching.*`) per image pair. On the GPU it is also SiftGPU's buffer size, i.e. **how many features per image are matched at all** (more are clamped: "Clamping features"), so keep it ≥ `COLMAP_MAX_FEATURES`; the planner lowers it only to fit the VRAM |
| `FRAME_NEIGHBOURS` / `FRAME_PHOTO_STRIDE` | 6 / 4 | `pairs` matcher: each video frame is matched with its next N frames, and every Nth frame with every photo |
| `COLMAP_THREADS` | 4 | upper bound on threads for feature extraction, matching and the mapper (`0` = all cores); the budget may use fewer. Memory grows with each; raise it on a big machine for speed |
| `COLMAP_USE_GPU` | 0 (`Dockerfile.cuda`: 1) | SIFT extraction + matching on the NVIDIA GPU (needs a CUDA build of COLMAP: `Dockerfile.cuda`). Matching is then planned against the free VRAM (`nvidia-smi`), not the host budget: GPU guided → GPU unguided + triangulate → CPU unguided (if a GPU run fails); never a CPU guided tier. No GPU visible → CPU, logged. See "GPU SfM" below |
| `COLMAP_GPU_INDEX` | 0 | the CUDA device COLMAP uses (`-1` = all) |
| `COLMAP_DSP_SIFT` / `COLMAP_AFFINE_SHAPE` | 1 / 1 (`Dockerfile.cuda`: 0 / 0) | domain-size pooling / affine-covariant SIFT (`SiftExtraction.domain_size_pooling` / `estimate_affine_shape`). COLMAP extracts these on the **CPU only** (4.2 silently switches `use_gpu` off), so with either on, extraction runs on the CPU and only matching uses the GPU |
| `COLMAP_MAPPER` | `incremental` | `incremental` (`mapper`) or `global` (`global_mapper`, GLOMAP merged into COLMAP 4.x; falls back to incremental on COLMAP 3.9) |
| `COLMAP_VOCAB_TREE_IMAGES` / `COLMAP_VOCAB_TREE_PATH` | 0 / unset (`Dockerfile.cuda`: `/opt/colmap/share/vocab_tree_sift.bin`) | > 0: after the `pairs` list, every `vf_*` video frame is also matched with its N most similar images by vocabulary-tree retrieval (`vocab_tree_matcher`, loop closure for walks; pairs already matched are skipped; a failure is logged, not fatal). The tree is COLMAP's pinned faiss SIFT tree (flickr100K, 256K words, sha256-checked at build time); COLMAP 3.9 cannot read it |
| `GSPLAT_EVAL_EVERY` | 0 | gsplat only: hold out every Nth PHOTO (sorted by name: 0, N, 2N, …; `vf_*` video frames always train) and score it at the end: `stats.evalPsnr` / `evalSsim` / `evalViews`, and with a wall geometry `evalWallPsnr` / `evalWallSsim` over the pixels showing a facet. A measurement mode (the held-out views do not train), so off by default; Brush ignores it |

## Run natively on a Mac (the "external GPU")

Docker on macOS has **no GPU access** (Docker Desktop's Linux VM sees neither Metal nor a Vulkan
device), so on a Mac the worker runs natively; Brush then trains through Metal.

```bash
brew install colmap                                   # 4.x; any 3.9+ works
COMPUTE_API_KEY=$(openssl rand -hex 32) docker/splat-worker/run-native.sh   # 127.0.0.1:8100
```

`run-native.sh` downloads the pinned Brush release (v0.3.0, sha256-checked) into `./.bin`, creates
`./.venv` from `requirements.txt` (uv if present) and starts `python -m splatworker`. The worker drops
`COMPUTE_API_KEY` / `COMPUTE_CALLBACK_SECRET` from its environment at startup; COLMAP and Brush run with
a minimal allow-listed environment (PATH, HOME, TMPDIR, locale, `XDG_*`, `VK_*`, `WGPU_*`, `NVIDIA_*`,
`MESA_*`, …). The Docker image installs the hash-locked `requirements.lock` (`--require-hashes`;
regenerate with the command in its header after changing a pin in `requirements.txt`). Try it with
`submit.py` (stdlib-only client: submit, live progress, download):

```bash
COMPUTE_API_KEY=… docker/splat-worker/submit.py --geometry wall-geometry.json \
    --options '{"maxSteps": 5000}' --out ./result photos/*.jpg
```

Keep the Mac awake while it works (`caffeinate -i` in front of the command). Measured on an M4 /
16 GB, 14 photos at 1800 px (2026-09-23, with geometry): upload + metadata strip 1.7 s, `sfm-features`
25 s, `sfm-matching` 271 s, `sfm-mapping` 9 s, `undistort` 0.5 s, `train` 715 s for 5000 steps
(the study: 15000 steps ≈ 51 min; steps slow down as splats multiply), align/crop/export < 1 s —
17 min in all; 14/14 registered, 115k splats, `.splat` 3.7 MB / `.spz` 1.9 MB, camera alignment
residual median 19 mm.

## Run on a Linux GPU host (Docker, Vulkan)

```bash
docker build -f docker/splat-worker/Dockerfile -t blocwerk-splat-worker .   # from the repo root
docker run -d --gpus all -p 127.0.0.1:8100:8100 -e COMPUTE_API_KEY=… blocwerk-splat-worker   # NVIDIA
docker run -d --device /dev/dri -p 127.0.0.1:8100:8100 -e COMPUTE_API_KEY=… blocwerk-splat-worker  # AMD/Intel
```

Image: Ubuntu 24.04, COLMAP 3.9.1 (apt), Brush v0.3.0 (release binary, sha256-pinned), Vulkan loader +
Mesa drivers, **linux/amd64 only** (no Brush Linux arm64 build). NVIDIA needs the **NVIDIA Container
Toolkit** on the host (`nvidia-ctk runtime configure --runtime=docker`); the image sets
`NVIDIA_DRIVER_CAPABILITIES=compute,graphics,utility` because `graphics` is what mounts the driver's
Vulkan ICD. Check with `docker run --rm --gpus all --entrypoint vulkaninfo blocwerk-splat-worker --summary`
(you want your GPU listed, not only `llvmpipe`). Without a GPU, Mesa's **lavapipe** (CPU Vulkan) is
picked: it works in principle but is orders of magnitude too slow for real training.

**Verification status (2026-09-23):** the image builds, its test stage passes, and a tiny real job
(5 photos, 480 px, 100 steps) ran end to end in it on this Mac under amd64 emulation with lavapipe
(COLMAP 3.9.1 path, Brush on Vulkan: 5.5 min, 281 s of it training 100 steps). It has NOT been run on a
real NVIDIA/AMD GPU host yet.

In the Blocwerk compose file the service is `splat-worker` under the **`gpu` profile**, so the
GPU-less production box never starts it: `docker compose --profile gpu up -d splat-worker`.

## NVIDIA hosts: gsplat on CUDA (`Dockerfile.cuda`, `SPLAT_TRAINER=gsplat`)

Brush has no working NVIDIA path under WSL2 (Mesa's dzn Vulkan-on-D3D12 crashes inside NVIDIA's
driver), so NVIDIA hosts, Linux or Windows via Docker Desktop's WSL2 backend, train with
[gsplat](https://github.com/nerfstudio-project/gsplat) 1.5.3 on CUDA instead. Everything else (ingest,
COLMAP, undistort, align, crop, clean-up, export) is the same code; the Mac runner and Linux Vulkan
keep Brush (`SPLAT_TRAINER` defaults to `brush`).

```bash
docker build -f docker/splat-worker/Dockerfile.cuda -t blocwerk-splat-worker-cuda .   # repo root
docker run -d --gpus all -p 127.0.0.1:8100:8100 -e COMPUTE_API_KEY=… blocwerk-splat-worker-cuda
docker compose --profile gpu-cuda up -d --build splat-worker-cuda   # compose: http://splat-worker-cuda:8100
```

- **Image** (7.5 GB on disk, 2.6 GB compressed; 6.9 / 2.4 GB with apt COLMAP 3.9.1; the spike's devel
  image was 21 GB): Ubuntu 24.04 + COLMAP 4.2.0 built with CUDA (see "GPU SfM" below) + the worker on Python 3.12 (hash-locked, as in the Brush image), and the trainer in its
  own CPython 3.10 venv (`/opt/gsplat`, `GSPLAT_PYTHON`): torch 2.4.1+cu124 and the **prebuilt**
  `gsplat-1.5.3+pt24cu124` wheel (sha256-pinned; gsplat publishes wheels for cp310 only). No CUDA
  toolkit, no nvidia/cuda base: the wheels bring the CUDA runtime, the NVIDIA container toolkit the
  driver. triton and cuDNN's engine libraries are dropped (the trainer turns cuDNN off). Covers
  compute capability 5.0–9.0 (up to RTX 40 / Hopper); RTX 50 (sm_120) would need torch ≥ 2.7 cu128 and
  gsplat built from source in a builder stage. No `--shm-size` needed (no DataLoader workers).
- **Trainer** (`gsplat_train.py`, a subprocess, so the worker never imports torch): gsplat's
  `simple_trainer` recipe trimmed to what the worker needs. **No world-space normalisation**: poses and
  points are read from COLMAP's binary model unchanged (`colmap_model.py`), the scene scale only scales
  the position learning rate, so the `.ply` stays in the COLMAP frame that `frame.json` and the
  alignment assume; a guard (`gsplat_trainer.check_frame`, reported as `stats.frameCheck`) fails the
  job if the splats' median leaves the sparse points' box or their spread is off by 10×. Cameras must
  be pinhole: the job's `colmap image_undistorter` step already writes PINHOLE for every camera group
  (photos of different lenses/sizes and the `vf_*` video frames each keep their own camera); anything
  else is refused before training.
- **Strategy: MCMC** with `cap_max` = the profile's splat cap. It grows 5 % per refine up to the cap and
  never beyond, so VRAM is plannable (the default densify-and-prune strategy has no hard cap), and at
  a given count it scores higher than densify-and-prune (the MCMC paper's and gsplat's benchmarks).
  **SH degree 0** for every profile: the exports keep only the DC colour; baking a higher-degree
  result would drop view-dependent colour the DC was never fitted without. Loss: L1 + 0.2 D-SSIM (torch
  SSIM, no compiled fused-ssim) + MCMC's opacity/scale regularisers.
- **Wall zones** (`zones.py`, `zone_run.py`, `gsplat_zones.py`; jobs with a wall geometry): plain MCMC
  samples new and relocated splats by opacity, i.e. wherever the photos are busy, and a room around a
  wall is most of every photo: on The Attic 82 % of a 6 M cap ended outside the ±400 mm crop, and what
  stayed inside grew a shell of needles and fog. Right after SfM the photos' COLMAP cameras are aligned
  to the geometry (the same fit as `align.py`) and three zones go to the trainer (`zones.json`):
  **wall** = each facet's outline + 100 mm, 60 mm behind to 250 mm in front of its plane (holds,
  volumes); **surround** = one axis-aligned, gravity-up box = facets + markers ± `cropMarginMm` (the old
  crop box), floor 150 mm below the lowest facet corner; **outside** = the rest. `ZonedMCMC` samples
  relocation and growth by opacity × zone weight: wall 1, surround 1 until it holds 10 % of the cap, outside
  0 (outside splats keep their sparse init and train, so occluders and the room stay explained instead
  of being painted into the wall). Export cuts hard to wall + surround (`frame.crop` = the box) and, in
  the surround only, drops splats whose 2σ ellipsoid pokes > 30 mm out of the box, needles (≥ 60 mm,
  ≥ 5× the middle axis) and faint haze (α < 0.08, ≥ 40 mm); the wall slab keeps everything (removing
  its large flat splats thinned the surface, the old clean-up's darkening). `frame.json` gets a `zones`
  block (per-zone counts, removals), `stats.zones` where the trained splats ended up. Brush and jobs
  without geometry keep the plain crop.
- **Memory**: gsplat needs VRAM, not host memory, so Brush's host model does not apply. The plan
  (`gsplat_trainer.plans`) fits the cap (then the edge, then the next profile down) to 90 % of the free
  VRAM (`nvidia-smi`, or `SPLAT_VRAM_MB`): 0.7 GB + 0.45 MB per 1000 splats + 160 MB per megapixel of
  the largest image (conservative: `high` measured 1.1 GB peak for 2 M splats at 2400 px where the model
  says 1.9). Host memory holds torch (~1.7 GB RSS measured) and the decoded images (a cache sized from
  the host budget; images beyond it are decoded per step), still under the RSS watchdog. A CUDA
  out-of-memory (`GSPLAT_OOM` from the script) or a watchdog kill is retried **once** at 60 % of the cap,
  75 % of the edge and no cache (`memoryRetries`), then fails with a clear message.
- **Progress**: `train` reports `loading images i/n`, then `step s/t, N splats`, then `writing splats`.

| `options.quality` → gsplat | `draft` | `high` | `max` | `ultra` |
|---|---|---|---|---|
| steps (`maxSteps` overrides) | 5000 | 15000 | 30000 | 50000 |
| photos' long edge (`maxImageEdge` overrides) / video frames | 1800 / 1800 | 2400 / 1280 | 4032 / 1920 | 4096 (the ingest cap, i.e. native) / 1920 |
| MCMC `cap_max` (fitted to VRAM, floor) | 1 M (250k) | 2 M (800k) | 5 M (2 M) | 6 M (3 M) |
| growth stops at | 50 % | 60 % | 50 % | 50 % |
| SH degree | 0 | 0 | 0 | 0 |

**`ultra`** is only picked with gsplat on a GPU with ≥ 12 GB (`nvidia-smi` total); otherwise, and always
with Brush, the job trains `max` (logged in `tools.log`). The worker accepts `options.quality: "ultra"`,
but the app does not send it yet (`SplatQuality` has Draft/High/Max; it would need an `Ultra = 3`
value, `CaptureSplatDocuments.QualityName` / `NextQuality`, and the UI). Until then
`SPLAT_PROFILE_OVERRIDE=ultra` makes the worker train every request as ultra (the app's
`SPLATSERVICE__MAXSTEPS` still caps the steps).

**Measured** (RTX 4070 Ti SUPER 16 GB, Docker Desktop WSL2, South Building 48 photos, 2026-09-24):
| run (whole job through the HTTP API) | photos | steps | splats trained / kept | train | job | peak VRAM (torch / GPU total incl. ~1 GB desktop) | `.spz` |
|---|---|---|---|---|---|---|---|
| `high` | 48 × 1600 px | 15000 | 2.00 M (cap) / 1.35 M | 7.8 min | 22.6 min (COLMAP 14.6) | 1.1 GB / 2.4 GB | 21.5 MB |
| `ultra` (`SPLAT_PROFILE_OVERRIDE=ultra`, `maxSteps` 20000) | 48 × 3072 px (native) | 20000 | 5.04 M / 3.24 M | 39.7 min | 51.3 min | 3.4 GB / 4.7 GB | 51.0 MB |

Host RSS of the trainer: 1.7 GB (`high`). Both `.spz` came out in the COLMAP frame (`stats.frameCheck`:
splat median inside the sparse points' 2–98 % box, spread ratio 1.1–1.9); training the spike's own
South Building model directly gave splat p2/p50/p98 within a few cm of the sparse points' (scene
diagonal 40 units) and a median splat-to-nearest-sparse-point distance of 0.011. A full 50k-step
`ultra` would take roughly 1.7–2 h on this card.

### GPU SfM (COLMAP with CUDA, `COLMAP_USE_GPU`)

`Dockerfile.cuda` builds COLMAP 4.2.0 (pinned commit) from source in a builder stage
(`nvidia/cuda:12.8.1-devel`, `CUDA_ARCHITECTURES` build arg, default `75-real;80-real;86-real;89-real;90`:
Turing … Hopper as SASS + PTX for newer GPUs), headless (no GUI / OpenGL / MVS / CGAL / ONNX), and copies
only the binary, `libcudart` and the vocabulary tree into the runtime stage (+0.6 GB, mostly
OpenImageIO's dependency tree). **This image therefore needs an NVIDIA GPU for COLMAP** unless
`COLMAP_USE_GPU=0`. It also has `global_mapper` (GLOMAP, merged into COLMAP 4.x): `COLMAP_MAPPER=global`.

- **Extraction** runs on the GPU (SiftGPU) unless `COLMAP_DSP_SIFT` / `COLMAP_AFFINE_SHAPE` are on:
  COLMAP 4.2 extracts covariant (DSP / affine) SIFT on the CPU only and silently turns `use_gpu` off
  (`controllers/feature_extraction.cc`), so the worker passes `use_gpu 0` itself and says so in
  `tools.log`. `max_num_features` is a soft cap on the GPU too: 16384 gave 26.6k per 3072 px photo.
- **Matching** runs on the GPU and is planned against the free VRAM (`tuning.gpu_matching_tiers`):
  SiftGPU allocates, per matcher, a `max_num_matches`² float distance matrix plus (cross-check) a
  `max_num_matches`²/8 × 4-channel table (6 B per feature pair); guided matching adds a second matcher.
  Model: 0.7 GB + 2 × 6 B × M² (measured 8.3 GB at 26.6k features, model 8.8). M = the largest image's
  feature count, capped by `COLMAP_MAX_MATCHES` (features beyond M are **not matched**: "Clamping
  features"), lowered only to fit 90 % of the free VRAM. Tiers: GPU guided → GPU unguided + triangulate
  → CPU unguided + triangulate (all threads) when a GPU run fails. Never a CPU guided tier (the Attic
  run with 16384 features planned "guided, 1 thread", est. 21.7 GB, and stalled). No RLIMIT_AS for
  CUDA runs (the driver reserves huge virtual ranges); the RSS watchdog stays. `max_num_matches` has a
  hard SiftGPU limit of 46336 (buffers < 2³¹ elements).
- **Pairs**: `pairs` (with `vf_*` frames) already matches every photo with every photo, frames with
  their next `FRAME_NEIGHBOURS`, and every `FRAME_PHOTO_STRIDE`-th frame with every photo;
  `COLMAP_VOCAB_TREE_IMAGES` adds vocabulary-tree retrieval for the frames (loop closure).

**Measured** (RTX 4070 Ti SUPER, South Building 48 photos at 2400 px, whole job through the HTTP API,
`high` with `maxSteps` 7000, `GSPLAT_EVAL_EVERY=8` = 6 held-out views, `COLMAP_THREADS=8`, 2026-09-25):

| | CPU, 8192, DSP + affine (old settings) | **GPU, 16384** (image default) | GPU, 16384 (repeat) | GPU matching, 16384, DSP + affine (CPU extraction) | GPU, 16384, `global_mapper` |
|---|---|---|---|---|---|
| features / matching / mapping | 44 s / 209 s / 40 s | **2 s / 20 s** / 70 s | 2 s / 20 s / 76 s | 63 s / 23 s / 153 s | 2 s / 20 s / 56 s |
| matching tier | guided, 8 threads (CPU) | GPU guided, ≤ 26564 features | same | GPU guided, ≤ 29038 | GPU guided |
| registered / points | 48 / 40.9k | 48 / 79.3k | 48 / 79.1k | 48 / 94.4k | 48 / 58.5k |
| reprojection error / track length | 0.72 px / 4.43 | 0.64 px / 4.62 | 0.63 px / 4.62 | 0.79 px / 4.57 | 0.64 px / 5.16 |
| splats (MCMC grows from the points) | 237k | 459k | 458k | 546k | 339k |
| held-out PSNR / SSIM | 19.90 / 0.665 | 19.85 / 0.705 | 20.34 / 0.701 | 18.26 / 0.648 | 18.96 / 0.683 |
| peak VRAM (GPU total, 1 GB desktop) | 1.9 GB (training) | 9.3 GB | | 10.9 GB | 9.3 GB |

GPU SfM is ~10× faster in matching, doubles the sparse points at a lower reprojection error; held-out
PSNR is equal within run-to-run noise (±0.5 dB between two identical runs), SSIM +0.04. DSP + affine
costs 30× the extraction time and 2× the mapping and scored worse here, so the image leaves it off.

## Exposing it safely

- The worker refuses to listen beyond loopback without `COMPUTE_API_KEY` (`ALLOW_OPEN_BIND=1`
  overrides, only for a private network). Use a long random key (`openssl rand -hex 32`); it is
  compared in constant time and never logged.
- Put **TLS** in front of it when it runs on a home machine: a reverse proxy (Caddy:
  `splat.example.org { reverse_proxy 127.0.0.1:8100 }`), or a tunnel — **Tailscale** (`tailscale serve
  --https=443 localhost:8100`, or Funnel if the app is outside the tailnet) or **Cloudflare Tunnel**
  (`cloudflared tunnel --url http://localhost:8100` with a named tunnel + hostname). Keep the worker
  bound to 127.0.0.1 behind them. Uploads are large: raise the proxy's body limit (Cloudflare's free
  plan caps request bodies at 100 MB — use fewer/smaller photos or Tailscale).
- The app → worker model is **push**: the app must be able to reach the worker's URL. Callbacks
  (worker → app) are optional; polling is primary, so the worker never needs to reach the app. A
  **worker-pulls-jobs** mode (the worker polls the app for work and uploads results, so a NAT'd machine
  needs no inbound port or tunnel at all) would be the natural next step if tunnels prove annoying.
- App side (compose): `SPLATSERVICE__URL` / `SPLATSERVICE__APIKEY` (the app polls; it takes no callback secret)
  (+ optional `SPLATSERVICE__MAXSTEPS`) from `docker/.env` (`SPLATSERVICE_URL`, `SPLAT_API_KEY`, `SPLAT_MAX_STEPS`;
  `SPLAT_CALLBACK_SECRET` is the worker's own). A set URL is the opt-in: every in-app capture then runs a
  photo-real stage after its textures (`WallCaptureProcessor.Splat.cs`; status `Splatting`, failure =
  `SucceededWithoutSplat`, the model stays live) and the 3D view offers "Photo-real (beta)". The app's
  job timeout for this worker defaults to 4 h (`SPLATSERVICE__JOBTIMEOUTMINUTES`).

## Capture recipe (from the feasibility study)

- **In the app**: add the walk (30–90 s, MP4/MOV) as the capture's optional video next to the marker
  photos; the server sends up to 120 sharp frames as `vf_*` (above). Via `submit.py`, extract frames
  yourself and name them `vf_0001.jpg`, … .
- **Video walk-along, not a few stills**: walk the wall's length slowly with the phone's **1× camera**
  (the ultra-wide's edges are soft and its distortion hurts matching), at **3 heights** (crouched,
  chest, arms up), camera roughly perpendicular to the wall, ~1–2 m away. Extract ~2–3 frames/s; use
  `matcher: sequential` (or `auto`, which switches above 150 photos).
- **Arcs at both ends**: at each end of the wall walk a quarter arc around the corner so the side walls
  and the wall's ends get oblique views; that ties the geometry together.
- **Pass under volumes and overhangs**: look up into roofs and under big volumes, otherwise they are
  holes/blobs in the splat.
- **Keep ArUco markers in some frames** so the job can be aligned to the solved wall geometry (the
  geometry photos themselves can be part of the upload: their names must match the solver's cameras).
- Every spot of the wall should be in 3+ photos from different positions; avoid motion blur (good
  light, slow walk), don't change zoom mid-capture, keep people out of the frame.
- The study's 14 marker stills registered 14/14 and trained fine; more overlap only helps.

## Tests

```bash
python -m pytest docker/splat-worker/tests docker/compute-jobs-py/tests   # in a venv with requirements-dev.txt
docker build -f docker/splat-worker/Dockerfile --target test .           # the same, in the image
SPLAT_INTEGRATION_PHOTOS=/path/to/photos [SPLAT_INTEGRATION_GEOMETRY=…] BRUSH_BIN=… COLMAP_BIN=… \
  python -m pytest docker/splat-worker/tests/test_integration.py       # opt-in, real tools, tiny job
```

Unit tests cover the memory caps (COLMAP 3.9 and 4.x option names, the watchdog killing a
memory-hungry fake tool under a 200 MB ceiling, RLIMIT_AS on Linux), upload validation + auth, EXIF/GPS/XMP/ICC stripping on arrival, progress parsing on
captured COLMAP 4.2 / Brush 0.3 output (plus COLMAP 3.9 wording), failure reasons, the similarity
alignment and crop box, and the `.spz` writer against Spark's; for gsplat (no CUDA needed) the trainer
selection, the profile → gsplat mapping and `ultra` gate, the VRAM plans and the one OOM retry, the
COLMAP binary reader and views, the COLMAP-frame guard, the progress parser (captured real output) and
the subprocess contract (a fake trainer Python); the protocol itself is tested once in
`../compute-jobs-py/tests`.
