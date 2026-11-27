# iPhone scan package, schema `blocwerk-scan/1`

What the companion app uploads for a wall update by iPhone scan, and how. The server checks every manifest with
`ScanManifestValidator` (`src/Blocwerk.Core/Capture/Scan/`) before it takes a byte of the package; its tests
(`test/Blocwerk.Core.Tests/Scan/`) are the reference. `scripts/scan-upload/synthetic_scan.py` builds and uploads a valid
synthetic package.

Conventions are ARKit's, unconverted: world right-handed, Y up = gravity, metres; camera x right, y up, looking down −Z.
Times are seconds on the session clock (`ARFrame.timestamp`).

## Package

```
manifest.json
video/seg0-000.mov         HEVC, 1280×720 up to 3840×2160, about 60 s per piece (.mov or .mp4)
depth/d000123.png          16-bit greyscale PNG, millimetres, 0 = no value
confidence/d000123.png     8-bit greyscale PNG, 0/1/2
mesh/arkit.ply             binary PLY, metres, ARKit world, with classification
```

A file name is one of these four folders and `[A-Za-z0-9_-]{1,64}` plus the folder's extension; nothing else is taken.

## manifest.json

| Field | Type | Rule |
|---|---|---|
| `schemaVersion` | string | `"blocwerk-scan/1"`; anything else is refused on its own |
| `scanId` | uuid | made by the app; the upload's id, reuse it to resume |
| `wallId` | uuid, optional | the server takes the wall from the URL and stores that; when sent, it must be the same |
| `synthetic` | bool, optional | default `false`; a generated test package, stored on the scan and shown as "synthetic" in the wall's scan list |
| `startedAt` | ISO 8601 | |
| `app` | `{version, build}` | version required, ≤ 64 chars |
| `device` | `{model, osVersion}` | e.g. `iPhone17,1`, `26.1` |
| `video` | `{codec, width, height, fps}` | `hevc`; 16:9 or 4:3 (to a pixel) from 1280×720 up to 3840×2160 (so 4:3 up to 2880×2160); fps 1–60 |
| `exposureLock` | `{exposureDurationSeconds, iso, whiteBalanceRed, whiteBalanceGreen, whiteBalanceBlue, lensPosition, lockedAt}` | the lock of the whole recording; duration 0–1 s, ISO > 0, gains 1–16, lens 0–1, `lockedAt` session time. Still accepted; required unless every segment has its own `exposureLock` |
| `wallBox` | `[[x,y,z] ×3]`, optional | the two floor ends of the wall and a point on its top edge, within 100 m. Older apps send it; the app now sends `referencePlane` instead |
| `referencePlane` | `{point: [x,y,z], normal: [x,y,z]}`, optional | the wall's plane: a point on it within 100 m and its normal, finite and not of zero length (any length; the server normalises it) |
| `wallBoxReach` | `{frontMeters, backMeters}`, optional | how far the wall reaches in front of and behind its plane (overhangs), each 0–20 m |
| `coverage` | `{percentGreen, patchSizeMeters, bins}`, optional | the app's coverage summary: `percentGreen` 0–100, `patchSizeMeters` > 0 and ≤ 10, `bins` an object of patch counts ≥ 0 by viewing direction: `frontal`, `fromLeft`, `fromRight`, `fromAbove`, `fromBelow`, `lookingUp` (other names are accepted; at most 32 bins, names ≤ 64 chars) |
| `segments[]` | `{id, startTime, endTime, videos[]: {file, startTime}, exposureLock?}` | 1–64, unique ids, start < end, ≤ 30 min, at least one video piece each. `exposureLock` optional: the same fields as the top-level one plus the flags `exposureLocked`, `whiteBalanceLocked`, `focusLocked` (bools, optional), checked like it |
| `frames[]` | `{t, segment, pose[16], intrinsics: {fx, fy, cx, cy}, tracking, camera?}` | every video frame, 1–36 000; `t` inside its segment and increasing; `pose` column-major camera-to-world, rigid (orthonormal, det +1, bottom row 0 0 0 1), within 100 m; intrinsics in px of the declared `video` size (focal 0.3–3 × width, fx ≈ fy within 5 %, principal point in the middle half); `tracking` `normal`, `limited` or `notAvailable`; `camera` optional `{lensPosition, iso, exposureDurationSeconds, whiteBalanceGains: [r, g, b]}`, the frame's own settings to verify the lock held (same ranges as the lock) |
| `depth[]` | `{t, segment, file, confidenceFile, width, height}` | at a lower rate than the frames, at least one, no more than there are frames; sides 1–1024 px |
| `mesh` | `{file}` | `mesh/*.ply` |
| `files[]` | `{name, bytes, sha256}` | 1–20 000 files, sha256 lowercase hex; video ≤ 1 GiB, depth/confidence ≤ 2 MiB, mesh ≤ 512 MiB, package ≤ 4 GiB; every file used by the manifest and every used file listed |

## Upload

All under `/api/walls/{wallId}/scans`, JSON, `Authorization: Bearer bwk_…` (a personal API key with write access) or the
browser session. Only the wall's owner, admins and moderators, and administrators of the installation, may upload; the
wall must have "iPhone scan" switched on in its settings. Wall keys, kiosk keys, share links and visitors are refused.
Per user and key (or browser session) 200 requests a second with bursts of 4000, enough for a phone sending about 20
files at a time with retries; 429 with `Retry-After: 1` beyond. The `POST`s need `Content-Type: application/json` (an
empty or `{}` body for the commit); anything else is 415.

Only the user who began a scan, or an administrator of the installation, may begin it again, send its files or commit
it (403 otherwise). Received scans are kept until processing applies them, so a wall may hold 3 received scans that are
not applied yet, and a user may commit 20 GB of scans in any 30 days over all walls (`Blocwerk:Scan:MaxWaitingPerWall`,
`MaxGbPerUser`, `UserWindowDays`); a begin, or a commit that would go over, is 409 until a site admin removes a waiting
scan (the remove button in the wall's settings scan list). A user may have 2 uploads open at a time, a wall 4; another begin is 409 until one is committed or
removed. Free space is checked at the begin and on every `PUT` (409 when the server would run short). The files of one
scan may be sent in parallel.

| Call | Answer |
|---|---|
| `POST /` with the manifest (≤ 16 MB; the app's ten minutes at 30 fps are about 11 MB) | 200 with the state; 400 with `problems[]` for an invalid manifest; 403/409 for a caller who may not upload or a wall not set to iPhone scan, before the manifest is read; 409 when the `scanId` is taken or a limit below is reached; 429 while another begin of the same user runs |
| `PUT /{scanId}/files/{name}` raw body, optional `X-Content-SHA256` | 200 with `{scanId, name, bytes, status: "received"}` for that file only; a file already received is not read again; 422 for wrong bytes or hash; 413 when longer than the manifest says |
| `POST /{scanId}/commit` | 200 with status `received`; 409 with the missing files in `problems[]` |
| `GET /{scanId}` | 200 with the state |
| `DELETE /{scanId}` | site admins only: removes a received scan and its files (200); 409 for an upload still open |

The state is `{scanId, wallId, status: "uploading"|"received", stage, missing[], totalFiles, totalBytes, receivedBytes}`.
After an interruption, `POST` the same manifest again and send exactly `missing[]`. A received scan shows on the wall's
3D capture list as "Received, waiting for processing". An upload left unfinished for the import retention period
(`ImportStagingDays`, default 3 days) is removed by the capture retention, even while retention is a dry run (it is the
server's own staging, not anybody's data); a received scan, and the point cloud that
processing will keep in its folder (`derived/point-cloud.ply`), are not.
