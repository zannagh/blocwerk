# o7-measure: memory peaks of real wall-geometry jobs

Replays the requests the app sends to wall-geometry (`solve`, `solve-sfm`, `textures` per quality) in a throwaway
container per case and reads the container's memory (O7: wall-geometry must fit 1.5 GiB on the VPS).

| file | what |
|---|---|
| `export.sql` | one capture (declarations, active model, photos with their markers) as JSON, read-only |
| `build_requests.py` | that JSON → `solve.json` (as `CaptureComputeDocuments.BuildSolveRequest`), `geometry.json` (carried facets left out, as `SubmitTexturesAsync`), `photos.json`, `qualities.json` (`TextureQualityPresets`) |
| `replay.py` | runs INSIDE the container (stdlib only): submits, polls, prints one JSON line with the sampled anonymous-memory peak, `memory.peak`, `memory.max` and the OOM-kill count. `--self-check` tests its multipart writer |
| `measure.ps1` | the case loop on the experimental PC: `docker run --network none --cpus 2 [--memory]`, `docker exec … replay.py`, `docker rm -f` |

`anonPeakMiB` (sampled every 0.2 s) is what the processes hold, including the service's parent (~80 MiB) and this client
(a few MiB). `memory.peak` also counts the page cache the uploaded photos fill; it is reclaimable and reaches any limit,
so it is not the number to size by.

## Results (2026-10-28, experimental PC, image `blocwerk-exp/wall-geometry:staging-51f7f58`)

Real data: The Attic, capture `bbb2a0cf` (781 photos, 719 solved cameras, 3127 marker observations); markerless
`solve-sfm` from `markerless/run3` (353 images, 37 MB sparse.zip). 2 CPUs per container.

| case | blend budget | limit | anon peak | time | outcome |
|---|---|---|---|---|---|
| solve (719 photos) | – | 1536m | 0.24 GiB (memory.peak 0.40 GiB) | > 25 min in free BA | still running at the time of writing; memory flat |
| solve-sfm (353 images) | – | none / 1536m | 0.49 GiB | 32–36 s | ok |
| textures standard | 2 GB (old default) | none | 3.14 GiB | 242 s | ok; **would not fit 1.5 GiB** |
| textures high | 2 GB | none | 3.50 GiB | 465 s | ok; would not fit |
| textures maximum | 2 GB | none | 3.47 GiB | 395 s | ok; would not fit |
| textures standard | 600 MB | 1536m | 0.54 GiB | 75 s | ok |
| textures high | 600 MB | 1536m | 0.96 GiB | 97 s | ok |
| textures maximum | 600 MB | 1536m | 1.10 GiB | 104 s | ok |
| textures standard / high / maximum | 900 MB | 1536m | 0.72 / 0.88 / 1.28 GiB | 86–94 s | ok, maximum too close to the guard |
| textures standard, 144-photo subset | 700 MB / 2 GB | none | 0.99 / 1.17 GiB | 59 / 66 s | ok |
| **guard**: textures high, `JOB_MAX_MEMORY_MB=1280` | 2 GB | 1536m | stopped at 1.37 GiB | 46 s | `failed`, `failureKind: resource`, no OOM kill, service alive |
| **guard**: textures maximum, `JOB_MAX_MEMORY_MB=1280` | 600 MB | 1536m | 1.09 GiB | 88 s | ok |

Chosen for the VPS: `mem_limit: 1536m`, `TEXTURES_BLEND_MAX_BYTES=600000000`, `JOB_MAX_MEMORY_MB=1280`. With 719 photos
the photos' views alone take ~1.1 GB, so at this budget The Attic blends single-view at every quality; the app sees the
reported budget (`texturesBlendMaxMb`) and routes a re-render it cannot blend in full to a 3D runner. `solve-sfm` stays
on the VPS (0.5 GiB, far below the 1.2 GiB threshold of the O7 plan's PR 8).

## Run it

```powershell
# on the PC, in a scratch folder holding these scripts plus req\ (build_requests.py output) and sfm\ (request.json, sparse.zip)
pwsh measure.ps1 -Image blocwerk-exp/wall-geometry:<tag> -Data <that folder> -Photos C:\Users\patri\blocwerk-data\wall-images\captures `
     -Memory 1536m -BlendBytes 600000000 -Cases "solve,solve-sfm,textures:standard,textures:high,textures:maximum"
```

Each case gets its own container (`o7m-<tag>-<case>`, removed afterwards); running containers are never touched.
`-Computejobs <dir>` mounts a `computejobs/` over the image's to try a guard change without building an image.
