# Blocwerk compute job protocol v1

How the Blocwerk app talks to its compute workers. One protocol, several implementations:

| kind | served by | where |
|---|---|---|
| `solve` | `wall-geometry` | `docker/wall-geometry/` (CPU, runs next to the app) |
| `textures` | `wall-geometry` | same container |
| `splat` | `splat-worker` (separate, GPU) | NOT in `wall-geometry`; may run on an external machine |
| `splat-prepare` | `splat-worker` (CPU half) | photos -> training bundle for a 3D runner (`SPLAT_WORKER_MODE=cpu` needs no GPU) |
| `splat-finish` | `splat-worker` (CPU half) | a runner's trained scene -> the same files as `splat` |

The app knows each worker only as **a base URL plus an optional API key**. Whether the worker runs
beside the app in the same compose network or on a GPU box elsewhere changes nothing in the protocol.
A worker that does not serve a kind answers `POST /v1/jobs/{kind}` with `501` (wall-geometry does
this for `splat`) or `404` (kind it has never heard of).

Protocol identifier: `blocwerk-compute/1` (reported by `/health`).

## Auth

- If the worker's env `COMPUTE_API_KEY` is set, **every endpoint except `GET /health`** requires
  `Authorization: Bearer <key>`. Missing or wrong key → `401` with `WWW-Authenticate: Bearer`.
  Workers compare in constant time and never log the key.
- Workers refuse to **start** listening beyond loopback without a key (exit code 2) unless
  `ALLOW_OPEN_BIND=1` says the port is only reachable on a private network; without a key they log a
  warning. In the compose file both workers are opt-in profiles (`compute`, `gpu`) and take their keys from
  `docker/.env` (`COMPUTE_API_KEY`, `SPLAT_API_KEY`); a worker started without one exits instead of
  running open.
- External workers (reached over the internet) MUST set a key and SHOULD sit behind TLS.
- The key and callback secret are removed from the worker's environment at startup: job processes and
  the tools they run (COLMAP, Brush, ...) get a minimal allow-listed environment.
- There are no `/docs`, `/redoc` or `/openapi.json` endpoints (this document is the spec).

## Endpoints

### `GET /health` (never authenticated)

Only what a probe needs; nothing about paths, tools, limits or the queue:

```json
{ "status": "ok", "service": "wall-geometry", "protocol": "blocwerk-compute/1",
  "version": "0.1.0", "kinds": ["solve", "textures"] }
```

`status` is `"degraded"` when the worker is up but cannot run jobs (e.g. splat tools missing).

### `GET /v1/info` (authenticated)

`/health`'s fields plus `gitSha`, `auth`, `jobs {queued, running}` and per-worker details (limits;
the splat worker's tool paths/versions and GPU backend).

### `POST /v1/jobs/{kind}` → `202 { "jobId": "…", "status": "queued" }`

The body format is per kind (below). Every kind accepts an optional **`callbackUrl`** (a top-level
JSON field for JSON bodies, a form field for multipart bodies).

Errors: `400` malformed body (incl. JSON `NaN`/`Infinity`, which are refused everywhere) or a refused
`callbackUrl`, `401` auth, `404` unknown kind, `413` body / photo / count / pixel count too large,
`422` well-formed but invalid input (the `detail` says what; unknown or out-of-range options included),
`429` queue full (retry later), `501` kind served by a different worker.

Photos are read by a streaming multipart parser and cleaned in memory; the uploaded bytes never reach
the worker's disk. Any photo whose header claims more than `MAX_IMAGE_MEGAPIXELS` (default 100) is
refused before it is decoded.

### `GET /v1/jobs/{id}` → job status

```jsonc
{
  "jobId": "4f0c…", "kind": "solve",
  "status": "queued | running | succeeded | failed | cancelled",
  "progress": 0.65,              // 0..1, monotone within a run; 1.0 on success
  "stage": "facet bundle adjustment",   // short text, for a progress label
  "stageDetail": "step 1200/5000",       // optional finer detail within the stage (e.g. "11/14 images registered")
  "message": null,               // human-readable note (e.g. why it was cancelled)
  "error": null,                 // set iff status == failed; short, no server paths (<= 400 chars);
                                 // unexpected failures are "internal error (ref <id>)", details in the worker log
  "createdAt": "2026-09-23T10:00:00+00:00", "updatedAt": "…",
  "result": null                 // set iff status == succeeded; per kind (below)
}
```

`result.files` (when a kind produces files) is a list of `{ "name", "url" }`, where `url` is the
path of the file endpoint, relative to the worker's base URL.

### `GET /v1/jobs/{id}/files/{name}` → the file (`image/jpeg`, `application/json`, …)

### `DELETE /v1/jobs/{id}` → job status (cancel)

A queued job becomes `cancelled` at once; a running one is killed and becomes `cancelled` within
about a second. Deleting a finished job is a no-op that returns its status.

### Unknown or expired job → `404`

## Lifetime

Workers are single-instance with in-memory state unless they say otherwise. **Results and files
expire `RESULT_TTL_S` after the job finishes (default 3600 s)**, and everything is lost on restart:
the app must fetch what it needs promptly and treat `404` on a job it knew as "expired or worker
restarted" (re-submit).

