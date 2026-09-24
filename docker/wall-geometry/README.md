# wall-geometry service

Server-side 3D work for Blocwerk, so users never run anything locally:

- **solve**: ArUco marker observations (already edge-refined by the app) from any number of photos
  → metric wall geometry: facets (planes) in mm, gravity, measured angles, per-marker plane
  coordinates, camera poses. Output format: `tools/glyph/wall-geometry.schema.md` (extra fields OK).
- **textures**: photos + a solved geometry → one rectified orthophoto JPEG per facet (default
  2 mm/px) plus its coverage mask (8-bit PNG), for the app's 3D view.

It speaks the shared **Blocwerk compute job protocol v1** (`../compute-jobs-protocol.md`): async jobs,
polling, optional signed callbacks, bearer auth. `kind=splat` is NOT served here (answers `501`); a
separate `splat-worker` implements the same protocol.

## Layout: one source of truth

| path | what |
|---|---|
| `wallgeometry/` | the solver package (the ONLY copy): request parsing, BA, facet assignment, gravity, export, textures, edge refinement |
| `service/` | the two kinds' request parsing + job bodies (thin: the protocol machinery is shared) |
| `../compute-jobs-py/computejobs/` | shared protocol package (queue, auth, callbacks, limits, status/files/cancel), also used by `../splat-worker/` |
| `tests/` | pytest; `fixtures/capture1-request.json` is capture 1 as a request document |
| `../../tools/glyph/geometry/solve.py` | thin CLI: builds capture 1's request from `tools/glyph` data and imports `wallgeometry` |

The image is built from the **repo root** (`-f docker/wall-geometry/Dockerfile .`) so it can COPY the
shared protocol package; `Dockerfile.dockerignore` limits the context to these two directories. Each
source exists once (the previous `docker/wall-stitch/` rotted from copies).

## Solve request (`POST /v1/jobs/solve`, JSON)

```jsonc
{
  "markerSizeMm": 125,                         // black-square side
  "markerSizeOverridesMm": { "40": 80 },       // optional, per marker id
  "dictionary": "DICT_4X4_50", "idScheme": "segment*6+role",   // or "plan"
  "markerSegments": { "44": 0 },               // optional; REQUIRED for "plan": marker id -> segment index
  "segments": [                                // what the owner declares; only these are "declared"
    { "index": 0, "name": "main wall", "declaredAngleDeg": 45, "verticalReference": false },
    { "index": 1, "name": "kickboard", "declaredAngleDeg": 0,  "verticalReference": true }
  ],
  "levelPairs": [[14, 15]],                    // optional: marker pairs at equal height (gravity)
  "photos": [ { "name": "IMG_2770", "width": 4032, "height": 3024,
                "focal35mm": 14,               // or "focalPx"
                "cameraGroup": "iPhone 16 Pro|ultra-wide",   // photos sharing a lens share intrinsics
                "markers": [ { "id": 4, "corners": [[x,y],[x,y],[x,y],[x,y]],   // TL,TR,BR,BL px
                               "refined": true,
                               "synthetic": [false,false,false,false],        // rebuilt corners: sigma 15 px
                               "sigmaPx": null } ] } ],                       // optional manual weight
  "options": { "validate": false,              // leave-one-photo-out (slow)
               "autoDownweight": true,         // see "Outlier markers"
               "rejectOutliers": true,         // see "False detections"
               "facets": { "foldDeg": 5, "mergeDeg": 5, "mergeMm": 40, "minMarkersPerFacet": 2 } },
  "callbackUrl": "https://…"                   // optional (protocol)
}
```

Validation (`422` with a message): ids within the dictionary's `segment*6+role` range (with `"plan"`:
the whole dictionary, and every observed id must be in `markerSegments`), no duplicate id within a photo, 4 finite corners, a focal length per photo, unique photo names, sane sizes.

**Response** (`result.geometry` of the finished job, also file `wall-geometry.json`): the schema's
document plus: per segment `declared`, `declaredVsMeasuredDeg`; per facet `markerIds`,
`angleToReferenceFacetDeg`; per marker `nominalSegment`, `sizeMm`, `downweightedSigmaPx`, `measuredSideMm` +
`measuredSidePhotos` (see below); `world`
`gravityKnown`, `referenceFacet`; `quality` `gravity` (`"unknown"` or the constraints used),
`gravityDetail` (per-constraint residual degrees), `checks.declaredVsMeasuredDeg`,
`checks.levelPairs` (height differences), `facetDecisions`, `downweightedMarkers`, `rejectedObservations`, `unusedPhotos`,
`intrinsics`, optional `leaveOnePhotoOut`.

