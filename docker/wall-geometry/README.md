# wall-geometry service

Server-side 3D work for Blocwerk, so users never run anything locally:

- **solve**: ArUco marker observations (already edge-refined by the app) from any number of photos
  → metric wall geometry: facets (planes) in mm, gravity, measured angles, per-marker plane
  coordinates, camera poses. Output format: `tools/glyph/wall-geometry.schema.md` (extra fields OK).
- **solve-sfm**: a feature reconstruction (the splat-worker's sparse.zip, no markers) → the same document
  (planes from the sparse points, gravity / scale / anchor chains; "Solve from features").
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
`checks.levelPairs` (height differences), `checks.borderlineFacetDecisions` (split decisions whose
plane angle is within 0.5° of `foldDeg`), `checks.warnings` (human-readable sentences: a folded
gravity reference, borderline facet decisions), `facetDecisions`, `downweightedMarkers`, `rejectedObservations`, `unusedPhotos`,
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
  this is `n1 × n2`). A reference segment that split into several facets counts ONCE, with its
  whole-segment plane (the facet BA re-run with that segment as one plane, `wallgeometry/refplanes.py`):
  equal votes let a 2-marker end outvote the rest and made the answer jump when a fold crossed
  `foldDeg` (The Attic's kickboard: main wall 45.2° unsplit, 46.5° split; now 45.2° both ways). The
  pieces are reported in `quality.gravityDetail.splitReferences` (markers, observations, extent, angle
  to the whole plane, implied `share`, `leanDeg` against the vertical) and as a warning.
  Sign from the cameras' image-up (phones are held upright). With fewer than two
  independent constraints: `quality.gravity = "unknown"`, the reference facet is treated as vertical,
  angles are `null`, millimetres stay valid.
- **World frame.** x along the reference facet (lowest-index declared non-reference segment, its
  biggest facet), z up, origin = that facet's origin (bottom-left of its markers' bbox).

## Solve from features (`POST /v1/jobs/solve-sfm`, multipart)

Walls without markers (`wallgeometry/sfm/`, design: markerless captures). Input: the splat-worker's
`splat-prepare` result `sparse.zip` (the distorted COLMAP model, intrinsics at each photo's stored resolution,
`stems.json` with every image's stem and role `photo` / `frame` / `anchor`; splat-worker README) plus a request.
Parts: `request` (JSON), `sparse` (the zip, at most `SFM_MAX_SPARSE_MB`), optional `callbackUrl`.

```jsonc
{
  "photos": [ { "name": "p01",                          // the stem in stems.json
                "deviceGravity": [-0.038, -0.987, -0.113],   // optional: iPhone AccelerationVector (g)
                "holds": [[1520.5, 2210.0], ...] } ],      // optional: hold detection centres, stored px
  "segments": [ { "index": 0, "name": "Main wall", "declaredAngleDeg": 45 } ],   // optional angle hints
  "measuredDistance": { "photo": "p05", "a": [x, y], "b": [x, y], "mm": 1234 },  // optional: 2 taps + mm
  "anchors": { "a00": "p05", ... },                    // optional: anchor stem -> reference camera image
  "reference": { ... },                                // the geometry document the anchors are known in
  "dictionary": "DICT_4X4_50", "markerSizeMm": 125,    // optional echoes for the document (no markers used)
  "options": { "cameraHeightMm": 1450, "minFacetAreaM2": 0.4, "seed": 7 }
}
```

Pixel coordinates use the geometry document's OpenCV convention (the top-left pixel's centre is (0, 0)).

