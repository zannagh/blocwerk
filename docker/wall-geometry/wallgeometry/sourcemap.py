"""Per-facet source-view map: which photo each part of a facet texture was painted from.

A protruding hold shows in the texture as its silhouette projected onto the facet plane FROM THE PHOTO
that painted that spot, i.e. shifted away from that camera by about protrusion * tan(view angle). A
viewer that knows the hold's 3D shape and this map can draw the hold's outline where the texture shows
it. The map is the renderer's own photo choice on its label grid (`labelCellPx` texture pixels per
cell), so it is low-resolution and costs next to nothing.

Document (`facet_<id>_source.json`, manifest field `sourceFile`):
  {"version": 1, "cellMm": 16.0, "aMin": ..., "bMax": ..., "cols": cw, "rows": ch,
   "cameras": ["IMG_2787", ...], "bits": 8, "cells": "<base64>"}
`cells` is row-major, row 0 at the top (b = bMax), one unsigned integer per cell (8 bit, or 16 bit
little-endian when more than 255 photos are used): 0 = no photo, k = cameras[k - 1]. Cell (i, j) covers
a in [aMin + i * cellMm, aMin + (i + 1) * cellMm), b in (bMax - (j + 1) * cellMm, bMax - j * cellMm]; the
last row / column may reach past the texture. Near a seam the texture is feathered over a few pixels
between the two photos; the map names the one the renderer chose.
"""
import base64
import json

import numpy as np


def from_cells(cell_lab, names, g, cell_px):
    """cell_lab (ch, cw) photo index into `names` (-1 = none) -> the map document."""
    used = [int(k) for k in np.unique(cell_lab) if k >= 0]
    lut = np.zeros(len(names) + 1, np.int64)
    for i, k in enumerate(used):
        lut[k + 1] = i + 1
    vals = lut[cell_lab.astype(np.int64) + 1]
    bits = 8 if len(used) <= 255 else 16
    raw = vals.astype(np.uint8 if bits == 8 else "<u2").tobytes()
    return {"version": 1, "cellMm": round(float(cell_px * g["res"]), 4), "aMin": round(float(g["aMin"]), 3),
            "bMax": round(float(g["bMax"]), 3), "cols": int(cell_lab.shape[1]), "rows": int(cell_lab.shape[0]),
            "cameras": [names[k] for k in used], "bits": bits, "cells": base64.b64encode(raw).decode("ascii")}


def decode(doc):
    """The map document -> (ch, cw) array of 1-based indices into doc['cameras'] (0 = none)."""
    dt = np.uint8 if doc["bits"] == 8 else np.dtype("<u2")
    return np.frombuffer(base64.b64decode(doc["cells"]), dt).reshape(doc["rows"], doc["cols"])


def best_cells(W):
    """Blend mode: per cell the photo with the highest blend weight (-1 where none)."""
    return np.where((W > 0).any(0), W.argmax(0), -1)


def encode(doc):
    return json.dumps(doc, separators=(",", ":")).encode("utf-8")
