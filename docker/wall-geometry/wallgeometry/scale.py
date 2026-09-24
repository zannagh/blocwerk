"""Texture constants that are sizes ON THE WALL, kept physical whatever `mmPerPx` a job asks for.

The pixel values in textures.DEFAULTS were tuned at the default 2 mm/px. The ones listed here are
really lengths on the facet plane (the source-map cell is ~16 mm, a seam is softened over ~10 mm, ...),
so a finer texture (1 mm/px) needs twice the pixels for the same look, and a coarser one fewer. At
2 mm/px every value comes out exactly as the pixel default, so the output there is unchanged.

Not scaled, on purpose: counts of label cells (`modeFilterCells`, `selectModeFilterCells`,
`consensusBlurCells`, `gainMinOverlapCells`: the cells themselves stay 16 mm), sizes in PHOTO pixels
(`imageMarginPx`, `borderRampPx`), constants already in mm (`seam*Mm`, `flatten*Mm`), and the memory
tiling (`combineTileRows`).
"""

REFERENCE_MM_PER_PX = 2.0

# key -> size on the wall in mm (= the pixel default x REFERENCE_MM_PER_PX)
PHYSICAL_MM = {
    "labelCellPx": 16.0,  # label / source-map cell
    "maskFeatherPx": 8.0,  # coverage-mask ramp into the covered area
    "seamFeatherPx": 10.0,  # "select" mode: seam between two photos softened over this
    "outlierBlurPx": 6.0,  # "blend" mode: Lab blur before the outlier test
    "outlierSmoothPx": 22.0,  # "blend" mode: neighbourhood the keep / drop decision is smoothed over
    "flattenDownscale": 16.0,  # work cell of the shading flattening (flatten.py)
    "seamDownscale": 8.0,  # work cell of the seam harmonisation (seams.py)
}


def at_resolution(p, given=None):
    """p (DEFAULTS merged with the job's params) with every PHYSICAL_MM key converted to pixels at
    p["mmPerPx"]. A key the caller set itself (`given`) is kept as it is. Integer keys stay integers
    (>= 1)."""
    given = given or {}
    res = float(p["mmPerPx"])
    out = dict(p)
    for key, mm in PHYSICAL_MM.items():
        if key in given or key not in p:
            continue
        px = mm / res
        out[key] = max(1, int(round(px))) if isinstance(p[key], int) else px
    return out