**Pipeline.** Points with track >= 3 and error < 2 px, PCA normals (20 neighbours). Sequential RANSAC with local
sampling (3 points within 0.385 D), normal-consistent inliers (<= 25 deg), tolerance 0.0175 D (D = the median
distance from a point to its nearest photo; The Attic: D = 0.91 m -> 16 mm), the largest connected part
(0.33 D cells), Tukey IRLS; near-parallel slabs (< 3 deg) within 30 mm whose footprints touch are one surface.
Then, in mm:
- **Wall-facet decision** per plane: hold hits (each detection cast as a ray; of the planes with points within 80 mm
  of where the ray meets them, the biggest within 150 mm behind the first one gets it, so a hold layer in front of
  its panel does not take the panel's holds; a hit on a feature lying on a facet counts for that facet), camera facing,
  area. Rejected: area < `minFacetAreaM2`, occupying < 40 % of its 1-99 % box (a plane through scattered clutter),
  horizontal (< 20 deg) when gravity is known, hold share < 1 % (with detections; without: not facing the photos),
  score < 0.5 (0.6 holds + 0.25 facing + 0.15 area), and a smaller plane within 40 deg of parallel lying in
  front of a bigger accepted one (> 60 % of its points over the big one's convex outline, within 300 mm: hold
  layers, volume faces). Small or folded facets (kickboard pieces, a 0.7 deg fold) are not separated from sparse
  points: they merge or stay out, for declared / user-corrected geometry (Phase 0).
- **Geometric sanity** of the accepted planes, biggest first (`sanity.py`; the first real markerless run had 8
  facets for 5): a near-coplanar slab (<= 5 deg, <= 60 mm) over a bigger facet is a feature on it whatever its size;
  a plane crossing a bigger facet inside both outlines (>= 10 % of its points > 50 mm on either side) is rejected
  unless it has >= 10 % of the hold hits; a near-parallel plane > 150 mm behind a bigger facet (the room wall past a
  panel), and a plane with no accepted plane (nor the floor) within 150 mm, unless it has >= 3 % (and >= 10 hits).
  Anchored, the reference facets are the prior: a plane on one (<= 5 deg, <= 50 mm, half of its points over its
  extent + 150 mm) is never "floating" (`planes[].referenceFacet`); one on none needs >= 3 % of the hold hits.
- **Gravity**, first that works: `device` (each photo's vector in its stored image's camera frame, portrait
  `(-aX, aY, aZ)` / landscape `(aY, aX, aZ)`, the other holdings by the vector's sign, never the EXIF Orientation;
  robust mean over >= 3 photos) -> `declared` (planes take the nearest declared angle under the prior, the worst
  fits are dropped, >= 2 planes >= 20 deg apart) -> `floor` (the biggest horizontal plane below the cameras) ->
  `cameras` (the image-up prior: `gravityKnown: false`, the reference facet treated as vertical, angles null).
- **Scale**, first that works: `anchors` -> `measured` (both taps cast onto the planes) -> `estimate`: the median
  photo height above the floor (the RANSAC floor plane, else the height histogram of horizontal points below the
  cameras) = 1.45 m, refused unless it puts the photos 0.4-3 m from the wall; else D = 1.0 m.
  `scaleKnown` is false for the estimate (The Attic: -0.1 .. +7.4 %).
- **Anchors** (photos of the active capture, reconstructed with the new ones): a similarity from their model
  camera centres to their centres in `reference` (LMedS start, then 3 x MAD trimming), gate >= 6 anchors, rms
  <= 25 mm, each <= 60 mm; then a rigid plane-ICP of the points onto the reference facets (splat-worker
  refine.py's schedule; beyond 80 mm / 3 deg the anchoring fails). Anchored: the document is in the
  reference's world (its up, its origin) and takes its gravity; failed: the chains above, and a warning.

**Result** (`result.geometry`, file `wall-geometry.json`): the SAME document v1 as `solve` with `markers: []`,
`idScheme: "plan"`, one segment per facet (`"0"` = the reference = the biggest facet; the others by x, then z),
facet extents = the 1-99 % box of its points clipped at the fold lines with adjacent facets, origin at its
corner; cameras = the photos (not frames, not anchors). `world` adds `frameSource: "features"`,
`gravitySource`, `scaleKnown`, `scaleSource`, `anchored`. `quality.sfm`: `points` (total, used,
`medianNearestCameraMm`, `tolMm`, `holdRays`), `images` per role, `planes` (every candidate with its decision
and reason), `anchors` (per-anchor residuals, outliers, ICP), `residuals` (per facet), `gravity`, `scale`.
Deterministic for the same input (`options.seed`).

**The Attic** (353-image model, placed holds as detections, `tests/test_sfm_real.py` with the owner's data):
exactly main wall, side panel and kickboard; main wall 0.09 deg from the marker model, 45.36 deg with the device
gravity (marker model 45.18); anchored on 15 photos: 14.5 mm rms / 28 mm max, ICP 0.28 deg, main wall 45.17 deg.
Without detections, anchored or not, on this model and on the photos-only one (P53): exactly the same three; the
third real markerless capture of the copy wall (`markerless/run3`) too, with its YOLO detections or without (the
old rules took two spurious slabs there without them).

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
| `SOLVE_TIMEOUT_S` / `TEXTURES_TIMEOUT_S` / `SFM_TIMEOUT_S` | 600 / 900 / 900 | per job; the job's process is killed |
| `SFM_MAX_SPARSE_MB` | 256 | solve-sfm: the sparse.zip part (`413` beyond; unpacked at most twice that) |
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
1's PNGs to also run the real-photo texture check, and `BLOCWERK_DATA_DIR` to the owner's data folder (default
`~/blocwerk-data`) to run solve-sfm on the real Attic sparse model, `tests/test_sfm_real.py`; never committed).

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