**Measured marker size.** The solved corners are always exactly the declared `sizeMm` square, so
they cannot reveal a misprinted or misdeclared sheet. `measuredSideMm` can: each corner is
triangulated (no size prior) from every photo that shows the marker, with the solved cameras fixed,
and the four sides are averaged. The cameras' scale comes from all markers, so one marker declared
as 100 mm but printed at 125 mm still measures ≈125 mm. `measuredSidePhotos` is the photo count;
both are `null` for a marker seen in a single photo. On capture 1 (125 mm prints) the markers
average 125.4 mm, within ±3 mm except the bent marker 32 (2 photos, 133.7 mm).

### What the solver decides by itself (no wall-specific code)

- **Facets.** Start from the nominal segment: `markerSegments[id]` when given (a marker plan), else `id // 6`. *Split* a segment when complete-linkage
  clustering of its marker normals gives sides that each have ≥ 2 markers and whose fitted planes
  differ by > `foldDeg` (one odd marker is a bent/bad marker, not a fold). *Merge* markers of an
  undeclared segment into the declared facet they are coplanar with (normal < `mergeDeg`, every
  corner < `mergeMm` from its plane). *Move* a declared marker to another facet only if its normal
  disagrees with its own facet by > `foldDeg` and fits the other. Declared segments are never merged
  with each other; coplanar ones get a `coplanarNote`. Every decision is in `quality.facetDecisions`.
- **Outlier markers.** A marker whose free-solve RMS is > 3 px and > 4× the median is down-weighted
  (sigma = its RMS / median, max 10 px).
- **False detections** (`wallgeometry/reject.py`). Down-weighting suits a marker that is wrong in every
  photo (bent); a detection that is wrong in ONE photo (a hold or a blurred grazing view read as an id)
  is REMOVED instead, then the free solve is re-run. After the down-weighted free solve, an observation
  is a candidate when its RMS exceeds max(4 px, median + 10 × MAD·1.4826) over all observations. For a
  marker seen in ≥ 3 photos it is removed only if the marker's pose re-fitted from its OTHER photos
  (cameras fixed) projects > threshold away AND > 3 × the median RMS of those other views (a bent
  marker fails everywhere and stays, down-weighted). A 2-view marker needs > 3 × threshold with the
  other view clean; a 1-view marker that fails is dropped from the model. At most 10 % of the
  observations and never a photo's last one. Each removal is reported in `quality.rejectedObservations`.
  On The Attic's 53-photo capture this removes a false id 17 on a black hold (left triangle: 267 → 1.4 mm
  coplanarity) and marker 33 in two blurred grazing photos; capture 1 (14 photos) has none.
- **Gravity.** Least-squares `up` ⟂ every facet normal of a `verticalReference` segment and ⟂ every
  `levelPairs` centre-to-centre direction (unit weights; with exactly two references and no pairs
  this is `n1 × n2`). Sign from the cameras' image-up (phones are held upright). With fewer than two
  independent constraints: `quality.gravity = "unknown"`, the reference facet is treated as vertical,
  angles are `null`, millimetres stay valid.
- **World frame.** x along the reference facet (lowest-index declared non-reference segment, its
  biggest facet), z up, origin = that facet's origin (bottom-left of its markers' bbox).

## Textures (`POST /v1/jobs/textures`, multipart)

Parts: `geometry` (JSON), `photos` (JPEG or PNG, named `<camera image>.<jpg|jpeg|png>`; size must equal
the solved camera), optional `options` JSON (`mmPerPx` 2.0 in 0.25–50, `maxSidePx` 4096 in 256–8192,
`extraMarginMm` 100 in 0–2000, `jpegQuality` 90 in 30–100; unknown keys → 422), optional `callbackUrl`.
The geometry is range-checked (finite numbers, ≤ 64 facets, sane image sizes, known dictionary) and the
total output must stay under `TEXTURES_MAX_MEGAPIXELS`, else `422`. The app sends `mmPerPx`, `maxSidePx` and
`jpegQuality` only when configured (`GEOMETRYSERVICE__TEXTURES__MMPERPX` / `__MAXSIDEPX` / `__JPEGQUALITY`).

