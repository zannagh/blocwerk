# Glyph wall-geometry: capture-1 CLI

The solver itself lives in **`docker/wall-geometry/wallgeometry/`** (single source of truth, also what
the `wall-geometry` service container runs). This directory only holds what is specific to capture 1:

| file | what |
|---|---|
| `data.py` | builds capture 1's **request document** (the same JSON the app POSTs) from `../detections.json`, `../id1_recovered.json`, `../exif.json` and the refined-corner cache. Segment names, declared angles and vertical references are declared here as request data; the solver hardcodes nothing about this wall |
| `refine.py` | CLI-only: loads a photo by name and calls `wallgeometry.refine` (edge-line corner refinement, centroid peak by default; `method="parabola"` reproduces the old results). The app sends corners already refined by its C# port |
| `solve.py` | the CLI |
| `figures.py` | debug figures (matplotlib) |

## Run

```bash
python3 -m venv .venv && .venv/bin/pip install -r requirements.txt   # .venv is git-ignored
.venv/bin/python solve.py                      # -> out/request.json + out/wall-geometry.json (~20-30 s)
.venv/bin/python solve.py --level-pairs 14,15  # owner: 14 and 15 are level (extra gravity constraint)
.venv/bin/python solve.py --validate --figures # + leave-one-photo-out and out/fig_*.png
.venv/bin/python solve.py --request some.json  # any request document
```

Corner refinement needs the full-res PNGs, but only once: it reads them from `tools/glyph/png/` (the
folder `detect.py` uses), or from `GLYPH_PNG_DIR` when set. The refined
corners are cached in `out/refined_corners.json`; later runs don't need the PNGs.

`out/report.md` is the narrative report of the original (pre-service) solver and is no longer
regenerated; its numbers were reproduced by the generic path (see `docker/wall-geometry/README.md`).