## Progress callbacks (optional)

If a job was created with `callbackUrl`, the worker POSTs the job status JSON (exactly the
`GET /v1/jobs/{id}` body) to it **on every status change** (`queued`, `running`, and the terminal
one). Headers:

```
Content-Type: application/json
X-Blocwerk-Signature: sha256=<hex HMAC-SHA256 of the raw request body, key = COMPUTE_CALLBACK_SECRET>
```

- The receiver MUST verify the signature over the raw body bytes (constant-time compare) before
  trusting anything in it. Reference: `docker/wall-geometry/service/security.py` (`sign`, `verify`).
  Test vector: body `{"jobId":"abc","status":"succeeded"}`, secret `s3cret` →
  `sha256=8498b6c0d99f7e79edbb127d423ecbc4aeec6cf6696247b164d40e527b298e60`.
- Best effort: a few retries with back-off (wall-geometry: 1 s, 3 s, 9 s; 5 s timeout each), from a
  background thread; a failing callback never delays or fails the job. Order is not guaranteed
  across retries, so use `updatedAt`/`status` rather than arrival order.
- If `COMPUTE_CALLBACK_SECRET` is unset the worker sends **no** callbacks (they could not be
  verified) and logs that once.
- **Polling stays the primary path.** An external worker may not be able to reach the app at all;
  callbacks only make the progress bar snappier when it can.
- **SSRF guard.** Only `http`/`https` URLs without credentials. The host is resolved once, every
  address must be allowed, and the POST goes to that pinned address (DNS rebinding cannot redirect it;
  TLS still verifies the name). Never allowed: link-local / cloud metadata (`169.254.0.0/16`,
  `fe80::/10`, `fd00:ec2::254`, `100.100.100.200`, `168.63.129.16`), unspecified, multicast, reserved.
  Loopback / private / CGNAT / unique-local only with `CALLBACK_ALLOW_PRIVATE=1` or for a host listed
  in `CALLBACK_ALLOWED_HOSTS` (the compose file lists `blocwerk`). Redirects are not followed (a 3xx
  is a failed attempt). An IP-literal URL that is not allowed is a `400` at submission; a name that
  resolves to a forbidden address is dropped at send time (logged).

## Kinds

### `solve` (wall-geometry)

`POST /v1/jobs/solve`, `Content-Type: application/json`, body = the solve request document
(contract in `docker/wall-geometry/README.md`). `result.geometry` = the wall-geometry document
(`tools/glyph/wall-geometry.schema.md`); also downloadable as file `wall-geometry.json`.

### `textures` (wall-geometry)

`POST /v1/jobs/textures`, `multipart/form-data`:

| part | content |
|---|---|
| `geometry` | a solved wall-geometry document (JSON, as file or field) |
| `photos` | one JPEG or PNG per photo, **file name = the camera's `image` name** + `.jpg`/`.jpeg`/`.png`; size must equal the solved camera's. Metadata is stripped on arrival without re-encoding (pixels untouched) |
| `options` | optional JSON: `mmPerPx` (2.0; 0.25–50), `maxSidePx` (4096; 256–8192), `extraMarginMm` (100; 0–2000), `jpegQuality` (90; 30–100). Unknown keys → 422 |
| `callbackUrl` | optional |

`result.facets[]`: `{ facet, file, widthPx, heightPx, mmPerPx, bounds {aMin,aMax,bMin,bMax},
photosUsed {image: fraction}, coverage, markerCheck }`; files `facet_<id>.jpg` + `textures.json`.

### `splat` (splat-worker, separate)

Not implemented by wall-geometry (it answers `501`). A separate `splat-worker` implements this same
protocol (same auth, status shape, files, cancel, TTL, callbacks); its request/result are defined
with that worker. One convention crosses the boundary: a `photos` file whose name starts with `vf_`
(`vf_0001.jpg`, … in video order) is an **auxiliary video frame**: trained on, never used to align
the result with the geometry and never counted towards the photo minimum.

### `splat-prepare` / `splat-finish` (splat-worker, the split for 3D runners)

The same job split in two CPU halves around the GPU training, which a **3D runner** does elsewhere
(the runner pulls work from the app; it never talks to this worker). `splat-prepare` takes exactly the
`splat` request and returns `bundle.zip` (undistorted, metadata-free images + COLMAP sparse model +
`train.json`: what the runner trains) and `prepared.json` (the state the finish needs; server-only).
`splat-finish` takes multipart `prepared` (that JSON), `splat` (one file `splat.ply` or `splat.spz`,
the trained scene; `SPLAT_MAX_RESULT_MB`, default 2048) and optional `trainStats` (JSON), and returns
the same `wall.splat` / `wall.spz` / `frame.json` as `splat`. Details: `splat-worker/README.md`.
The app uses the split only when the worker's `/health` lists **both** kinds in `kinds`; otherwise it
keeps sending `splat`. An optional `/health` field `maxQuality` (`"ultra"` when the worker trains ultra
itself) lets the app offer the Ultra quality without a 3D runner.