**Resolution and physical sizes.** The renderer's internal sizes that are lengths on the wall are kept
physical at any `mmPerPx` (`wallgeometry/scale.py`): the label / source-map cell (16 mm), the seam feather
(10 mm), the mask feather (8 mm), the blend mode's outlier blur / smoothing (6 / 22 mm) and the work cells
of the shading flattening and seam harmonisation (16 / 8 mm). At the default 2 mm/px they are exactly the
old pixel values (8, 5, 4, 3, 11, 8, 4 px), so the output there is unchanged. The multi-view blend keeps
`(blendViews + 2) × 7` bytes per output pixel; above `TEXTURES_BLEND_MAX_BYTES` (2 GB ≈ 36 MP) a job
silently renders single-view instead, so raise it together with a finer `mmPerPx` (1 mm/px is 4× the
pixels), and `TEXTURES_MAX_MEGAPIXELS` / `MAX_REQUEST_MB` / `TEXTURES_TIMEOUT_S` with it.

**Privacy / integrity.** Photos are parsed from the stream in memory (never spooled to disk as
uploaded) and stripped of all metadata on arrival **without re-encoding** (`service/photos.py`, same
rules as the app's `ImageMetadataStripper`): JPEG APP1–APP15 (EXIF incl. GPS and orientation, XMP, ICC,
MPF, …) and COM segments and everything after the primary image's EOI (e.g. an MPO's second image);
PNG keeps only IHDR/PLTE/tRNS/IDAT/IEND. The compressed pixel data is copied byte for byte, so the pixel
grid the marker corners were measured on is unchanged; photos are used on that raw grid (orientation
is not applied, as in the app). The header size is checked against `MAX_IMAGE_MEGAPIXELS` before any
decoding.

Per facet pixel, the ONE photo with the best `f·cos(view angle)/distance` is used (no averaging, so no
ghosted holds), chosen on a 16 mm (8 px at 2 mm/px) label grid cleaned with a 5-cell mode filter. Plane points behind
another facet's surface are left black (clips the side triangle along the overhang). Pixel `(i, j)`
covers `a = aMin + (i+0.5)·mmPerPx`, `b = bMax − (j+0.5)·mmPerPx` in the facet frame.
**Coverage mask.** Every facet also gets `facet_<id>_mask.png` (manifest field `maskFile`): an 8-bit
grayscale PNG on exactly the texture's pixel grid, 0 where no photo was drawn (outside every photo,
behind another facet — the black parts of the JPEG), 255 where one was, with a linear 8 mm (4 px at 2 mm/px) feather
*inside* the covered area so the black fill never bleeds into the seam. A viewer uses it as the alpha of
the texture and shows the plain facet elsewhere. It stays a separate file so the photo keeps JPEG's
size and format: on The Attic's 4 facets (≈ 6.6 MP) the masks are 27 KB in total next to 1.95 MB of
JPEG (+1.4 %). An RGBA PNG of the same textures would be 10.0 MB; a q90 WebP with alpha 1.56 MB, i.e.
smaller, but a new image format for every consumer and no way back for old ones. The field is
additive: clients that ignore it get exactly the old result.

**Source-view map.** Every facet also gets `facet_<id>_source.json` (manifest field `sourceFile`, see
`wallgeometry/sourcemap.py`): which photo painted each label cell (16 mm at any resolution: 8 px at the
default 2 mm/px), as a base64 grid of 1-based indices into its `cameras` list (0 = none). A protruding hold
shows in the texture as seen from that photo, so the app's 3D view uses it to draw hold outlines where
the texture shows them. ~0.1 MB for a 5 × 3.5 m facet; additive like the mask.

`markerCheck` re-detects the facet's markers in its own orthophoto, edge-refines them and reports
side length vs `markerSizeMm` and position vs `cornersPlaneMm`.

Not handled: occlusion by holds/volumes, exposure differences between photos (visible seams).

## Configuration (env)

