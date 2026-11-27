# iPhone scan package, schema `blocwerk-scan/1`

What the companion app uploads for a wall update by iPhone scan, and how. The server checks every manifest with
`ScanManifestValidator` (`src/Blocwerk.Core/Capture/Scan/`) before it takes a byte of the package; its tests
(`test/Blocwerk.Core.Tests/Scan/`) are the reference.

Conventions are ARKit's, unconverted: world right-handed, Y up = gravity, metres; camera x right, y up, looking down −Z.
Times are seconds on the session clock (`ARFrame.timestamp`).

## Package

```
manifest.json
video/seg0-000.mov         HEVC, 3840×2160, about 60 s per piece (.mov or .mp4)
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
| `wallId` | uuid | the wall in the URL |
| `startedAt` | ISO 8601 | |
| `app` | `{version, build}` | version required, ≤ 64 chars |
| `device` | `{model, osVersion}` | e.g. `iPhone17,1`, `26.1` |
| `video` | `{codec, width, height, fps}` | `hevc`, `3840`, `2160`, 1–60 |
| `exposureLock` | `{exposureDurationSeconds, iso, whiteBalanceRed, whiteBalanceGreen, whiteBalanceBlue, lensPosition, lockedAt}` | locked at the start of recording; duration 0–1 s, ISO > 0, gains 1–16, lens 0–1, `lockedAt` session time |
| `wallBox` | `[[x,y,z] ×3]` | the two floor ends of the wall and a point on its top edge, within 100 m |
| `segments[]` | `{id, startTime, endTime, videos[]: {file, startTime}}` | 1–64, unique ids, start < end, ≤ 30 min, at least one video piece each |
| `frames[]` | `{t, segment, pose[16], intrinsics: {fx, fy, cx, cy}, tracking}` | every video frame, 1–36 000; `t` inside its segment and increasing; `pose` column-major camera-to-world, rigid (orthonormal, det +1, bottom row 0 0 0 1), within 100 m; intrinsics in px of the 3840×2160 frame (focal 0.3–3 × width, fx ≈ fy within 5 %, principal point in the middle half); `tracking` `normal`, `limited` or `notAvailable` |
| `depth[]` | `{t, segment, file, confidenceFile, width, height}` | at a lower rate than the frames, at least one, no more than there are frames; sides 1–1024 px |
| `mesh` | `{file}` | `mesh/*.ply` |
| `files[]` | `{name, bytes, sha256}` | 1–20 000 files, sha256 lowercase hex; video ≤ 1 GiB, depth/confidence ≤ 2 MiB, mesh ≤ 512 MiB, package ≤ 4 GiB; every file used by the manifest and every used file listed |