| var | default | |
|---|---|---|
| `COMPUTE_API_KEY` | unset | bearer key for everything but `/health`. **Required** to listen beyond loopback (the image listens on 0.0.0.0: without a key it exits with code 2) |
| `ALLOW_OPEN_BIND` | unset | `1` = start without a key anyway (private networks only) |
| `COMPUTE_CALLBACK_SECRET` | unset | HMAC key for `X-Blocwerk-Signature`; unset = no callbacks |
| `CALLBACK_ALLOWED_HOSTS` / `CALLBACK_ALLOW_PRIVATE` | unset | callbacks go to public addresses only, plus these host names (compose: `blocwerk`) / any private address when `1` |
| `MAX_IMAGE_MEGAPIXELS` | 100 | photos with a bigger header size are refused (`413`) |
| `TEXTURES_MAX_MEGAPIXELS` | 200 | total output pixels of one textures job (`422` beyond) |
| `TEXTURES_BLEND_MAX_BYTES` | 2000000000 | memory the multi-view blend may take (`(blendViews + 2) × 7` bytes per output pixel); a bigger job renders single-view. Raise it for `mmPerPx` < 2 on a machine with the RAM |
| `HOST` / `PORT` | 127.0.0.1 / 8000 (image: 0.0.0.0) | listen address |
| `RESULT_TTL_S` | 3600 | finished jobs + files are deleted after this |
| `MAX_REQUEST_MB` / `MAX_PHOTO_MB` / `MAX_PHOTOS` | 400 / 40 / 60 | limits (`413`). A textures request carries every photo: 50 full-size 48 MP JPEGs (12–22 MB each) need `MAX_REQUEST_MB` ≈ 1200 |
| `MAX_QUEUED_JOBS` | 16 | `429` beyond this |
| `SOLVE_TIMEOUT_S` / `TEXTURES_TIMEOUT_S` | 600 / 900 | per job; the job's process is killed |
| `WORK_DIR` | /tmp/wall-geometry-jobs | job inputs/outputs |

Single instance, one worker, in-memory queue: jobs are lost on restart (clients re-submit on `404`).
Each job runs in a spawned child process, so cancel/timeout are hard kills. Request bodies and image
bytes are never logged. The service starts through `python -m service` (`computejobs.serve`: bind check,
then uvicorn); `/health` is minimal, details (git SHA, queue, limits) are at the authenticated
`GET /v1/info`.

Dependencies: `requirements.txt` holds the pins; the image installs `requirements.lock` (generated
from it with hashes, `--require-hashes`). After changing a pin, regenerate both locks with the command
in their headers (`uv pip compile … --generate-hashes --universal --python-version 3.12`).

## Run locally

```bash
docker build -f docker/wall-geometry/Dockerfile -t blocwerk-geometry .   # from the repo root
docker run --rm -p 8000:8000 -e COMPUTE_API_KEY=dev-key blocwerk-geometry
curl localhost:8000/health
curl -s -H 'Authorization: Bearer dev-key' localhost:8000/v1/info
curl -s -H 'Authorization: Bearer dev-key' -H 'Content-Type: application/json' \
     --data @docker/wall-geometry/tests/fixtures/capture1-request.json localhost:8000/v1/jobs/solve
curl -s -H 'Authorization: Bearer dev-key' localhost:8000/v1/jobs/<jobId>
```

Tests: `docker build -f docker/wall-geometry/Dockerfile --target test .` (runs these tests and the
shared package's), or in a venv `pip install -r requirements-dev.txt && python -m pytest tests
../compute-jobs-py/tests` (the tests put `../compute-jobs-py` on the path; set `GLYPH_PNG_DIR` to capture
1's PNGs to also run the real-photo texture check).

## Production (manual step)

The IONOS box runs a **standalone copy** of `docker/docker-compose.yml`, so CI publishing the image
does not deploy it. On the box, in the compose directory:

1. Add the `wall-geometry` service block from `docker/docker-compose.yml` to the standalone file, and
   the two `GEOMETRYSERVICE__*` lines (`GEOMETRYSERVICE__URL`, `GEOMETRYSERVICE__APIKEY`) to the
   `blocwerk` service's `environment`. The app polls the service, so it takes no callback secret.
2. In `docker/.env` there: `COMPOSE_PROFILES=compute`, `GEOMETRYSERVICE_URL=http://wall-geometry:8000`,
   `COMPUTE_API_KEY=<random, e.g. openssl rand -hex 32>`, and optionally
   `COMPUTE_CALLBACK_SECRET=<another random value>` (the service's own; unset = no callbacks).
   Never commit them.
3. The GHCR package `blocwerk-geometry` is new and private by default: make sure the box's GHCR login
   (the one that pulls `blocwerk`) can pull it, or make the package public.
4. `docker compose pull wall-geometry && docker compose up -d wall-geometry blocwerk`, then check
   `docker compose ps` shows it `healthy`.
5. The autodeploy cron only redeploys the `blocwerk` service. Updating `wall-geometry` later is
   `docker compose pull wall-geometry && docker compose up -d wall-geometry` until autodeploy
   learns about it.
